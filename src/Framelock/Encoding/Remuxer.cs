using FFmpeg.AutoGen;
using Framelock.Core;

namespace Framelock.Encoding;

/// <summary>Lossless container conversion (the crash-safe MKV → final MP4/MOV step). Copies chapters and adds fast-start.</summary>
public static unsafe class Remuxer
{
    public static void Remux(string input, string output, ContainerFormat container, IProgress<double>? progress = null)
    {
        AVFormatContext* inCtx = null;
        AVFormatContext* outCtx = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            ffmpeg.avformat_open_input(&inCtx, input, null, null).Check($"Opening {input}");
            ffmpeg.avformat_find_stream_info(inCtx, null).Check("Reading stream info");
            string fmtName = container switch { ContainerFormat.Mkv => "matroska", ContainerFormat.Mov => "mov", _ => "mp4" };
            ffmpeg.avformat_alloc_output_context2(&outCtx, null, fmtName, output).Check("Creating output");

            var map = new int[inCtx->nb_streams];
            int next = 0;
            for (int i = 0; i < inCtx->nb_streams; i++)
            {
                var ist = inCtx->streams[i];
                var type = ist->codecpar->codec_type;
                if (type != AVMediaType.AVMEDIA_TYPE_VIDEO && type != AVMediaType.AVMEDIA_TYPE_AUDIO) { map[i] = -1; continue; }
                var ost = ffmpeg.avformat_new_stream(outCtx, null);
                ffmpeg.avcodec_parameters_copy(ost->codecpar, ist->codecpar).Check("Copying codec parameters");
                ost->codecpar->codec_tag = 0;
                if (container != ContainerFormat.Mkv && ost->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
                    ost->codecpar->codec_tag = (uint)('h' | ('v' << 8) | ('c' << 16) | ('1' << 24));
                ost->time_base = ist->time_base;
                ost->avg_frame_rate = ist->avg_frame_rate;
                ost->r_frame_rate = ist->r_frame_rate;
                ost->disposition = ist->disposition;
                ffmpeg.av_dict_copy(&ost->metadata, ist->metadata, 0);
                var title = ffmpeg.av_dict_get(ist->metadata, "title", null, 0);
                if (title != null && ist->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    ffmpeg.av_dict_set(&ost->metadata, "handler_name", System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)title->value), 0);
                map[i] = next++;
            }

            ffmpeg.av_dict_copy(&outCtx->metadata, inCtx->metadata, 0);
            // Chapters (markers) survive the conversion.
            if (inCtx->nb_chapters > 0)
            {
                var arr = (AVChapter**)ffmpeg.av_mallocz((ulong)(sizeof(AVChapter*) * inCtx->nb_chapters));
                for (int i = 0; i < inCtx->nb_chapters; i++)
                {
                    var src = inCtx->chapters[i];
                    var ch = (AVChapter*)ffmpeg.av_mallocz((ulong)sizeof(AVChapter));
                    ch->id = src->id;
                    ch->time_base = src->time_base;
                    ch->start = src->start;
                    ch->end = src->end;
                    ffmpeg.av_dict_copy(&ch->metadata, src->metadata, 0);
                    arr[i] = ch;
                }
                outCtx->chapters = arr;
                outCtx->nb_chapters = inCtx->nb_chapters;
            }

            AVIOContext* pb = null;
            ffmpeg.avio_open(&pb, output, ffmpeg.AVIO_FLAG_WRITE).Check($"Opening {output}");
            outCtx->pb = pb;
            AVDictionary* opts = null;
            if (container != ContainerFormat.Mkv) ffmpeg.av_dict_set(&opts, "movflags", "+faststart", 0);
            int hr = ffmpeg.avformat_write_header(outCtx, &opts);
            ffmpeg.av_dict_free(&opts);
            hr.Check("Writing header");

            long duration = inCtx->duration > 0 ? inCtx->duration : 1;
            long lastReport = 0;
            var lastDts = Enumerable.Repeat(long.MinValue, (int)outCtx->nb_streams).ToArray();
            while (ffmpeg.av_read_frame(inCtx, pkt) >= 0)
            {
                int si = pkt->stream_index;
                if (si >= map.Length || map[si] < 0) { ffmpeg.av_packet_unref(pkt); continue; }
                var ist = inCtx->streams[si];
                var ost = outCtx->streams[map[si]];
                if (progress != null && pkt->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    long us = ffmpeg.av_rescale_q(pkt->pts, ist->time_base, new AVRational { num = 1, den = 1_000_000 });
                    if (us - lastReport > 500_000) { lastReport = us; progress.Report(Math.Clamp((double)us / duration, 0, 1)); }
                }
                pkt->stream_index = map[si];
                ffmpeg.av_packet_rescale_ts(pkt, ist->time_base, ost->time_base);
                pkt->pos = -1;
                if (pkt->dts == ffmpeg.AV_NOPTS_VALUE) pkt->dts = pkt->pts;
                int oi = map[si];
                if (lastDts[oi] != long.MinValue && pkt->dts <= lastDts[oi]) pkt->dts = lastDts[oi] + 1;
                if (pkt->pts != ffmpeg.AV_NOPTS_VALUE && pkt->pts < pkt->dts) pkt->pts = pkt->dts;
                lastDts[oi] = pkt->dts;
                ffmpeg.av_interleaved_write_frame(outCtx, pkt).Check("Writing packet");
            }
            ffmpeg.av_write_trailer(outCtx).Check("Writing trailer");
            progress?.Report(1);
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (inCtx != null) ffmpeg.avformat_close_input(&inCtx);
            if (outCtx != null)
            {
                if (outCtx->pb != null) ffmpeg.avio_closep(&outCtx->pb);
                ffmpeg.avformat_free_context(outCtx);
            }
        }
    }
}
