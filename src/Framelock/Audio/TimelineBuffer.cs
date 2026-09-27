namespace Framelock.Audio;

/// <summary>
/// Stereo float ring buffer addressed by absolute sample position on the pipeline timeline. Capture threads write where the
/// audio belongs in time (from WASAPI QPC timestamps); the mixer reads fixed blocks a little behind real time. Unwritten
/// ranges read as silence, so gaps (e.g. loopback during silence) cost nothing.
/// </summary>
public sealed class TimelineBuffer
{
    private const int Capacity = 1 << 18; // frames (~5.4 s at 48 kHz)
    private const int Mask = Capacity - 1;
    private readonly float[] _data = new float[Capacity * 2];
    private readonly object _lock = new();
    private long _readPos;
    private long _lateFrames;

    public long ReadPosition { get { lock (_lock) return _readPos; } }
    public long LateFrames => Interlocked.Read(ref _lateFrames);

    public void Write(long pos, ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        if (frames == 0) return;
        lock (_lock)
        {
            int skip = 0;
            if (pos < _readPos)
            {
                skip = (int)Math.Min(frames, _readPos - pos);
                Interlocked.Add(ref _lateFrames, skip);
            }
            long maxPos = _readPos + Capacity;
            for (int i = skip; i < frames; i++)
            {
                long p = pos + i;
                if (p >= maxPos) break;
                int idx = (int)(p & Mask) * 2;
                _data[idx] = interleaved[2 * i];
                _data[idx + 1] = interleaved[2 * i + 1];
            }
        }
    }

    /// <summary>Reads [pos, pos+frames) into <paramref name="dest"/> and clears it so the ring never replays stale audio.</summary>
    public void Read(long pos, Span<float> dest)
    {
        int frames = dest.Length / 2;
        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                int idx = (int)((pos + i) & Mask) * 2;
                dest[2 * i] = _data[idx];
                dest[2 * i + 1] = _data[idx + 1];
                _data[idx] = 0;
                _data[idx + 1] = 0;
            }
            _readPos = pos + frames;
        }
    }

    /// <summary>Discards everything before <paramref name="pos"/> (used when a source starts late).</summary>
    public void SkipTo(long pos)
    {
        lock (_lock)
        {
            if (pos <= _readPos) return;
            long n = Math.Min(pos - _readPos, Capacity);
            for (long i = 0; i < n; i++)
            {
                int idx = (int)((_readPos + i) & Mask) * 2;
                _data[idx] = 0;
                _data[idx + 1] = 0;
            }
            _readPos = pos;
        }
    }
}
