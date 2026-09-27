using System.Diagnostics;
using Framelock.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Framelock.Audio;

public enum AudioSourceKind { DeviceLoopback, ProcessLoopback, Microphone }

public sealed record AudioSourceSpec(AudioSourceKind Kind, string? DeviceId, uint ProcessId, bool ExcludeProcess, bool VoiceProcessing, string Label);

/// <summary>
/// One WASAPI capture stream placed on the pipeline timeline. Packet positions come from WASAPI's QPC timestamps with
/// continuous-write smoothing and sub-millisecond drift correction, so long recordings stay in sync with the video.
/// </summary>
public sealed class AudioSource : IDisposable
{
    private static readonly long Freq = Stopwatch.Frequency;
    private readonly WasapiRecorder _recorder;
    private readonly SampleConverter _converter;
    private readonly long _t0Ticks;
    private readonly long _t0Hns;
    private float[] _conv = new float[8192];
    private bool _hasPos;
    private long _pos;
    private double _errAvg;
    private float _lastL, _lastR;

    public AudioSourceSpec Spec { get; }
    public TimelineBuffer Buffer { get; } = new();
    public string DeviceName { get; }
    public volatile bool Failed;
    public string? FailReason { get; private set; }
    /// <summary>Extra delay in samples (mic sync offset). Can change live.</summary>
    public volatile int OffsetSamples;

    private AudioSource(AudioSourceSpec spec, WasapiRecorder rec, long t0Ticks)
    {
        Spec = spec;
        _recorder = rec;
        _t0Ticks = t0Ticks;
        _t0Hns = (long)((Int128)t0Ticks * 10_000_000 / Freq);
        _converter = new SampleConverter(rec.WaveFormat);
        DeviceName = rec.DeviceFriendlyName ?? spec.Label;
        rec.DataAvailable += OnData;
        rec.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                Failed = true;
                FailReason = e.Exception.Message;
                Log.Warn($"Audio source '{spec.Label}' stopped: {e.Exception.Message}");
            }
        };
        Buffer.SkipTo(NowSamples() - 4800);
    }

    public static async Task<AudioSource> CreateAsync(AudioSourceSpec spec, long t0Ticks)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        WasapiRecorder rec;
        switch (spec.Kind)
        {
            case AudioSourceKind.ProcessLoopback:
            {
                var mode = spec.ExcludeProcess ? ProcessLoopbackMode.ExcludeTargetProcessTree : ProcessLoopbackMode.IncludeTargetProcessTree;
                try
                {
                    rec = await new WasapiRecorderBuilder().WithProcessLoopback(spec.ProcessId, mode).WithFormat(format).WithSharedMode().BuildAsync();
                }
                catch (Exception ex)
                {
                    Log.Debug($"Float process loopback failed ({ex.Message}); retrying with 16-bit PCM");
                    rec = await new WasapiRecorderBuilder().WithProcessLoopback(spec.ProcessId, mode).WithFormat(new WaveFormat(48000, 16, 2)).WithSharedMode().BuildAsync();
                }
                break;
            }
            case AudioSourceKind.DeviceLoopback:
            {
                using var en = new MMDeviceEnumerator();
                var dev = spec.DeviceId != null ? en.GetDevice(spec.DeviceId) : en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                rec = new WasapiRecorderBuilder().WithDevice(dev).WithLoopbackCapture().WithSharedMode().WithFormat(format).Build();
                break;
            }
            default:
            {
                using var en = new MMDeviceEnumerator();
                var dev = spec.DeviceId != null ? en.GetDevice(spec.DeviceId) : en.GetDefaultAudioEndpoint(DataFlow.Capture, spec.VoiceProcessing ? Role.Communications : Role.Console);
                var b = new WasapiRecorderBuilder().WithDevice(dev).WithSharedMode().WithFormat(format);
                if (spec.VoiceProcessing) b = b.WithCommunicationsMode();
                rec = b.Build();
                break;
            }
        }
        AudioSource? src = null;
        try
        {
            src = new AudioSource(spec, rec, t0Ticks);
            rec.StartRecording();
        }
        catch
        {
            // Don't leak the COM capture client (retries would pile them up).
            if (src != null) src.Dispose(); else rec.Dispose();
            throw;
        }
        Log.Info($"Audio source started: {spec.Label} [{src.DeviceName}] {rec.WaveFormat}");
        return src;
    }

    private long NowSamples() => (long)((Int128)(Stopwatch.GetTimestamp() - _t0Ticks) * 48000 / Freq);

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
            int frames = _converter.Convert(buffer, silent, ref _conv);
            if (frames <= 0) return;

            // Where does this packet belong? WASAPI stamps the first frame's capture time (100 ns QPC units).
            long now = NowSamples();
            long arrival = now - frames;
            long measured = qpcPosition > 0 ? (qpcPosition - _t0Hns) * 48 / 10_000 : arrival;
            if (Math.Abs(measured - arrival) > 24_000) measured = arrival; // bogus timestamp: trust arrival time

            if (!_hasPos || (flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
            {
                _pos = measured;
                _hasPos = true;
                _errAvg = 0;
            }
            else
            {
                long err = measured - _pos;
                if (Math.Abs(err) > 2400) // >50 ms: stream restarted / loopback resumed after silence
                {
                    _pos = measured;
                    _errAvg = 0;
                }
                else
                {
                    // Clock drift: nudge by single samples (inaudible) to track the QPC timeline.
                    _errAvg = _errAvg * 0.97 + err * 0.03;
                    if (_errAvg > 48)
                    {
                        Span<float> dup = stackalloc float[] { _lastL, _lastR };
                        Buffer.Write(_pos + OffsetSamples, dup);
                        _pos++;
                        _errAvg -= 1;
                    }
                    else if (_errAvg < -48)
                    {
                        _pos--;
                        _errAvg += 1;
                    }
                }
            }

            var span = new ReadOnlySpan<float>(_conv, 0, frames * 2);
            Buffer.Write(_pos + OffsetSamples, span);
            _pos += frames;
            _lastL = _conv[frames * 2 - 2];
            _lastR = _conv[frames * 2 - 1];
        }
        catch (Exception ex)
        {
            Log.Error($"Audio capture callback ({Spec.Label})", ex);
        }
    }

    public void Dispose()
    {
        try { _recorder.StopRecording(); } catch { }
        try { _recorder.Dispose(); } catch { }
    }
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

public static class AudioDevices
{
    public static List<AudioDevice> Get(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? def = null;
            if (en.TryGetDefaultAudioEndpoint(flow, flow == DataFlow.Render ? Role.Multimedia : Role.Console, out var d)) def = d.ID;
            foreach (var dev in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                list.Add(new AudioDevice(dev.ID, dev.FriendlyName, dev.ID == def));
        }
        catch (Exception ex) { Log.Warn("Audio device enumeration failed: " + ex.Message); }
        return list.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToList();
    }
}
