using FFmpeg.AutoGen;
using Framelock.Core;
using Framelock.Encoding;
using NAudio.Wave;

namespace Framelock.Audio;

/// <summary>
/// Plays a recording's audio with live levels, so the new balance can be heard before exporting. Mixes exactly like
/// the export does (<see cref="AudioRemixer"/>). Framelock's own audio is excluded from capture, so previewing during
/// a recording never ends up in it.
/// </summary>
public sealed unsafe class RemixPreview : ISampleProvider, IDisposable
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(TrackDecoder.Rate, 2);
    public double DurationSeconds { get; }
    /// <summary>Post-gain peak of each source (same order as the levels), for meters.</summary>
    public float[] Peaks { get; }

    private readonly object _lock = new();
    private AVFormatContext* _fmt;
    private AVPacket* _pkt;
    private readonly TrackDecoder[] _dec;
    private readonly SampleQueue[] _q;
    private readonly int[] _srcIndex;
    private long _demuxPos = long.MinValue; // newest source packet read since the last seek (samples)
    private readonly float[] _gains;
    private float[] _tmp = new float[4096];
    private long _pos, _seekTo = 0;
    private bool _eof, _disposed;

    public RemixPreview(string path, MediaInfo info)
    {
        info = AudioCompanion.RefreshInfo(path, info);
        DurationSeconds = info.DurationSeconds;
        var (sources, _, _, _) = AudioRemixer.Plan(info, new RemixLevels(1, 1));
        AVFormatContext* fmt = null;
        ffmpeg.avformat_open_input(&fmt, info.AudioSourcePath ?? path, null, null).Check("Opening the recording audio");
        _fmt = fmt;
        try
        {
            ffmpeg.avformat_find_stream_info(_fmt, null).Check("Reading the recording");
            AudioCompanion.ValidateSource(_fmt, info);
            _srcIndex = new int[_fmt->nb_streams];
            Array.Fill(_srcIndex, -1);
            _dec = new TrackDecoder[sources.Count];
            _q = new SampleQueue[sources.Count];
            _gains = new float[sources.Count];
            Peaks = new float[sources.Count];
            for (int k = 0; k < sources.Count; k++)
            {
                _dec[k] = new TrackDecoder(_fmt, sources[k].Stream);
                _q[k] = new SampleQueue();
                _gains[k] = 1;
                _srcIndex[sources[k].Stream] = k;
            }
            // Only the audio we play is read; the (large) video packets are skipped by the demuxer.
            for (int i = 0; i < _fmt->nb_streams; i++)
                if (_srcIndex[i] < 0) _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            _pkt = ffmpeg.av_packet_alloc();
        }
        catch { Dispose(); throw; }
    }

    public void SetGain(int source, float gain) { if (source < _gains.Length) _gains[source] = Math.Max(0, gain); }

    public double Position
    {
        get
        {
            long s = Interlocked.Read(ref _seekTo);
            return (s >= 0 ? s : Interlocked.Read(ref _pos)) / (double)TrackDecoder.Rate;
        }
    }

    public bool AtEnd => _eof && _q.All(q => q.Ended) && Interlocked.Read(ref _pos) >= MaxEnd();

    public void Seek(double seconds) =>
        Interlocked.Exchange(ref _seekTo, (long)(Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds)) * TrackDecoder.Rate));

    private long MaxEnd() => _q.Where(q => q.Anchored).Select(q => q.End).DefaultIfEmpty(0).Max();

    public int Read(Span<float> buffer)
    {
        lock (_lock)
        {
            if (_disposed) return 0;
            long seek = Interlocked.Exchange(ref _seekTo, -1);
            if (seek >= 0) DoSeek(seek);
            int frames = buffer.Length / 2;
            try { Fill(_pos + frames); }
            catch (Exception ex) { Log.Warn("Preview decode: " + ex.Message); _eof = true; foreach (var q in _q) q.Ended = true; }

            if (_eof && _q.All(q => q.Ended) && _pos >= MaxEnd()) return 0; // finished: the player stops
            if (_tmp.Length < frames * 2) _tmp = new float[frames * 2];
            buffer[..(frames * 2)].Clear();
            for (int k = 0; k < _q.Length; k++)
            {
                _q[k].Take(_pos, _tmp, frames);
                float g = _gains[k], peak = 0;
                for (int i = 0; i < frames * 2; i++)
                {
                    float v = _tmp[i] * g;
                    buffer[i] += v;
                    float a = Math.Abs(v);
                    if (a > peak) peak = a;
                }
                Peaks[k] = Math.Max(peak, Peaks[k]);
            }
            for (int i = 0; i < frames * 2; i++) buffer[i] = AudioRemixer.Limit(buffer[i], _gains.Length == 1, _gains[0]);
            Interlocked.Exchange(ref _pos, _pos + frames);
            return frames * 2;
        }
    }

    /// <summary>Demuxes until every track has audio up to <paramref name="until"/> (or the file ends).</summary>
    private void Fill(long until)
    {
        // A track with no audio here (a gap, or it ended early) is silence once the others are well past it.
        while (!_eof && _demuxPos < until + TrackDecoder.GapSlack && _q.Any(q => !q.Ended && (!q.Anchored || q.End < until)))
        {
            int r = ffmpeg.av_read_frame(_fmt, _pkt);
            if (r < 0)
            {
                for (int k = 0; k < _dec.Length; k++) { _dec[k].Decode(null, _q[k]); _q[k].Ended = true; }
                _eof = true;
                return;
            }
            try
            {
                int si = _pkt->stream_index;
                if (si < _srcIndex.Length && _srcIndex[si] >= 0)
                {
                    var dec = _dec[_srcIndex[si]];
                    _demuxPos = Math.Max(_demuxPos, dec.PositionOf(_pkt));
                    dec.Decode(_pkt, _q[_srcIndex[si]]);
                }
            }
            finally { ffmpeg.av_packet_unref(_pkt); }
        }
    }

    private void DoSeek(long target)
    {
        // Land a little early: AAC needs the previous frame to decode the first one cleanly.
        long us = Math.Max(0, target * 1_000_000 / TrackDecoder.Rate - 200_000);
        int r = ffmpeg.av_seek_frame(_fmt, -1, us, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (r < 0) r = ffmpeg.av_seek_frame(_fmt, -1, us, ffmpeg.AVSEEK_FLAG_ANY);
        if (r < 0) Log.Debug("Preview seek: " + FFmpegSetup.ErrorText(r));
        for (int k = 0; k < _dec.Length; k++)
        {
            _dec[k].Reset();
            _q[k].Anchor(target);
        }
        _eof = false;
        _demuxPos = long.MinValue;
        Interlocked.Exchange(ref _pos, target);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_dec != null) foreach (var d in _dec) d?.Dispose();
            if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
            if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        }
    }
}
