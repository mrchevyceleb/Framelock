using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Framelock.Core;

namespace Framelock.Encoding;

public enum AudioRole { Mix, Desktop, Mic, Other }

public sealed record AudioTrackInfo(int StreamIndex, string Name, AudioRole Role, int BitrateKbps);

/// <summary>What a finished recording contains (read with <see cref="MediaFile.Probe"/>).</summary>
public sealed record MediaInfo(string Path, double DurationSeconds, int Width, int Height, double Fps, string VideoCodec, IReadOnlyList<AudioTrackInfo> Audio)
{
    public AudioTrackInfo? Desktop => Audio.FirstOrDefault(a => a.Role == AudioRole.Desktop);
    public AudioTrackInfo? Mic => Audio.FirstOrDefault(a => a.Role == AudioRole.Mic);
    public AudioTrackInfo? Mix => Audio.FirstOrDefault(a => a.Role == AudioRole.Mix);
    /// <summary>Game and mic were kept as their own tracks, so the mix can be rebuilt with new levels.</summary>
    public bool HasSeparateTracks => Desktop != null && Mic != null;
    public bool HasVideo => Width > 0;
}

public static unsafe class MediaFile
{
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".mov" };

    public static MediaInfo Probe(string path)
    {
        AVFormatContext* fmt = null;
        ffmpeg.avformat_open_input(&fmt, path, null, null).Check("Opening the file");
        try
        {
            ffmpeg.avformat_find_stream_info(fmt, null).Check("Reading the file");
            int w = 0, h = 0;
            double fps = 0, dur = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            string codec = "";
            var audio = new List<AudioTrackInfo>();
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                var par = st->codecpar;
                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && w == 0 && (st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    w = par->width;
                    h = par->height;
                    var r = st->avg_frame_rate.num > 0 ? st->avg_frame_rate : st->r_frame_rate;
                    fps = r.den > 0 ? r.num / (double)r.den : 0;
                    codec = ffmpeg.avcodec_get_name(par->codec_id) ?? "";
                    if (dur <= 0 && st->duration > 0) dur = st->duration * ffmpeg.av_q2d(st->time_base);
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    string name = Tag(st->metadata, "title") ?? Tag(st->metadata, "handler_name") ?? "";
                    if (name is "SoundHandler" or "") name = $"Track {audio.Count + 1}";
                    audio.Add(new AudioTrackInfo(i, name, RoleOf(name), (int)(par->bit_rate / 1000)));
                }
            }
            return new MediaInfo(path, dur, w, h, fps, codec, audio);
        }
        finally { ffmpeg.avformat_close_input(&fmt); }
    }

    internal static string? Tag(AVDictionary* dict, string key)
    {
        var e = ffmpeg.av_dict_get(dict, key, null, 0);
        return e == null ? null : Marshal.PtrToStringUTF8((IntPtr)e->value);
    }

    private static AudioRole RoleOf(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.StartsWith("mix") || n == "audio") return AudioRole.Mix;
        if (n.Contains("desktop") || n.Contains("system") || n.StartsWith("game")) return AudioRole.Desktop;
        if (n.Contains("microphone") || n == "mic" || n.StartsWith("mic ")) return AudioRole.Mic;
        return AudioRole.Other;
    }
}

/// <summary>Decodes one audio stream to interleaved float, 48 kHz stereo, placed on a sample timeline.</summary>
internal sealed unsafe class TrackDecoder : IDisposable
{
    public const int Rate = AudioEncoder.SampleRate;
    private static readonly AVRational SampleTb = new() { num = 1, den = Rate };

    public int StreamIndex { get; }
    private AVCodecContext* _ctx;
    private SwrContext* _swr;
    private AVFrame* _frame;
    private readonly AVRational _tb;
    private float[] _buf = new float[8192];
    private long _next = long.MinValue;
    private int _inFormat = -1, _inRate, _inChannels;

    public TrackDecoder(AVFormatContext* fmt, int streamIndex)
    {
        StreamIndex = streamIndex;
        if (streamIndex < 0 || streamIndex >= fmt->nb_streams || fmt->streams[streamIndex]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
            throw new InvalidOperationException("The recording changed since it was opened. Close this window and open it again.");
        var st = fmt->streams[streamIndex];
        _tb = st->time_base;
        var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null) throw new FFmpegException($"No decoder for {ffmpeg.avcodec_get_name(st->codecpar->codec_id)} audio");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        try
        {
            ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar).Check("Audio decoder setup");
            _ctx->pkt_timebase = st->time_base;
            ffmpeg.avcodec_open2(_ctx, codec, null).Check("Opening the audio decoder");
        }
        catch { Dispose(); throw; }
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>How far one track may run ahead of another that has no audio there (a gap, or a track that ended early)
    /// before the silent one counts as silence. Bounds the memory a gap can pin.</summary>
    public const long GapSlack = Rate * 10;

    /// <summary>A packet's timeline position in samples, or long.MinValue when it has no timestamp.</summary>
    public long PositionOf(AVPacket* pkt) => pkt->pts != ffmpeg.AV_NOPTS_VALUE ? ffmpeg.av_rescale_q(pkt->pts, _tb, SampleTb) : long.MinValue;

