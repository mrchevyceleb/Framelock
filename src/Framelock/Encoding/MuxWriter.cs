using System.IO;
using FFmpeg.AutoGen;
using Framelock.Core;

namespace Framelock.Encoding;

/// <summary>Codec parameters of one encoder output, owned copy (safe to use after the encoder is gone).</summary>
public sealed unsafe class StreamInfo : IDisposable
{
    public int Track { get; }
    public string Name { get; }
    public bool IsVideo => Track == 0;
    public AVRational TimeBase { get; }
    public AVRational FrameRate { get; }
    public AVCodecParameters* Parameters { get; private set; }

    public StreamInfo(int track, string name, AVCodecContext* ctx, AVRational frameRate)
    {
        Track = track;
        Name = name;
        TimeBase = ctx->time_base;
        FrameRate = frameRate;
        Parameters = ffmpeg.avcodec_parameters_alloc();
        ffmpeg.avcodec_parameters_from_context(Parameters, ctx).Check("avcodec_parameters_from_context");
    }

    public void Dispose()
    {
        if (Parameters == null) return;
        var p = Parameters;
        ffmpeg.avcodec_parameters_free(&p);
        Parameters = null;
    }
}

public readonly record struct Chapter(long StartMs, string Title);

/// <summary>Thin libavformat muxer wrapper: timestamp offsetting, monotonic DTS repair, chapters.</summary>
public sealed unsafe class MuxWriter : IDisposable
{
    private static readonly AVRational Us = new() { num = 1, den = 1_000_000 };

    private AVFormatContext* _fmt;
    private readonly Dictionary<int, int> _trackToStream = new();
    private readonly Dictionary<int, AVRational> _encoderTb = new();
    private readonly long[] _lastDts;
    private bool _headerWritten, _closed;
    private readonly bool _faststart;
    public string Path { get; }
    public long BytesWritten { get; private set; }
    public long MaxVideoEndUs { get; private set; }
    public int PacketsWritten { get; private set; }
    public bool HasVideo { get; private set; }

