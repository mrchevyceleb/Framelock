using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Framelock.Audio;
using Framelock.Capture;
using Framelock.Core;
using Framelock.Encoding;
using Framelock.Graphics;

namespace Framelock.Engine;

public enum RecorderState { Idle, Starting, Recording, Paused, Finalizing }

public sealed record Notification(string Title, string Message, string? FilePath = null, bool IsError = false, bool RequiresAcknowledgement = false);

/// <summary>
/// Owns the capture pipeline and the recording state machine (record / pause / stop / split / replay / screenshots / markers).
/// Lives on the UI thread; pipeline callbacks are marshalled back here.
/// </summary>
public sealed class RecorderController : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _tick;
    private CapturePipeline? _pipeline;
    private EncoderInfo? _encoderInfo;
    private VideoEncoderSettings? _encoderSettings;
    private FileSink? _sink;
    private string? _finalPath;
    private int _splitIndex;
    private long _recStartUs, _pauseStartUs, _pausedUs, _nextSplitUs;
    private ReplayBuffer? _replay;
    private bool _windowVisible = true;
    private bool _rebuildPending;
    private readonly List<Task> _finalizing = new();
    private CaptureTarget? _lastTarget;
    private WebcamCapture? _webcam;
    private WebcamConfig? _webcamConfig;
    private readonly SemaphoreSlim _webcamGate = new(1);
    private string _webcamStatus = "Webcam is off";
    public string WebcamStatus { get => _webcamStatus; private set => Set(ref _webcamStatus, value); }

    public AppSettings Settings => _store.Settings;
    public CapturePipeline? Pipeline => _pipeline;

    public event Action<Notification>? Notify;
    /// <summary>Raised on start with the countdown length; the UI shows it and the controller waits.</summary>
    public event Action<int>? CountdownRequested;
    public event Action? PipelineChanged;

    private RecorderState _state;
    public RecorderState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRecording)); OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(CanEditPipelineSettings));
            }
        }
    }
    public bool IsRecording => State is RecorderState.Recording or RecorderState.Paused;
    public bool IsPaused => State == RecorderState.Paused;
    public bool IsIdle => State == RecorderState.Idle;
    public bool IsBusy => State is RecorderState.Starting or RecorderState.Finalizing;
    public bool CanEditPipelineSettings => State == RecorderState.Idle;

    private TimeSpan _elapsed;
    public TimeSpan Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    private string _fileSize = "";
    public string FileSize { get => _fileSize; private set => Set(ref _fileSize, value); }
    private PipelineStats? _stats;
    public PipelineStats? Stats { get => _stats; private set => Set(ref _stats, value); }
    private string _statusText = "Ready";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    private bool _replayActive;
    public bool ReplayActive { get => _replayActive; private set => Set(ref _replayActive, value); }
    private string _replayInfo = "";
    public string ReplayInfo { get => _replayInfo; private set => Set(ref _replayInfo, value); }
    private string _outputInfo = "";
    public string OutputInfo { get => _outputInfo; private set => Set(ref _outputInfo, value); }
    private int _markerCount;
    public int MarkerCount { get => _markerCount; private set => Set(ref _markerCount, value); }

    public RecorderController(SettingsStore store, Dispatcher ui)
    {
        _store = store;
        _ui = ui;
        _tick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        Settings.PropertyChanged += (_, e) => OnSettingChanged(e.PropertyName);
        SoundFx.Enabled = Settings.PlaySounds;
    }

    // ================================================================== pipeline lifecycle

    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        if (_pipeline != null) _pipeline.PreviewEnabled = visible || Settings.PreviewWhileRecording && IsRecording;
        UpdateLifecycle();
    }

    private bool NeedPipeline => _windowVisible || IsRecording || State != RecorderState.Idle || Settings.ReplayEnabled;

    public void UpdateLifecycle()
    {
        if (NeedPipeline) EnsurePipeline();
        else if (_pipeline != null && State == RecorderState.Idle) DisposePipeline();
        if (DesiredWebcamConfig() != _webcamConfig) _ = SyncWebcamAsync();
    }

    private static readonly string[] PipelineKeys =
    {
        nameof(AppSettings.SourceKind), nameof(AppSettings.DisplayId), nameof(AppSettings.WindowHandle), nameof(AppSettings.RegionDisplayId),
        nameof(AppSettings.RegionX), nameof(AppSettings.RegionY), nameof(AppSettings.RegionWidth), nameof(AppSettings.RegionHeight),
        nameof(AppSettings.UseSourceResolution), nameof(AppSettings.OutputWidth), nameof(AppSettings.OutputHeight), nameof(AppSettings.Fps),
        nameof(AppSettings.VideoEncoder), nameof(AppSettings.SeparateAudioTracks), nameof(AppSettings.AudioBitrateKbps),
    };
    private static readonly string[] SourceKeys =
    {
        nameof(AppSettings.CaptureCursor), nameof(AppSettings.WindowClientOnly), nameof(AppSettings.HdrMode), nameof(AppSettings.SdrWhiteNitsOverride),
    };
    private static readonly string[] EncoderKeys =
    {
        nameof(AppSettings.RateControl), nameof(AppSettings.Quality), nameof(AppSettings.BitrateMbps), nameof(AppSettings.SpeedPreset),
        nameof(AppSettings.KeyframeSeconds), nameof(AppSettings.BFrames),
    };
    private static readonly string[] AudioKeys =
    {
        nameof(AppSettings.DesktopAudioEnabled), nameof(AppSettings.DesktopAudioMode), nameof(AppSettings.DesktopDeviceId), nameof(AppSettings.DesktopAppExe),
        nameof(AppSettings.MicEnabled), nameof(AppSettings.MicDeviceId), nameof(AppSettings.MicVoiceProcessing),
    };

    private void OnSettingChanged(string? name)
    {
        if (name == null) return;
        if (name == nameof(AppSettings.PlaySounds)) SoundFx.Enabled = Settings.PlaySounds;
        if (PipelineKeys.Contains(name) || SourceKeys.Contains(name))
        {
            // Source switches apply live (even while recording); size/fps/encoder changes rebuild when idle.
            _ui.BeginInvoke(DispatcherPriority.Background, () => EnsurePipeline());
        }
        else if (EncoderKeys.Contains(name))
        {
            _ui.BeginInvoke(DispatcherPriority.Background, RestartEncoderIfIdle);
        }
        else if (AudioKeys.Contains(name))
        {
            _ui.BeginInvoke(DispatcherPriority.Background, ApplyAudio);
        }
        else if (name == nameof(AppSettings.ReplayEnabled))
        {
            _ui.BeginInvoke(DispatcherPriority.Background, () => _ = SyncReplayAsync());
        }
        else if (name == nameof(AppSettings.ReplaySeconds) && _replay != null)
        {
            _replay.Seconds = Settings.ReplaySeconds;
        }
        if (name is nameof(AppSettings.WebcamDeviceId) or nameof(AppSettings.WebcamWidth) or nameof(AppSettings.WebcamHeight)
            or nameof(AppSettings.WebcamFps) or nameof(AppSettings.Fps) or nameof(AppSettings.SourceKind))
            _ui.BeginInvoke(DispatcherPriority.Background, () => _ = SyncWebcamAsync());
    }

    /// <summary>Creates/rebuilds the pipeline so it matches the settings. Returns false when the source can't be resolved.</summary>
    public bool EnsurePipeline()
    {
        if (!NeedPipeline && State == RecorderState.Idle) return false;
        CaptureTarget? target;
        (int w, int h) srcSize;
        try { (target, srcSize) = ResolveTarget(); }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            target = null;
            srcSize = (1920, 1080);
        }

        var cfg = BuildConfig(target, srcSize);
        if (_pipeline != null && _pipeline.Config != cfg)
        {
            if (State != RecorderState.Idle && State != RecorderState.Starting) _rebuildPending = true; // apply after the recording
            else DisposePipeline();
        }
        if (_pipeline == null)
        {
            try
            {
                _pipeline = new CapturePipeline(cfg, Settings);
                _pipeline.PreviewEnabled = _windowVisible;
                _pipeline.SourceLost += msg => _ui.BeginInvoke(() => OnSourceLost(msg));
                _pipeline.EncoderFailed += msg => _ui.BeginInvoke(() => OnEncoderFailed(msg));
                _pipeline.Fatal += ex => _ui.BeginInvoke(() => OnFatal(ex));
                _lastTarget = null;
                ApplyAudio();
                PushOverlays();
                OutputInfo = $"{cfg.Width}×{cfg.Height} · {cfg.Fps} fps · {_encoderInfo?.Label}";
                PipelineChanged?.Invoke();
                _ = SyncReplayAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Pipeline start failed", ex);
                StatusText = "Capture failed to start: " + ex.Message;
                Notify?.Invoke(new Notification("Capture failed", ex.Message, IsError: true));
                _pipeline = null;
                return false;
            }
        }
        if (target != _lastTarget)
        {
            _lastTarget = target;
            _pipeline.SetTarget(target);
        }
        _pipeline.WebcamOnly = Settings.SourceKind == SourceKind.Webcam;
        _pipeline.Webcam = _webcam;
        if (DesiredWebcamConfig() != _webcamConfig) _ = SyncWebcamAsync();
        if (Settings.SourceKind == SourceKind.Webcam)
        {
            if (State == RecorderState.Idle) StatusText = _webcam?.IsReady == true ? $"Ready · {_webcam.Name}" : WebcamStatus;
            return _webcam?.IsReady == true && !_webcam.IsStalled;
        }
        if (target != null && State == RecorderState.Idle) StatusText = $"Ready · {target.Name}";
        return target != null;
    }

    private PipelineConfig BuildConfig(CaptureTarget? target, (int w, int h) src)
    {
        var s = Settings;
        int w = s.UseSourceResolution ? src.w : s.OutputWidth;
        int h = s.UseSourceResolution ? src.h : s.OutputHeight;
        w = Math.Clamp(w & ~1, 64, 8192);
        h = Math.Clamp(h & ~1, 64, 8192);
        int fps = Math.Clamp(s.Fps, 1, 240);
        _encoderInfo = EncoderCatalog.Resolve(s.VideoEncoder, w, h);
        long luid = EncoderCatalog.AdapterFor(_encoderInfo);
        IntPtr monitor = target?.Kind == CaptureTargetKind.Monitor ? target.Handle : target != null ? Native.MonitorFromWindow(target.Handle, Native.MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
        return new PipelineConfig(monitor, luid, w, h, fps, s.SeparateAudioTracks, s.AudioBitrateKbps);
    }

    /// <summary>Turns the source settings into a capture target and its native pixel size.</summary>
    public (CaptureTarget? target, (int w, int h) size) ResolveTarget()
    {
        var s = Settings;
        var displays = DisplayInfo.GetAll();
        bool hdr = s.HdrMode == HdrMode.Auto;
        switch (s.SourceKind)
        {
            case SourceKind.Webcam:
                return (null, (_webcam?.Width > 0 ? _webcam.Width : s.WebcamWidth, _webcam?.Height > 0 ? _webcam.Height : s.WebcamHeight));
            case SourceKind.Window:
            {
                var win = WindowInfo.Resolve(s.WindowHandle, s.WindowExe, s.WindowTitle, s.WindowClass);
                if (win == null) throw new InvalidOperationException(string.IsNullOrEmpty(s.WindowTitle) ? "Pick a window to record" : $"Window \"{s.WindowTitle}\" isn't open");
                if (win.Handle.ToInt64() != s.WindowHandle) s.WindowHandle = win.Handle.ToInt64();
                var mon = Native.MonitorFromWindow(win.Handle, Native.MONITOR_DEFAULTTONEAREST);
                var d = DisplayInfo.FromHandle(displays, mon);
                var rect = s.WindowClientOnly ? Native.GetClientScreenRect(win.Handle) : Native.GetVisibleBounds(win.Handle);
                double white = s.SdrWhiteNitsOverride > 0 ? s.SdrWhiteNitsOverride : d?.SdrWhiteNits ?? 80;
                var t = new CaptureTarget(CaptureTargetKind.Window, win.Handle, null, s.WindowClientOnly, s.CaptureCursor, hdr && (d?.IsHdr ?? false), white,
                    string.IsNullOrWhiteSpace(win.Title) ? win.ExeName : win.Title);
                return (t, (Math.Max(64, rect.Width), Math.Max(64, rect.Height)));
            }
            case SourceKind.Region:
            {
                var d = DisplayInfo.Find(displays, s.RegionDisplayId) ?? displays.First();
                int x = Math.Clamp(s.RegionX, 0, d.Width - 2), y = Math.Clamp(s.RegionY, 0, d.Height - 2);
                int w = Math.Clamp(s.RegionWidth, 2, d.Width - x), h = Math.Clamp(s.RegionHeight, 2, d.Height - y);
                var t = CaptureTarget.ForRegion(d, new Native.RECT(x, y, x + w, y + h), s.CaptureCursor, hdr);
                if (s.SdrWhiteNitsOverride > 0) t = t with { SdrWhiteNits = s.SdrWhiteNitsOverride };
                return (t, (w, h));
            }
            default:
            {
                var d = DisplayInfo.Find(displays, s.DisplayId) ?? displays.First();
                var t = CaptureTarget.ForDisplay(d, s.CaptureCursor, hdr);
                if (s.SdrWhiteNitsOverride > 0) t = t with { SdrWhiteNits = s.SdrWhiteNitsOverride };
                return (t, (d.Width, d.Height));
            }
        }
    }

    private void DisposePipeline()
    {
        var p = _pipeline;
        if (p == null) return;
        _pipeline = null;
        if (_replay != null) { p.Hub.Remove(_replay); _replay.Dispose(); _replay = null; ReplayActive = false; }
        p.Dispose();
        p.ReleaseStreamInfos();
        PipelineChanged?.Invoke();
    }

    public void ApplyAudio()
    {
        var p = _pipeline;
        if (p == null) return;
        var s = Settings;
        AudioSourceSpec? desktop = null, mic = null;
        if (s.DesktopAudioEnabled)
        {
            uint self = (uint)Environment.ProcessId;
            switch (s.DesktopAudioMode)
            {
                case DesktopAudioMode.SpecificDevice:
                    desktop = new AudioSourceSpec(AudioSourceKind.DeviceLoopback, s.DesktopDeviceId, 0, false, false, "Desktop audio");
                    break;
                case DesktopAudioMode.CapturedApp:
                {
                    uint pid = 0;
                    if (s.SourceKind == SourceKind.Window && _lastTarget != null) Native.GetWindowThreadProcessId(_lastTarget.Handle, out pid);
                    desktop = pid != 0
                        ? new AudioSourceSpec(AudioSourceKind.ProcessLoopback, null, pid, false, false, "Game audio")
                        : new AudioSourceSpec(AudioSourceKind.ProcessLoopback, null, self, true, false, "Desktop audio");
                    break;
                }
                case DesktopAudioMode.SpecificApp:
                {
                    var pid = string.IsNullOrEmpty(s.DesktopAppExe) ? null : WindowInfo.FindProcessId(s.DesktopAppExe);
                    desktop = pid is uint id
                        ? new AudioSourceSpec(AudioSourceKind.ProcessLoopback, null, id, false, false, $"{s.DesktopAppExe} audio")
                        : null;
                    if (pid == null && !string.IsNullOrEmpty(s.DesktopAppExe)) StatusText = $"{s.DesktopAppExe} isn't running - no app audio";
                    break;
                }
                case DesktopAudioMode.ExcludeApp:
                {
                    var pid = string.IsNullOrEmpty(s.DesktopAppExe) ? null : WindowInfo.FindProcessId(s.DesktopAppExe);
                    desktop = new AudioSourceSpec(AudioSourceKind.ProcessLoopback, null, pid ?? self, true, false, "Desktop audio");
                    break;
                }
                default:
                    // Whole desktop minus Framelock's own sounds.
                    desktop = new AudioSourceSpec(AudioSourceKind.ProcessLoopback, null, self, true, false, "Desktop audio");
                    break;
            }
        }
        if (s.MicEnabled)
            mic = new AudioSourceSpec(AudioSourceKind.Microphone, s.MicDeviceId, 0, false, s.MicVoiceProcessing, "Microphone");
        p.Audio.Apply(desktop, mic);
    }

    public void PushOverlays()
    {
        if (_webcam is { IsReady: true, Width: > 0, Height: > 0 } camera)
            foreach (var o in Settings.Overlays.Where(o => o.Kind == OverlayKind.Webcam))
                o.WebcamAspectRatio = (double)camera.Height / camera.Width;
        var p = _pipeline;
        if (p != null) p.Overlays = Settings.Overlays.Where(o => o.Visible && (o.Bitmap != null || o.Kind == OverlayKind.Webcam)).Select(OverlayState.From).ToArray();
        if (DesiredWebcamConfig() != _webcamConfig) _ = SyncWebcamAsync();
    }

    private WebcamConfig? DesiredWebcamConfig() => !_shutDown && NeedPipeline &&
        (Settings.SourceKind == SourceKind.Webcam || Settings.Overlays.Any(o => o.Kind == OverlayKind.Webcam && o.Visible))
        ? new WebcamConfig(Settings.WebcamDeviceId, Settings.WebcamWidth, Settings.WebcamHeight,
            Settings.SourceKind == SourceKind.Webcam ? Settings.Fps : Settings.WebcamFps) : null;

    public async Task SyncWebcamAsync(bool retry = false)
    {
        await _webcamGate.WaitAsync();
        try
        {
            do
            {
                var wanted = DesiredWebcamConfig();
                if (wanted == _webcamConfig && (!retry || wanted == null || _webcam?.IsReady == true && !_webcam.IsStalled)) return;
                retry = false;
                if (_pipeline != null) _pipeline.Webcam = null;
                _webcam?.Dispose();
                _webcam = null;
                _webcamConfig = wanted;
                if (wanted == null) { WebcamStatus = "Webcam is off"; return; }
                WebcamStatus = "Starting webcam…";
                var camera = new WebcamCapture();
                _webcam = camera;
                camera.Failed += message => _ui.BeginInvoke(() =>
                {
                    if (_webcam != camera) return;
                    OnWebcamLost(message);
                });
                try
                {
                    await camera.StartAsync(wanted);
                    if (DesiredWebcamConfig() != wanted) { camera.Dispose(); continue; }
                    if (camera.Error != null) throw new InvalidOperationException(camera.Error);
                    string actual = $"{camera.Width}×{camera.Height} · {camera.FrameRate:0.##} fps";
                    bool fallback = Math.Abs(camera.FrameRate - wanted.Fps) > 0.1 || camera.Width != wanted.Width || camera.Height != wanted.Height;
                    WebcamStatus = $"{camera.Name} · {actual}" + (fallback ? $" (requested {wanted.Width}×{wanted.Height} · {wanted.Fps} fps)" : "");
                    foreach (var o in Settings.Overlays.Where(o => o.Kind == OverlayKind.Webcam))
                        o.WebcamAspectRatio = (double)camera.Height / camera.Width;
                    EnsurePipeline();
                    PushOverlays();
                }
                catch (Exception ex)
                {
                    camera.Dispose();
                    if (_webcam == camera) _webcam = null;
                    if (_pipeline?.Webcam == camera) _pipeline.Webcam = null;
                    WebcamStatus = "Webcam unavailable: " + ex.Message;
                    if (Settings.SourceKind == SourceKind.Webcam) StatusText = WebcamStatus;
                    Log.Warn(WebcamStatus);
                    if (IsRecording && Settings.SourceKind == SourceKind.Webcam && DesiredWebcamConfig() == wanted)
                        OnWebcamLost(WebcamStatus);
                }
            } while (DesiredWebcamConfig() != _webcamConfig);
        }
        finally { _webcamGate.Release(); }
    }

    private VideoEncoderSettings CurrentEncoderSettings()
    {
        var s = Settings;
        return new VideoEncoderSettings(s.RateControl, s.Quality, s.BitrateMbps, s.SpeedPreset, s.KeyframeSeconds, s.BFrames);
    }

    private async void RestartEncoderIfIdle()
    {
        var p = _pipeline;
        if (p == null || !p.EncoderRunning || State != RecorderState.Idle) return;
        if (_encoderSettings == CurrentEncoderSettings()) return;
        try
        {
            if (_replay != null) { p.Hub.Remove(_replay); _replay.Dispose(); _replay = null; ReplayActive = false; }
            await p.StopEncoderAsync();
            await SyncReplayAsync();
        }
        catch (Exception ex) { Log.Error("Encoder restart failed", ex); }
    }

    private async Task StartEncoderAsync(CapturePipeline p)
    {
        if (p.EncoderRunning) return;
        _encoderSettings = CurrentEncoderSettings();
        var info = _encoderInfo ?? EncoderCatalog.Resolve(Settings.VideoEncoder, p.Config.Width, p.Config.Height);
        try { await p.EnsureEncoderAsync(info, _encoderSettings); }
        catch (Exception ex) when (info.IsHardware)
        {
            // Hardware encoder refused (driver session limit, unsupported size...): fall back to the next best.
            Log.Warn($"{info.Id} failed ({ex.Message}); falling back");
            var fallback = EncoderCatalog.Available.FirstOrDefault(e => e != info && e.Vendor == p.D3D.Vendor && Math.Max(p.Config.Width, p.Config.Height) <= e.MaxDimension)
                           ?? EncoderCatalog.Resolve("libx264", p.Config.Width, p.Config.Height);
            Notify?.Invoke(new Notification("Encoder fallback", $"{info.Label} failed: {ex.Message}\nUsing {fallback.Label}.", IsError: true));
            _encoderInfo = fallback;
            await p.EnsureEncoderAsync(fallback, _encoderSettings);
        }
        OutputInfo = $"{p.Config.Width}×{p.Config.Height} · {p.Config.Fps} fps · {p.EncoderLabel}";
    }

    private async Task StopEncoderIfUnused()
    {
        var p = _pipeline;
        if (p == null || !p.EncoderRunning) return;
        if (IsRecording || State != RecorderState.Idle || _replay != null || p.Hub.SinkCount > 0) return;
        await p.StopEncoderAsync();
    }

    // ================================================================== recording

    public async Task StartRecordingAsync()
    {
        if (State != RecorderState.Idle) return;
        State = RecorderState.Starting;
        try
        {
            await SyncWebcamAsync(retry: true);
            if (State != RecorderState.Starting) return;
            if (DesiredWebcamConfig() != null && (_webcam?.IsReady != true || _webcam.IsStalled))
            {
                if (Settings.SourceKind == SourceKind.Webcam) throw new InvalidOperationException(WebcamStatus);
                Notify?.Invoke(new Notification("Recording without webcam", WebcamStatus + " The screen recording will continue without the camera overlay.", IsError: true));
            }
            if (!EnsurePipeline() || _pipeline == null) throw new InvalidOperationException(StatusText);
            var p = _pipeline;
            CheckDiskSpace();
            // Validate the destination before showing a countdown or warming the encoder.
            _finalPath = BuildPath(Settings.FileNameTemplate, ContainerExt(Settings.Container));

            if (Settings.CountdownSeconds > 0)
            {
                CountdownRequested?.Invoke(Settings.CountdownSeconds);
                var encTask = StartEncoderAsync(p); // warm up the encoder during the countdown
                await Task.Delay(Settings.CountdownSeconds * 1000);
                await encTask;
            }
            else await StartEncoderAsync(p);
            if (State != RecorderState.Starting || _pipeline != p) return; // cancelled
            if (Settings.SourceKind == SourceKind.Webcam && (_webcam?.IsReady != true || _webcam.IsStalled))
                throw new InvalidOperationException(WebcamStatus);

            _splitIndex = 0;
            // The sink listens before the keyframe is asked for: a slow file open must not miss it (and then wait for the
            // next scheduled keyframe, which cut up to 2 s from the start).
            long t = p.NowUs;
            _sink = CreateSink(p, _finalPath, t);
            p.RequestKeyframe();
            _recStartUs = t;
            _pausedUs = 0;
            _nextSplitUs = Settings.SplitEveryMinutes > 0 ? t + Settings.SplitEveryMinutes * 60_000_000L : long.MaxValue;
            p.OverlayClockOriginUs = t;
            MarkerCount = 0;
            State = RecorderState.Recording;
            StatusText = "Recording";
            ApplyRecordingPriority(true);
            SoundFx.Start();
            Log.Info($"Recording started → {_sink.Path}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not start recording", ex);
            State = RecorderState.Idle;
            StatusText = "Recording failed: " + ex.Message;
            Notify?.Invoke(new Notification("Recording did not start", "No video is being saved.\n\n" + ex.Message, IsError: true, RequiresAcknowledgement: true));
            _ = StopEncoderIfUnused();
        }
    }

    private FileSink CreateSink(CapturePipeline p, string finalPath, long startUs)
    {
        var container = Settings.Container;
        bool crashSafe = Settings.CrashSafe && container != ContainerFormat.Mkv;
        string writePath = crashSafe ? UniquePath(Path.ChangeExtension(finalPath, ".recording.mkv")) : finalPath;
        var sink = new FileSink(writePath, crashSafe ? ContainerFormat.Mkv : container, p.Streams, startUs, Settings.SaveMarkers);
        p.Hub.Add(sink);
        var captured = (sink, finalPath, crashSafe, container, p);
        _ = sink.Completion.ContinueWith(t => _ui.BeginInvoke(() => TrackFinalize(OnSinkCompleted(captured.sink, captured.finalPath, captured.crashSafe, captured.container, captured.p, t.Result))));
        return sink;
    }

    private void TrackFinalize(Task t)
    {
        _finalizing.Add(t);
        t.ContinueWith(_ => _ui.BeginInvoke(() => _finalizing.Remove(t)));
    }

    private async Task OnSinkCompleted(FileSink sink, string finalPath, bool crashSafe, ContainerFormat container, CapturePipeline p, FileSinkResult r)
    {
        p.Hub.Remove(sink);
        // The writer ended on its own (disk error, drive too slow): the recording is over.
        bool unexpected = _sink == sink && IsRecording;
        if (unexpected)
        {
            _sink = null;
            State = RecorderState.Finalizing;
            SoundFx.Stop();
            ApplyRecordingPriority(false);
        }
        try { await FinishFileAsync(finalPath, crashSafe, container, r); }
        finally
        {
            if (unexpected)
            {
                State = RecorderState.Idle;
                Elapsed = TimeSpan.Zero;
                FileSize = "";
                _ = StopEncoderIfUnused();
                UpdateLifecycle();
            }
        }
    }

    private async Task FinishFileAsync(string finalPath, bool crashSafe, ContainerFormat container, FileSinkResult r)
    {
        if (!r.Success)
        {
            Notify?.Invoke(new Notification("Recording failed", r.Error ?? "Unknown error", r.Path, IsError: true));
            return;
        }
        string result = r.Path;
        if (crashSafe)
        {
            StatusText = "Finalizing " + Path.GetFileName(finalPath) + "…";
            try
            {
                string target = UniquePath(finalPath);
                await Task.Run(() => Remuxer.Remux(r.Path, target, container));
                File.Delete(r.Path);
                result = target;
            }
            catch (Exception ex)
            {
                Log.Error("Remux failed; keeping MKV", ex);
                var mkv = UniquePath(Path.ChangeExtension(finalPath, ".mkv"));
                try { File.Move(r.Path, mkv); result = mkv; } catch (Exception moveEx) { Log.Warn("Keeping the .recording.mkv name: " + moveEx.Message); }
                Notify?.Invoke(new Notification("Kept as MKV", $"Converting to {container} failed ({ex.Message}). Your recording is safe as MKV.", result, true));
            }
        }
        if (Settings.SaveMarkers && r.Markers.Count > 0) WriteChapterFile(result, r.Markers);
        var size = new FileInfo(result).Exists ? new FileInfo(result).Length : r.Bytes;
        var saved = $"{Path.GetFileName(result)} · {FormatDuration(TimeSpan.FromMicroseconds(r.DurationUs))} · {FormatBytes(size)}";
        Notify?.Invoke(r.Warning != null
            ? new Notification("Recording stopped early", r.Warning + Environment.NewLine + saved, result, IsError: true)
            : new Notification("Recording saved", saved, result));
        if (State == RecorderState.Idle) StatusText = "Saved " + Path.GetFileName(result);
    }

    public void TogglePause()
    {
        var p = _pipeline;
        if (p == null || _sink == null) return;
        if (State == RecorderState.Recording)
        {
            long t = p.RequestKeyframe();
            _sink.Pause(t);
            _pauseStartUs = t;
            State = RecorderState.Paused;
            StatusText = "Paused";
            SoundFx.Tick();
        }
        else if (State == RecorderState.Paused)
        {
            long t = p.RequestKeyframe();
            _sink.Resume(t);
            _pausedUs += t - _pauseStartUs;
            State = RecorderState.Recording;
            StatusText = "Recording";
            SoundFx.Tick();
        }
    }

    public Task StopRecordingAsync() => StopRecordingAsync(abort: false);

    private Task? _stopping;

    /// <param name="abort">The video encoder is gone: end the file at its last frame instead of waiting for a keyframe.</param>
    private Task StopRecordingAsync(bool abort)
    {
        var t = StopRecordingCoreAsync(abort);
        if (!t.IsCompleted) _stopping = t; // a no-op call (already stopping) must not hide the real stop from exit
        return t;
    }

    private async Task StopRecordingCoreAsync(bool abort)
    {
        if (State == RecorderState.Starting) { State = RecorderState.Idle; _ = StopEncoderIfUnused(); return; }
        if (!IsRecording || _sink == null || _pipeline == null) return;
        var p = _pipeline;
        var sink = _sink;
        if (Stats is { } st)
            Log.Info($"Recording stats: out {st.OutputFps:F1} fps, capture {st.CaptureFps:F1} fps, skipped {st.Lagged} frame slots (GPU busy, since pipeline start), " +
                     $"encoder dropped {st.EncoderDropped}, work {st.WorkMsAvg:F2}/{st.WorkMsMax:F1} ms");
        if (abort) sink.Abort();
        else sink.Stop(p.RequestKeyframe());
        _sink = null;
        State = RecorderState.Finalizing;
        StatusText = "Finishing…";
        SoundFx.Stop();
        ApplyRecordingPriority(false);
        try
        {
            await sink.Completion;
            await Task.Delay(50); // let OnSinkCompleted register
            var pending = _finalizing.ToArray();
            await Task.WhenAll(pending);
        }
        catch (Exception ex) { Log.Error("Stop failed", ex); }
        State = RecorderState.Idle;
        Elapsed = TimeSpan.Zero;
        FileSize = "";
        if (_rebuildPending) { _rebuildPending = false; DisposePipeline(); }
        await StopEncoderIfUnused();
        UpdateLifecycle();
    }

    public Task ToggleRecordingAsync() => State switch
    {
        RecorderState.Idle => StartRecordingAsync(),
        RecorderState.Recording or RecorderState.Paused or RecorderState.Starting => StopRecordingAsync(),
        _ => Task.CompletedTask,
    };

    private void SplitNow()
    {
        var p = _pipeline;
        var old = _sink;
        if (p == null || old == null || State != RecorderState.Recording) return;
        _splitIndex++;
        long t = p.NowUs;
        var basePath = _finalPath!;
        var partPath = Path.Combine(Path.GetDirectoryName(basePath)!, $"{Path.GetFileNameWithoutExtension(basePath)} (part {_splitIndex + 1}){Path.GetExtension(basePath)}");
        _sink = CreateSink(p, partPath, t);
        old.Stop(t);
        p.RequestKeyframe(); // after both sinks know the cut, so neither can miss the keyframe
        _nextSplitUs = t + Settings.SplitEveryMinutes * 60_000_000L;
        Log.Info($"Split recording → {partPath}");
    }

    public void AddMarker()
    {
        var p = _pipeline;
        if (p == null || _sink == null || !IsRecording) return;
        MarkerCount++;
        var label = $"Marker {MarkerCount}";
        // While paused the file is frozen at the pause point; the live clock would land past the end of the file.
        _sink.AddMarker(State == RecorderState.Paused ? _pauseStartUs : p.NowUs, label);
        Notify?.Invoke(new Notification("Marker added", $"{label} at {FormatDuration(Elapsed)}"));
        SoundFx.Tick();
    }

    // ================================================================== replay buffer

    private async Task SyncReplayAsync()
    {
        var p = _pipeline;
        if (p == null) return;
        if (Settings.ReplayEnabled && _replay == null)
        {
            try
            {
                await StartEncoderAsync(p);
                if (_pipeline != p || _replay != null || !Settings.ReplayEnabled) return;
                _replay = new ReplayBuffer(p.Streams, Settings.ReplaySeconds);
                p.Hub.Add(_replay);
                ReplayActive = true;
                Log.Info($"Replay buffer on ({Settings.ReplaySeconds}s)");
            }
            catch (Exception ex)
            {
                Log.Error("Replay buffer failed", ex);
                Notify?.Invoke(new Notification("Replay buffer failed", ex.Message, IsError: true));
            }
        }
        else if (!Settings.ReplayEnabled && _replay != null)
        {
            p.Hub.Remove(_replay);
            _replay.Dispose();
            _replay = null;
            ReplayActive = false;
            ReplayInfo = "";
            await StopEncoderIfUnused();
            UpdateLifecycle();
        }
    }

    public async Task SaveReplayAsync()
    {
        if (_replay == null)
        {
            Notify?.Invoke(new Notification("Replay buffer is off", "Turn on the replay buffer to save the last moments on demand.", IsError: true));
            return;
        }
        var path = BuildPath("Replay {source} {date} {time}", ".mp4");
        SoundFx.Shot();
        var r = await _replay.SaveAsync(path);
        Notify?.Invoke(r.Success
            ? new Notification("Replay saved", $"{Path.GetFileName(path)} · {FormatDuration(TimeSpan.FromMicroseconds(r.DurationUs))}", path)
            : new Notification("Replay failed", r.Error ?? "", IsError: true));
    }

    // ================================================================== screenshots

    public async Task TakeScreenshotAsync()
    {
        try
        {
            bool temp = _pipeline == null;
            if (temp)
            {
                _windowVisible = true; // keep it alive for the grab
                EnsurePipeline();
                await Task.Delay(400);
            }
            var p = _pipeline ?? throw new InvalidOperationException("Capture isn't running");
            var path = BuildPath("Screenshot {source} {date} {time}", ".png", "Screenshots");
            await p.TakeScreenshotAsync(path);
            SoundFx.Shot();
            Notify?.Invoke(new Notification("Screenshot saved", Path.GetFileName(path), path));
        }
        catch (Exception ex)
        {
            Notify?.Invoke(new Notification("Screenshot failed", ex.Message, IsError: true));
        }
    }

    // ================================================================== misc

    public void ToggleMicMute()
    {
        Settings.MicMuted = !Settings.MicMuted;
        Notify?.Invoke(new Notification(Settings.MicMuted ? "Microphone muted" : "Microphone live", ""));
    }

    private void OnTick()
    {
        if (_webcam?.IsStalled == true && !WebcamStatus.StartsWith("Webcam stopped"))
        {
            OnWebcamLost("Webcam stopped sending frames. Reconnect it or choose another camera.");
        }
        var p = _pipeline;
        Stats = p?.Stats;
        if (p != null && IsRecording)
        {
            long now = State == RecorderState.Paused ? _pauseStartUs : p.NowUs;
            Elapsed = TimeSpan.FromMicroseconds(Math.Max(0, now - _recStartUs - _pausedUs));
            if (_sink != null) FileSize = FormatBytes(_sink.BytesWritten);
            if (State == RecorderState.Recording && p.NowUs >= _nextSplitUs) SplitNow();
        }
        if (_replay != null)
            ReplayInfo = $"{_replay.BufferedSeconds:F0}s buffered · {FormatBytes(_replay.MemoryBytes)} RAM";
    }

    private void OnSourceLost(string msg)
    {
        StatusText = msg;
        Notify?.Invoke(new Notification("Capture source lost", IsRecording ? msg + " - still recording (black frames)." : msg, IsError: true));
        _lastTarget = null;
    }

    private void OnWebcamLost(string message)
    {
        WebcamStatus = message;
        bool stop = IsRecording && Settings.SourceKind == SourceKind.Webcam;
        Notify?.Invoke(new Notification(stop ? "Webcam recording stopped" : "Webcam unavailable",
            message + (stop ? " Saving the recording captured so far." : ""), IsError: true));
        if (stop) _ = StopRecordingAsync();
    }

    private void OnEncoderFailed(string msg)
    {
        Notify?.Invoke(new Notification("Encoder error", msg, IsError: true));
        // No more video packets will come: the replay buffer would only collect audio.
        if (_replay != null && _pipeline != null)
        {
            _pipeline.Hub.Remove(_replay);
            _replay.Dispose();
            _replay = null;
            ReplayActive = false;
            ReplayInfo = "";
        }
        if (IsRecording) _ = StopRecordingAsync(abort: true);
    }

    private void OnFatal(Exception ex)
    {
        Log.Error("Pipeline fatal error - restarting capture", ex);
        Notify?.Invoke(new Notification("Capture restarted", ex.Message, IsError: true));
        _sink = null;
        State = RecorderState.Idle;
        DisposePipeline();
        _ui.BeginInvoke(DispatcherPriority.Background, UpdateLifecycle);
    }

    private void ApplyRecordingPriority(bool recording)
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            proc.PriorityClass = !recording ? ProcessPriorityClass.Normal : Settings.Priority switch
            {
                RecorderPriority.High => ProcessPriorityClass.High,
                RecorderPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
                _ => ProcessPriorityClass.Normal,
            };
            Native.SetThreadExecutionState(recording ? Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED : Native.ES_CONTINUOUS);
        }
        catch (Exception ex) { Log.Debug("Priority change failed: " + ex.Message); }
    }

    private void CheckDiskSpace()
    {
        Settings.OutputFolder = Path.GetFullPath(Settings.OutputFolder);
        Directory.CreateDirectory(Settings.OutputFolder);
        // Creating a directory alone doesn't prove that a recording file can be written there.
        using (var probe = new FileStream(Path.Combine(Settings.OutputFolder, ".framelock-write-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            probe.WriteByte(0);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Settings.OutputFolder)!);
            if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                Notify?.Invoke(new Notification("Low disk space", $"Only {FormatBytes(drive.AvailableFreeSpace)} free on {drive.Name}", IsError: true));
        }
        catch { }
    }

    public string SourceName => Settings.SourceKind == SourceKind.Webcam ? _webcam?.Name ?? "Webcam" : _lastTarget?.Name ?? Settings.SourceKind.ToString();
    public CaptureTarget? CurrentTarget => _lastTarget;

    private string BuildPath(string template, string ext, string? subfolder = null)
    {
        var now = DateTime.Now;
        var p = _pipeline;
        string name = template
            .Replace("{source}", SourceName)
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH-mm-ss"))
            .Replace("{res}", p != null ? $"{p.Config.Width}x{p.Config.Height}" : "")
            .Replace("{fps}", p != null ? $"{p.Config.Fps}fps" : "");
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        name = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.');
        if (name.Length > 150) name = name[..150];
        if (string.IsNullOrWhiteSpace(name)) name = "Recording " + now.ToString("yyyy-MM-dd HH-mm-ss");
        var folder = Settings.OutputFolder;
        if (string.IsNullOrWhiteSpace(folder)) folder = Paths.DefaultOutputFolder;
        if (subfolder != null) folder = Path.Combine(folder, subfolder);
        Directory.CreateDirectory(folder);
        return UniquePath(Path.Combine(folder, name + ext));
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var baseName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static string ContainerExt(ContainerFormat c) => c switch { ContainerFormat.Mkv => ".mkv", ContainerFormat.Mov => ".mov", _ => ".mp4" };

    private static void WriteChapterFile(string videoPath, IReadOnlyList<Chapter> markers)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# YouTube chapters - paste into your video description");
            foreach (var m in FileSink.WithStartChapter(markers))
                sb.AppendLine($"{FormatDuration(TimeSpan.FromMilliseconds(m.StartMs))} {m.Title}");
            File.WriteAllText(Path.ChangeExtension(videoPath, ".chapters.txt"), sb.ToString());
        }
        catch (Exception ex) { Log.Warn("Writing chapters failed: " + ex.Message); }
    }

    public static string FormatDuration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F0} MB",
        _ => $"{b / 1024.0:F0} KB",
    };

    private bool _shutDown;

    /// <summary>Stops an active recording and waits (bounded) for every file still being finalized, including a stop
    /// or remux already in flight. The engine stays usable, so a cancelled logoff leaves Framelock working.</summary>
    public void FinishRecordingBlocking()
    {
        try
        {
            var tasks = new List<Task>(_finalizing);
            if (IsRecording) tasks.Add(StopRecordingAsync());
            else if (_stopping is { IsCompleted: false } s) tasks.Add(s);
            if (tasks.Count == 0) return;
            var all = Task.WhenAll(tasks);
            var sw = Stopwatch.StartNew();
            while (!all.IsCompleted && sw.ElapsedMilliseconds < 15000)
                _ui.Invoke(DispatcherPriority.Background, () => { });
            if (!all.IsCompleted) Log.Warn("Finalizing didn't finish within 15 s");
        }
        catch (Exception ex) { Log.Error("Stopping the recording failed", ex); }
    }

    /// <summary>Stops everything; waits for files to be finalized (bounded).</summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _tick.Stop();
        FinishRecordingBlocking();
        DisposePipeline();
        _webcam?.Dispose();
        _webcam = null;
    }

    public void Dispose() => Shutdown();
}
