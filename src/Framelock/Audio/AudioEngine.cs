using System.Diagnostics;
using System.Windows.Input;
using Framelock.Core;
using Framelock.Encoding;

namespace Framelock.Audio;

/// <summary>
/// Mixes desktop + microphone on the pipeline timeline into 1024-sample blocks, ~120 ms behind real time so late WASAPI
/// packets still land in place. Feeds the AAC encoders: track 1 = mix, and optionally 2 = desktop only, 3 = mic only.
/// Volumes, mute, noise gate and push-to-talk apply live.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int Block = 1024;
    private const int MarginSamples = 48000 * 120 / 1000;
    private static readonly long Freq = Stopwatch.Frequency;

    private readonly long _t0;
    private readonly AppSettings _settings;
    private readonly AudioEncoder _mixEncoder;
    private readonly AudioEncoder? _desktopEncoder, _micEncoder;
    private readonly Thread _thread;
    private volatile bool _stop;

    private AudioSource? _desktop, _mic;
    private AudioSourceSpec? _desktopSpec, _micSpec;
    private readonly object _srcLock = new();
    private long _nextRetry;
    private int _applyVersion;

    private long _pos;
    private readonly float[] _d = new float[Block * 2], _m = new float[Block * 2], _mix = new float[Block * 2];
    private float _desktopGain = 1, _micGain = 1, _gateGain = 1;
    private int _gateHold;
    private string? _pttKeyText;
    private int _pttVk;

    /// <summary>Post-gain peak levels 0..1 with a short decay, for UI meters.</summary>
    public float DesktopLevel { get; private set; }
    public float MicLevel { get; private set; }
    public bool MicGateOpen { get; private set; } = true;
    public bool PushToTalkActive { get; private set; }
    public string DesktopStatus { get; private set; } = "Off";
    public string MicStatus { get; private set; } = "Off";
    public long PositionSamples => Interlocked.Read(ref _pos);

    public AudioEngine(long t0Ticks, AppSettings settings, AudioEncoder mix, AudioEncoder? desktop, AudioEncoder? mic)
    {
        _t0 = t0Ticks;
        _settings = settings;
        _mixEncoder = mix;
        _desktopEncoder = desktop;
        _micEncoder = mic;
        _pos = NowSamples() - MarginSamples;
        _thread = new Thread(Run) { Name = "Framelock audio mixer", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private long NowSamples() => (long)((Int128)(Stopwatch.GetTimestamp() - _t0) * 48000 / Freq);

    /// <summary>Switches the capture sources (null = off). Unchanged specs keep their running stream.</summary>
    public void Apply(AudioSourceSpec? desktop, AudioSourceSpec? mic)
    {
        int version = Interlocked.Increment(ref _applyVersion);
        _ = ApplyAsync(desktop, mic, version);
    }

    private async Task ApplyAsync(AudioSourceSpec? desktop, AudioSourceSpec? mic, int version)
    {
        if (desktop != _desktopSpec || (_desktop?.Failed ?? false))
        {
            var created = desktop == null ? null : await TryCreate(desktop);
            if (version != _applyVersion) { created?.Dispose(); return; }
            AudioSource? old;
            lock (_srcLock) { old = _desktop; _desktop = created; _desktopSpec = desktop; }
            old?.Dispose();
            DesktopStatus = desktop == null ? "Off" : created == null ? "Unavailable" : created.DeviceName;
        }
        if (mic != _micSpec || (_mic?.Failed ?? false))
        {
            var created = mic == null ? null : await TryCreate(mic);
            if (version != _applyVersion) { created?.Dispose(); return; }
            AudioSource? old;
            lock (_srcLock) { old = _mic; _mic = created; _micSpec = mic; }
            old?.Dispose();
            MicStatus = mic == null ? "Off" : created == null ? "Unavailable" : created.DeviceName;
        }
    }

    private async Task<AudioSource?> TryCreate(AudioSourceSpec spec)
    {
        try { return await AudioSource.CreateAsync(spec, _t0); }
        catch (Exception ex)
        {
            Log.Warn($"Audio source '{spec.Label}' failed to start: {ex.Message}");
            // Game-audio-only capture can fail (old Windows / protected app): fall back to the whole desktop.
            if (spec.Kind == AudioSourceKind.ProcessLoopback)
            {
                try { return await AudioSource.CreateAsync(spec with { Kind = AudioSourceKind.DeviceLoopback, DeviceId = null, Label = "Desktop audio (fallback)" }, _t0); }
                catch (Exception ex2) { Log.Warn("Desktop audio fallback failed: " + ex2.Message); }
            }
            return null;
        }
    }

    private void Run()
    {
        uint taskIndex = 0;
        var mmcss = Native.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        try
        {
            while (!_stop)
            {
                long limit = NowSamples() - MarginSamples;
                if (limit - _pos > 48000 * 3)
                {
                    Log.Warn($"Audio mixer fell {(limit - _pos) / 48.0:F0} ms behind; skipping ahead");
                    _pos = limit - Block;
                }
                while (!_stop && _pos + Block <= limit)
                {
                    MixBlock();
                    Interlocked.Add(ref _pos, Block);
                }
                RetryFailedSources();
                Thread.Sleep(4);
            }
        }
        catch (Exception ex) { Log.Error("Audio mixer crashed", ex); }
        finally { if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss); }
    }

    private void RetryFailedSources()
    {
        if (Environment.TickCount64 < _nextRetry) return;
        _nextRetry = Environment.TickCount64 + 3000;
        bool desktopBad = _desktopSpec != null && (_desktop == null || _desktop.Failed);
        bool micBad = _micSpec != null && (_mic == null || _mic.Failed);
        if (!desktopBad && !micBad) return;
        var d = _desktopSpec; var m = _micSpec;
        lock (_srcLock)
        {
            if (desktopBad) _desktopSpec = null;
            if (micBad) _micSpec = null;
        }
        Apply(d, m);
    }

    private void MixBlock()
    {
        AudioSource? desktop, mic;
        lock (_srcLock) { desktop = _desktop; mic = _mic; }
        var s = _settings;

        if (desktop != null) desktop.Buffer.Read(_pos, _d); else Array.Clear(_d);
        if (mic != null)
        {
            mic.OffsetSamples = s.MicSyncOffsetMs * 48;
            mic.Buffer.Read(_pos, _m);
        }
        else Array.Clear(_m);

        // ---- desktop gain (ramped) ----
        float dTarget = s.DesktopAudioEnabled && !s.DesktopMuted ? (float)s.DesktopVolume : 0f;
        float dPeak = ApplyGain(_d, ref _desktopGain, dTarget, 0.002f);

        // ---- mic: gate / push-to-talk / mute ----
        bool pttOk = true;
        if (s.MicPushToTalk)
        {
            if (_pttKeyText != s.PushToTalkKey)
            {
                _pttKeyText = s.PushToTalkKey;
                try { _pttVk = KeyInterop.VirtualKeyFromKey(Hotkey.ParseKey(s.PushToTalkKey)); } catch { _pttVk = 0x14; }
            }
            pttOk = _pttVk != 0 && (Native.GetAsyncKeyState(_pttVk) & 0x8000) != 0;
        }
        PushToTalkActive = s.MicPushToTalk && pttOk;

        if (s.MicNoiseGate)
        {
            double sum = 0;
            for (int i = 0; i < _m.Length; i++) sum += _m[i] * _m[i];
            double db = 10 * Math.Log10(sum / _m.Length + 1e-12);
            if (db > s.MicGateDb) _gateHold = 10; // ~210 ms hold
            else if (_gateHold > 0) _gateHold--;
            MicGateOpen = _gateHold > 0;
        }
        else MicGateOpen = true;

        float mTarget = s.MicEnabled && !s.MicMuted && pttOk ? (float)s.MicVolume : 0f;
        // Gate: fast attack, slower release.
        float gateTarget = MicGateOpen ? 1f : 0f;
        for (int i = 0; i < Block; i++)
        {
            _gateGain += (gateTarget - _gateGain) * (gateTarget > _gateGain ? 0.02f : 0.0015f);
            _m[2 * i] *= _gateGain;
            _m[2 * i + 1] *= _gateGain;
        }
        float mPeak = ApplyGain(_m, ref _micGain, mTarget, 0.003f);

        DesktopLevel = Math.Max(dPeak, DesktopLevel * 0.85f);
        MicLevel = Math.Max(mPeak, MicLevel * 0.85f);

        for (int i = 0; i < _mix.Length; i++) _mix[i] = SoftClip(_d[i] + _m[i]);
        _mixEncoder.Encode(_mix, _pos);
        if (_desktopEncoder != null)
        {
            for (int i = 0; i < _d.Length; i++) _d[i] = SoftClip(_d[i]);
            _desktopEncoder.Encode(_d, _pos);
        }
        if (_micEncoder != null)
        {
            for (int i = 0; i < _m.Length; i++) _m[i] = SoftClip(_m[i]);
            _micEncoder.Encode(_m, _pos);
        }
    }

    private static float ApplyGain(float[] buf, ref float gain, float target, float rate)
    {
        float peak = 0;
        for (int i = 0; i < Block; i++)
        {
            gain += (target - gain) * rate;
            float l = buf[2 * i] * gain, r = buf[2 * i + 1] * gain;
            buf[2 * i] = l;
            buf[2 * i + 1] = r;
            float a = Math.Max(Math.Abs(l), Math.Abs(r));
            if (a > peak) peak = a;
        }
        return peak;
    }

    private static float SoftClip(float x)
    {
        const float t = 0.9f;
        float a = Math.Abs(x);
        if (a <= t) return x;
        float y = t + (1 - t) * MathF.Tanh((a - t) / (1 - t));
        return x < 0 ? -y : y;
    }

    /// <summary>Stops mixing and flushes the AAC encoders.</summary>
    public void Stop()
    {
        if (_stop) return;
        _stop = true;
        _thread.Join(2000);
        _mixEncoder.Flush();
        _desktopEncoder?.Flush();
        _micEncoder?.Flush();
    }

    public void Dispose()
    {
        Stop();
        lock (_srcLock)
        {
            _desktop?.Dispose();
            _mic?.Dispose();
            _desktop = _mic = null;
        }
    }
}
