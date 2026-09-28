using FFmpeg.AutoGen;

namespace Framelock.Encoding;

/// <summary>Grabs one frame of a video as small BGRA pixels (software decode, no GPU involved).</summary>
public static unsafe class Thumbnailer
{
    public sealed record Thumb(byte[] Bgra, int Width, int Height);

    public static Thumb? Grab(string path, int maxWidth, int maxHeight, double atSeconds)
    {
        AVFormatContext* fmt = null;
        AVCodecContext* ctx = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        SwsContext* sws = null;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;
            AVCodec* codec = null;
            int si = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (si < 0 || codec == null) return null;
            for (int i = 0; i < fmt->nb_streams; i++) if (i != si) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            var st = fmt->streams[si];
            ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar) < 0) return null;
            ctx->pkt_timebase = st->time_base;
            ctx->thread_count = Math.Min(Environment.ProcessorCount, 8);
            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0) return null;

            if (atSeconds > 0)
            {
                long ts = (long)(atSeconds / ffmpeg.av_q2d(st->time_base));
                if (st->start_time != ffmpeg.AV_NOPTS_VALUE) ts += st->start_time;
                ffmpeg.av_seek_frame(fmt, si, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            }

            bool got = false;
            for (int reads = 0; !got && reads < 2000; reads++)
            {
                int r = ffmpeg.av_read_frame(fmt, pkt);
                if (r < 0) { ffmpeg.avcodec_send_packet(ctx, null); got = ffmpeg.avcodec_receive_frame(ctx, frame) >= 0; break; }
                if (pkt->stream_index == si)
                {
                    ffmpeg.avcodec_send_packet(ctx, pkt); // EAGAIN only means a frame is waiting; it's taken right below
                    got = ffmpeg.avcodec_receive_frame(ctx, frame) >= 0;
                }
                ffmpeg.av_packet_unref(pkt);
            }
            if (!got || frame->width <= 0 || frame->height <= 0) return null;

            double scale = Math.Min((double)maxWidth / frame->width, (double)maxHeight / frame->height);
            int w = Math.Max(2, (int)Math.Round(frame->width * scale)), h = Math.Max(2, (int)Math.Round(frame->height * scale));
            sws = ffmpeg.sws_getContext(frame->width, frame->height, (AVPixelFormat)frame->format, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_AREA, null, null, null);
            if (sws == null) return null;
            var pixels = new byte[w * h * 4];
            fixed (byte* p = pixels)
            {
                var dst = new byte*[] { p, null, null, null };
                var dstStride = new[] { w * 4, 0, 0, 0 };
                ffmpeg.sws_scale(sws, frame->data.ToArray(), frame->linesize.ToArray(), 0, frame->height, dst, dstStride);
            }
            return new Thumb(pixels, w, h);
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&pkt);
            if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }
}