    /// <summary>Decodes a packet (null drains the decoder at end of file) into <paramref name="queue"/>.</summary>
    public void Decode(AVPacket* pkt, SampleQueue queue)
    {
        // EAGAIN: the decoder is holding frames. Once they're taken it must accept the packet, so one retry is enough.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int r = ffmpeg.avcodec_send_packet(_ctx, pkt);
            if (r < 0 && r != ffmpeg.AVERROR_EOF && r != FFmpegSetup.AVERROR_EAGAIN) Log.Debug($"audio decode: {FFmpegSetup.ErrorText(r)}");
            while (ffmpeg.avcodec_receive_frame(_ctx, _frame) >= 0)
            {
                try { Convert(queue); }
                finally { ffmpeg.av_frame_unref(_frame); }
            }
            if (r != FFmpegSetup.AVERROR_EAGAIN) break;
        }
    }

    private void Convert(SampleQueue queue)
    {
        var f = _frame;
        if (f->nb_samples <= 0) return;
        if (_swr == null || f->format != _inFormat || f->sample_rate != _inRate || f->ch_layout.nb_channels != _inChannels)
        {
            if (_swr != null) { var old = _swr; ffmpeg.swr_free(&old); _swr = null; }
            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, 2);
            SwrContext* s = null;
            ffmpeg.swr_alloc_set_opts2(&s, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate, &f->ch_layout, (AVSampleFormat)f->format, f->sample_rate, 0, null).Check("Audio converter setup");
            int ir = ffmpeg.swr_init(s);
            if (ir < 0) { ffmpeg.swr_free(&s); ir.Check("Audio converter init"); }
            _swr = s;
            _inFormat = f->format; _inRate = f->sample_rate; _inChannels = f->ch_layout.nb_channels;
        }
        int max = ffmpeg.swr_get_out_samples(_swr, f->nb_samples);
        if (max <= 0) return;
        if (_buf.Length < max * 2) _buf = new float[max * 2];
        int n;
        fixed (float* p = _buf)
        {
            var o = (byte*)p;
            n = ffmpeg.swr_convert(_swr, &o, max, f->extended_data, f->nb_samples);
        }
        if (n <= 0) return;
        long ts = f->best_effort_timestamp;
        long pos = ts != ffmpeg.AV_NOPTS_VALUE ? ffmpeg.av_rescale_q(ts, _tb, SampleTb) : _next;
        if (pos == long.MinValue) pos = 0;
        // Timestamp rounding jitter must not open tiny gaps or overlaps between frames.
        if (_next != long.MinValue && Math.Abs(pos - _next) <= 32) pos = _next;
        queue.Push(pos, _buf, n);
        _next = pos + n;
    }

    /// <summary>After a seek: forget buffered state.</summary>
    public void Reset()
    {
        ffmpeg.avcodec_flush_buffers(_ctx);
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        _next = long.MinValue;
    }

    public void Dispose()
    {
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}

/// <summary>
/// Interleaved stereo samples keyed by timeline position (sample index). Pushes fill gaps with silence and drop
/// overlaps, so two tracks line up sample-for-sample however their packets arrive.
/// </summary>
internal sealed class SampleQueue
{
    private float[] _buf = new float[48000 * 2];
    private int _off, _count; // in frames
    public long Start { get; private set; }
    public bool Anchored { get; private set; }
    public bool Ended { get; set; }
    public long End => Start + _count;

    /// <summary>Starts the queue at <paramref name="pos"/>; anything decoded before it is dropped.</summary>
    public void Anchor(long pos) { Start = pos; _off = 0; _count = 0; Anchored = true; Ended = false; }

    public void Push(long pos, float[] src, int frames)
    {
        if (!Anchored) Anchor(pos);
        int skip = 0;
        if (pos < End) skip = (int)Math.Min(frames, End - pos);
        else if (pos > End)
        {
            long gap = pos - End;
            if (_count == 0) Start = pos; // nothing buffered: just move forward (reads before Start are silent)
            else if (gap > TrackDecoder.Rate * 60) { } // a bogus timestamp jump: keep the audio contiguous instead
            else
            {
                Ensure(_count + (int)gap);
                Array.Clear(_buf, (_off + _count) * 2, (int)gap * 2);
                _count += (int)gap;
            }
        }
        int n = frames - skip;
        if (n <= 0) return;
        Ensure(_count + n);
        Array.Copy(src, skip * 2, _buf, (_off + _count) * 2, n * 2);
        _count += n;
    }

    /// <summary>Copies [at, at+frames) into <paramref name="dst"/> (silence where there is no data) and drops everything before at+frames.</summary>
    public void Take(long at, float[] dst, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            long p = at + i;
            if (p >= Start && p < End)
            {
                int k = (_off + (int)(p - Start)) * 2;
                dst[2 * i] = _buf[k];
                dst[2 * i + 1] = _buf[k + 1];
            }
            else { dst[2 * i] = 0; dst[2 * i + 1] = 0; }
        }
        long upTo = at + frames;
        if (upTo <= Start) return;
        int drop = (int)Math.Min(_count, upTo - Start);
        _off += drop;
        _count -= drop;
        Start += drop;
        if (_count == 0) { _off = 0; Start = Math.Max(Start, upTo); }
    }

    private void Ensure(int frames)
    {
        int cap = _buf.Length / 2;
        if (_off + frames <= cap) return;
        if (frames <= cap / 2)
        {
            Array.Copy(_buf, _off * 2, _buf, 0, _count * 2);
            _off = 0;
            return;
        }
        var nb = new float[Math.Max(cap * 2, frames * 2) * 2];
        Array.Copy(_buf, _off * 2, nb, 0, _count * 2);
        _buf = nb;
        _off = 0;
    }
}