    public MuxWriter(string path, ContainerFormat container, IReadOnlyList<StreamInfo> streams, bool faststart = false, string? pairId = null)
    {
        Path = path;
        _faststart = faststart;
        string fmtName = container switch { ContainerFormat.Mkv => "matroska", ContainerFormat.Mov => "mov", _ => "mp4" };
        AVFormatContext* fmt = null;
        ffmpeg.avformat_alloc_output_context2(&fmt, null, fmtName, path).Check("avformat_alloc_output_context2");
        _fmt = fmt;
        try
        {
            foreach (var si in streams)
            {
                var st = ffmpeg.avformat_new_stream(_fmt, null);
                if (st == null) throw new FFmpegException("avformat_new_stream failed");
                ffmpeg.avcodec_parameters_copy(st->codecpar, si.Parameters).Check("avcodec_parameters_copy");
                st->codecpar->codec_tag = 0;
                if (container != ContainerFormat.Mkv && st->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
                    st->codecpar->codec_tag = MkTag('h', 'v', 'c', '1'); // plays in QuickTime/Apple + what YouTube expects
                st->time_base = si.TimeBase;
                if (si.IsVideo)
                {
                    st->avg_frame_rate = si.FrameRate;
                    st->r_frame_rate = si.FrameRate;
                }
                else
                {
                    // MKV reads "title"; MP4/MOV players (and editors like Premiere/Resolve) read the handler name.
                    ffmpeg.av_dict_set(&st->metadata, "title", si.Name, 0);
                    ffmpeg.av_dict_set(&st->metadata, "handler_name", si.Name, 0);
                }
                if (si.Track == 1) st->disposition |= ffmpeg.AV_DISPOSITION_DEFAULT;
                _trackToStream[si.Track] = st->index;
                _encoderTb[si.Track] = si.TimeBase;
            }
            _lastDts = Enumerable.Repeat(long.MinValue, (int)_fmt->nb_streams).ToArray();
            ffmpeg.av_dict_set(&_fmt->metadata, "encoder", "Framelock", 0);
            if (pairId != null) ffmpeg.av_dict_set(&_fmt->metadata, "comment", AudioCompanion.PairComment(pairId), 0);
        }
        catch
        {
            ffmpeg.avformat_free_context(_fmt);
            _fmt = null;
            throw;
        }
    }

    private static uint MkTag(char a, char b, char c, char d) => (uint)(a | (b << 8) | (c << 16) | (d << 24));

    private void EnsureHeader()
    {
        if (_headerWritten) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if ((_fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            AVIOContext* pb = null;
            ffmpeg.avio_open(&pb, Path, ffmpeg.AVIO_FLAG_WRITE).Check($"Opening {Path}");
            _fmt->pb = pb;
        }
        AVDictionary* opts = null;
        if (_faststart) ffmpeg.av_dict_set(&opts, "movflags", "+faststart", 0);
        try { ffmpeg.avformat_write_header(_fmt, &opts).Check("Writing file header"); }
        finally { ffmpeg.av_dict_free(&opts); }
        _headerWritten = true;
    }

    /// <summary>Writes a packet shifted by <paramref name="offsetUs"/> (timeline → file time). Consumes the packet.</summary>
    public void Write(EncodedPacket p, long offsetUs)
    {
        using (p)
        {
            if (!_trackToStream.TryGetValue(p.Track, out int si)) return;
            EnsureHeader();
            var pkt = p.Packet;
            var tb = _encoderTb[p.Track];
            long off = ffmpeg.av_rescale_q(offsetUs, Us, tb);
            pkt->pts -= off;
            if (pkt->dts != ffmpeg.AV_NOPTS_VALUE) pkt->dts -= off;
            pkt->stream_index = si;
            var st = _fmt->streams[si];
            ffmpeg.av_packet_rescale_ts(pkt, tb, st->time_base);
            if (pkt->dts == ffmpeg.AV_NOPTS_VALUE) pkt->dts = pkt->pts;
            if (_lastDts[si] != long.MinValue && pkt->dts <= _lastDts[si]) pkt->dts = _lastDts[si] + 1;
            if (pkt->pts < pkt->dts) pkt->pts = pkt->dts;
            _lastDts[si] = pkt->dts;

            if (p.IsVideo)
            {
                HasVideo = true;
                long endUs = p.PtsUs - offsetUs + Math.Max(p.DurationUs, 1);
                if (endUs > MaxVideoEndUs) MaxVideoEndUs = endUs;
            }
            BytesWritten += pkt->size;
            PacketsWritten++;
            ffmpeg.av_interleaved_write_frame(_fmt, pkt).Check("Writing packet");
        }
    }

    public void SetChapters(IReadOnlyList<Chapter> chapters, long totalMs)
    {
        if (_fmt == null || chapters.Count == 0) return;
        var arr = (AVChapter**)ffmpeg.av_realloc_array(_fmt->chapters, (ulong)chapters.Count, (ulong)sizeof(AVChapter*));
        if (arr == null) return;
        _fmt->chapters = arr;
        for (int i = 0; i < chapters.Count; i++)
        {
            var ch = (AVChapter*)ffmpeg.av_mallocz((ulong)sizeof(AVChapter));
            ch->id = i + 1;
            ch->time_base = new AVRational { num = 1, den = 1000 };
            ch->start = chapters[i].StartMs;
            ch->end = i + 1 < chapters.Count ? chapters[i + 1].StartMs : Math.Max(totalMs, chapters[i].StartMs + 1);
            ffmpeg.av_dict_set(&ch->metadata, "title", chapters[i].Title, 0);
            arr[i] = ch;
        }
        _fmt->nb_chapters = (uint)chapters.Count;
    }

    /// <summary>Writes the trailer and closes the file. Returns false when nothing was ever written.</summary>
    public bool Finish()
    {
        if (_closed) return _headerWritten;
        _closed = true;
        if (!_headerWritten) return false;
        int r = ffmpeg.av_write_trailer(_fmt);
        if (_fmt->pb != null) { var pb = _fmt->pb; ffmpeg.avio_closep(&pb); _fmt->pb = null; }
        r.Check($"Finalizing {Path}");
        return true;
    }

    public void Dispose()
    {
        if (_fmt == null) return;
        if (!_closed)
        {
            try { Finish(); } catch (Exception ex) { Log.Error("Closing file failed", ex); }
        }
        if (_fmt->pb != null) { var pb = _fmt->pb; ffmpeg.avio_closep(&pb); }
        ffmpeg.avformat_free_context(_fmt);
        _fmt = null;
    }
}
