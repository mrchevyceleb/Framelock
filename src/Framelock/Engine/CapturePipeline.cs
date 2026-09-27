using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelock.Audio;
using Framelock.Capture;
using Framelock.Core;
using Framelock.Encoding;
using Framelock.Graphics;
using Vortice.Mathematics;

namespace Framelock.Engine;

/// <summary>Everything that needs a pipeline rebuild when it changes.</summary>
public sealed record PipelineConfig(IntPtr Monitor, long AdapterLuid, int Width, int Height, int Fps, bool SeparateAudioTracks, int AudioBitrateKbps)
{
    public override string ToString() => $"{Width}x{Height}@{Fps}";
}

public sealed record PipelineStats(double OutputFps, double CaptureFps, long Frames, long Lagged, long EncoderDropped, double VideoMbps,
                                   int SourceWidth, int SourceHeight, bool Hdr, bool Encoding, string? EncoderLabel)
{
    /// <summary>Average / worst time the video thread spent on one frame (capture + compose + encode submit).</summary>
    public double WorkMsAvg { get; init; }
    public double WorkMsMax { get; init; }
}

/// <summary>
/// The real-time core. A dedicated MMCSS thread paced by a high-resolution timer produces exactly <c>Fps</c> frames per second:
/// capture → GPU composite (scale + HDR tone-map + overlays) → NV12 → hardware encoder, plus a throttled preview.
/// Constant frame rate output: frame N has pts N, so audio (placed on the same QPC timeline) stays locked to it.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private static readonly long Freq = Stopwatch.Frequency;

    public PipelineConfig Config { get; }
    public D3DContext D3D { get; }
    public PacketHub Hub { get; } = new();
    public AudioEngine Audio { get; }
    public long T0 { get; }
    public int Fps => Config.Fps;

    private readonly AppSettings _settings;
    private readonly Compositor _comp;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _commands = new();
    private volatile bool _stop;

    private WgcSource? _source;
    private CaptureTarget? _target;
    private volatile CaptureTarget? _requestedTarget;
    private bool _targetDirty;

    private VideoEncoder? _encoder;
    private StreamInfo? _videoStream;
    private readonly List<StreamInfo> _audioStreams = new();
    private readonly List<AudioEncoder> _audioEncoders = new();
    private volatile int _forceKey;
    private long _frameIndex;

    // preview (triple-swap under a tiny lock)
    private byte[] _previewWork, _previewShared, _previewUi;
    private bool _previewFresh;
    private readonly object _previewLock = new();
    public volatile bool PreviewEnabled = true;
    public int PreviewWidth => _comp.PreviewWidth;
    public int PreviewHeight => _comp.PreviewHeight;

    public volatile OverlayState[] Overlays = Array.Empty<OverlayState>();
    /// <summary>Timeline µs that interval overlays count from (recording start).</summary>
    public long OverlayClockOriginUs;

    private string? _bgText;
    private Color4 _bg = new(0, 0, 0, 1);

    // stats
    private long _statWorkTicks, _statWorkMax, _statLoops;
    private long _lagged, _statFrames, _statCaptured, _lastStatTicks, _lastStatBytes, _consecutiveErrors;
    private PipelineStats _stats = new(0, 0, 0, 0, 0, 0, 0, 0, false, false, null);
    public PipelineStats Stats => _stats;

    public event Action<string>? SourceLost;
    public event Action<string>? EncoderFailed;
    public event Action<Exception>? Fatal;

    public IReadOnlyList<StreamInfo> Streams => _videoStream == null ? _audioStreams : new[] { _videoStream }.Concat(_audioStreams).ToList();
    public bool EncoderRunning => _encoder != null;
    public string? EncoderLabel => _encoder?.Info.Label;
    public long CurrentFrame => Interlocked.Read(ref _frameIndex);
    public long FrameTimeUs(long frame) => frame * 1_000_000L / Config.Fps;
    public long NowUs => FrameTimeUs(CurrentFrame);

    public CapturePipeline(PipelineConfig config, AppSettings settings)
    {
        Config = config;
        _settings = settings;

        var adapter = D3DContext.FindAdapterByLuid(config.AdapterLuid) ?? D3DContext.FindAdapterForMonitor(config.Monitor) ?? D3DContext.FindBestAdapter();
        D3D = D3DContext.Create(adapter);
        try
        {
            _comp = new Compositor(D3D, config.Width, config.Height);
            int pBytes = _comp.PreviewWidth * _comp.PreviewHeight * 4;
            _previewWork = new byte[pBytes];
            _previewShared = new byte[pBytes];
            _previewUi = new byte[pBytes];

            T0 = Stopwatch.GetTimestamp();
            _audioEncoders.Add(new AudioEncoder(1, config.SeparateAudioTracks ? "Mix (game + mic)" : "Audio", config.AudioBitrateKbps, Hub.Publish));
            if (config.SeparateAudioTracks)
            {
                _audioEncoders.Add(new AudioEncoder(2, "Desktop audio", config.AudioBitrateKbps, Hub.Publish));
                _audioEncoders.Add(new AudioEncoder(3, "Microphone", config.AudioBitrateKbps, Hub.Publish));
            }
            unsafe
            {
                foreach (var ae in _audioEncoders)
                    _audioStreams.Add(new StreamInfo(ae.Track, ae.Name, ae.Context, new FFmpeg.AutoGen.AVRational { num = 48000, den = 1 }));
            }
            Audio = new AudioEngine(T0, settings, _audioEncoders[0], _audioEncoders.ElementAtOrDefault(1), _audioEncoders.ElementAtOrDefault(2));
        }
        catch
        {
            _comp?.Dispose();
            D3D.Dispose();
            throw;
        }

        _thread = new Thread(VideoLoop) { Name = "Framelock video", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        Log.Info($"Pipeline started {config} on {D3D.AdapterName}");
    }

    // ------------------------------------------------------------------ control (any thread)

    public void SetTarget(CaptureTarget? target) { _requestedTarget = target; _targetDirty = true; }

    /// <summary>Forces a keyframe on the next frame and returns the timeline time (µs) cuts should use.</summary>
    public long RequestKeyframe()
    {
        long t = NowUs;
        _forceKey = 1;
        return t;
    }

    private Task RunOnVideoThread(Action a)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            try { a(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Starts the video encoder (off the video thread, so capture doesn't hitch) if it isn't running.</summary>
    public async Task EnsureEncoderAsync(EncoderInfo info, VideoEncoderSettings settings)
    {
        if (_encoder != null) return;
        var enc = await Task.Run(() => new VideoEncoder(info, D3D, Config.Width, Config.Height, Config.Fps, settings, _comp.Nv12Supported, Hub.Publish));
        StreamInfo si;
        unsafe { si = new StreamInfo(0, "Video", enc.Context, new FFmpeg.AutoGen.AVRational { num = Config.Fps, den = 1 }); }
        await RunOnVideoThread(() =>
        {
            if (_encoder != null) { enc.Dispose(); si.Dispose(); return; }
            _videoStream?.Dispose();
            _videoStream = si;
            _encoder = enc;
            _forceKey = 1;
        });
    }

    /// <summary>Flushes and closes the video encoder (nothing is recording or buffering anymore).</summary>
    public Task StopEncoderAsync() => RunOnVideoThread(() =>
    {
        var enc = _encoder;
        if (enc == null) return;
        _encoder = null;
        try { enc.Flush(); } catch (Exception ex) { Log.Warn("Encoder flush: " + ex.Message); }
        enc.Dispose();
    });

    public Task<string> TakeScreenshotAsync(string path)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            try
            {
                var px = _comp.ReadCompositeFull();
                int w = _comp.Width, h = _comp.Height;
                Task.Run(() =>
                {
                    try
                    {
                        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bmp));
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using (var fs = File.Create(path)) encoder.Save(fs);
                        tcs.TrySetResult(path);
                    }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Latest preview frame (BGRA, PreviewWidth×PreviewHeight) or null when nothing new since the last call.</summary>
    public byte[]? TakePreview()
    {
        lock (_previewLock)
        {
            if (!_previewFresh) return null;
            (_previewShared, _previewUi) = (_previewUi, _previewShared);
            _previewFresh = false;
            return _previewUi;
        }
    }

    // ------------------------------------------------------------------ video thread

    private void VideoLoop()
    {
        Native.timeBeginPeriod(1);
        uint taskIndex = 0;
        var mmcss = Native.AvSetMmThreadCharacteristics("Games", ref taskIndex);
        using var timer = new HighResTimer();
        long frameTicks = Freq / Config.Fps;
        long n = 0;
        _lastStatTicks = Stopwatch.GetTimestamp();
        try
        {
            while (!_stop)
            {
                long target = T0 + (long)((Int128)n * Freq / Config.Fps);
                timer.WaitUntil(target);
                long behind = Stopwatch.GetTimestamp() - target;
                if (behind > frameTicks * 2)
                {
                    // Fell behind (GPU saturated / system stall): skip the missed slots instead of bursting.
                    long skip = behind / frameTicks;
                    n += skip;
                    _lagged += skip;
                }
                Interlocked.Exchange(ref _frameIndex, n);

                while (_commands.TryDequeue(out var cmd)) cmd();
                try
                {
                    long w0 = Stopwatch.GetTimestamp();
                    RenderFrame(n);
                    long w = Stopwatch.GetTimestamp() - w0;
                    _statWorkTicks += w;
                    if (w > _statWorkMax) _statWorkMax = w;
                    _consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    Log.Error($"Frame {n} failed", ex);
                    if (++_consecutiveErrors > 30) { Fatal?.Invoke(ex); break; }
                }
                n++;
                _statLoops++;
                UpdateStats();
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
            Native.timeEndPeriod(1);
            while (_commands.TryDequeue(out var cmd)) { try { cmd(); } catch { } }
        }
    }

    private void RenderFrame(long n)
    {
        if (_targetDirty)
        {
            _targetDirty = false;
            var t = _requestedTarget;
            if (t != _target)
            {
                _source?.Dispose();
                _source = null;
                _target = t;
                if (t != null)
                {
                    try { _source = new WgcSource(D3D, t); }
                    catch (Exception ex)
                    {
                        Log.Error($"Could not start capture of {t.Name}", ex);
                        SourceLost?.Invoke($"Can't capture {t.Name}: {ex.Message}");
                    }
                }
            }
        }

        var src = _source;
        if (src != null)
        {
            if (src.Update()) _statCaptured++;
            if (src.IsClosed)
            {
                Log.Info($"Capture target closed: {src.Target.Name}");
                SourceLost?.Invoke($"{src.Target.Name} was closed");
                src.Dispose();
                _source = null;
                _target = null;
                src = null;
            }
        }

        bool needEncode = _encoder != null;
        int previewEvery = Math.Max(1, (int)Math.Round(Config.Fps / 30.0));
        bool needPreview = PreviewEnabled && n % previewEvery == 0;
        if (!needEncode && !needPreview) return;

        // ---- compose ----
        var bgText = _settings.BackgroundColor;
        if (bgText != _bgText)
        {
            _bgText = bgText;
            var c = OverlayRenderer.ParseColor(bgText, System.Windows.Media.Colors.Black);
            _bg = new Color4(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
        }
        int sw = src?.Width ?? 0, sh = src?.Height ?? 0;
        var layout = SourceLayout.Compute(sw, sh, Config.Width, Config.Height, _settings.ScaleMode);
        _comp.Compose(src?.HasFrame == true ? src.Srv : null, sw, sh, src?.Hdr ?? false, src?.HdrScale ?? 1f, layout, _settings.ScaleFilter, _bg, BuildOverlayDraws(n));

        // ---- encode ----
        var enc = _encoder;
        if (enc != null)
        {
            bool key = Interlocked.Exchange(ref _forceKey, 0) == 1;
            try
            {
                if (_comp.Nv12Supported)
                {
                    _comp.ConvertToNv12();
                    enc.Encode(_comp.Nv12Texture!, n, key);
                }
                else enc.Encode(_comp.CompositeTexture, n, key);
            }
            catch (Exception ex)
            {
                Log.Error("Video encoder failed", ex);
                _encoder = null;
                try { enc.Dispose(); } catch { }
                EncoderFailed?.Invoke(ex.Message);
            }
        }

        // ---- preview ----
        if (needPreview && _comp.RenderPreview(_previewWork))
        {
            lock (_previewLock)
            {
                (_previewWork, _previewShared) = (_previewShared, _previewWork);
                _previewFresh = true;
            }
        }
        _statFrames++;
    }

    private readonly List<OverlayDraw> _draws = new();

    private List<OverlayDraw> BuildOverlayDraws(long n)
    {
        _draws.Clear();
        var list = Overlays;
        if (list.Length == 0) return _draws;
        double seconds = (FrameTimeUs(n) - OverlayClockOriginUs) / 1e6;
        foreach (var o in list)
        {
            if (o.Bitmap == null) continue;
            double a = OverlayLayout.GetOpacity(o, seconds);
            if (a <= 0.001) continue;
            var r = OverlayLayout.GetRect(o, Config.Width, Config.Height);
            _draws.Add(new OverlayDraw(o.Bitmap, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, (float)a));
        }
        return _draws;
    }

    private void UpdateStats()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (double)(now - _lastStatTicks) / Freq;
        if (dt < 1.0) return;
        var enc = _encoder;
        long bytes = enc?.BytesOut ?? 0;
        double mbps = enc != null && bytes >= _lastStatBytes ? (bytes - _lastStatBytes) * 8 / dt / 1e6 : 0;
        _lastStatBytes = bytes;
        _stats = new PipelineStats(_statFrames / dt, _statCaptured / dt, CurrentFrame, _lagged, enc?.FramesDropped ?? 0, mbps,
            _source?.Width ?? 0, _source?.Height ?? 0, _source?.Hdr ?? false, enc != null, enc?.Info.Label)
        {
            WorkMsAvg = _statLoops > 0 ? _statWorkTicks * 1000.0 / Freq / _statLoops : 0,
            WorkMsMax = _statWorkMax * 1000.0 / Freq,
        };
        _statWorkTicks = _statWorkMax = _statLoops = 0;
        _statFrames = 0;
        _statCaptured = 0;
        _lastStatTicks = now;
    }

    // ------------------------------------------------------------------ shutdown

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _thread.Join(3000);
        try { Audio.Stop(); } catch (Exception ex) { Log.Warn("Audio stop: " + ex.Message); }
        if (_encoder != null)
        {
            try { _encoder.Flush(); } catch (Exception ex) { Log.Warn("Encoder flush: " + ex.Message); }
        }
        Hub.EndOfStream();
        _encoder?.Dispose();
        _encoder = null;
        Audio.Dispose();
        foreach (var a in _audioEncoders) a.Dispose();
        _source?.Dispose();
        _comp.Dispose();
        D3D.Dispose();
        Log.Info("Pipeline stopped");
    }

    /// <summary>Stream infos stay valid after Dispose (owned copies) until the pipeline object is collected; release explicitly.</summary>
    public void ReleaseStreamInfos()
    {
        _videoStream?.Dispose();
        foreach (var s in _audioStreams) s.Dispose();
    }
}
