using FFmpeg.AutoGen;
using Framelock.Core;

namespace Framelock.Encoding;

/// <summary>AAC-LC 48 kHz stereo. Input is interleaved float blocks of <see cref="FrameSize"/> samples; pts = sample index.</summary>
public sealed unsafe class AudioEncoder : IDisposable
{
    public const int SampleRate = 48000;
    public int Track { get; }
    public string Name { get; }
    public int FrameSize { get; }
    public AVRational TimeBase { get; } = new() { num = 1, den = SampleRate };
    public AVCodecContext* Context => _ctx;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private readonly Action<EncodedPacket> _output;
    private bool _flushed;

    public AudioEncoder(int track, string name, int bitrateKbps, Action<EncodedPacket> output)
    {
        Track = track;
        Name = name;
        _output = output;
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null) throw new FFmpegException("AAC encoder missing from FFmpeg build");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_rate = SampleRate;
        _ctx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _ctx->bit_rate = Math.Clamp(bitrateKbps, 64, 512) * 1000L;
        _ctx->time_base = TimeBase;
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, 2);
        _ctx->ch_layout = layout;
        ffmpeg.av_opt_set(_ctx->priv_data, "aac_coder", "twoloop", 0);
        int r = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (r < 0)
        {
            var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null;
            r.Check("Opening AAC encoder");
        }
        FrameSize = _ctx->frame_size > 0 ? _ctx->frame_size : 1024;

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _frame->nb_samples = FrameSize;
        _frame->sample_rate = SampleRate;
        _frame->ch_layout = layout;
        ffmpeg.av_frame_get_buffer(_frame, 0).Check("Audio frame alloc");
    }

    /// <summary>Encodes one block of <see cref="FrameSize"/> interleaved stereo float samples at sample position <paramref name="pts"/>.</summary>
    public void Encode(ReadOnlySpan<float> interleaved, long pts)
    {
        if (_flushed) return;
        ffmpeg.av_frame_make_writable(_frame).Check("audio frame writable");
        float* l = (float*)_frame->data[0];
        float* r = (float*)_frame->data[1];
        int n = Math.Min(FrameSize, interleaved.Length / 2);
        for (int i = 0; i < n; i++)
        {
            l[i] = interleaved[2 * i];
            r[i] = interleaved[2 * i + 1];
        }
        for (int i = n; i < FrameSize; i++) { l[i] = 0; r[i] = 0; }
        _frame->pts = pts;
        Send(_frame);
    }

    private void Send(AVFrame* f)
    {
        int r = ffmpeg.avcodec_send_frame(_ctx, f);
        if (r == FFmpegSetup.AVERROR_EAGAIN) { Drain(); r = ffmpeg.avcodec_send_frame(_ctx, f); }
        if (r < 0 && r != ffmpeg.AVERROR_EOF) Log.Warn($"AAC send_frame: {FFmpegSetup.ErrorText(r)}");
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            var pkt = ffmpeg.av_packet_alloc();
            int r = ffmpeg.avcodec_receive_packet(_ctx, pkt);
            if (r < 0) { ffmpeg.av_packet_free(&pkt); return; }
            _output(new EncodedPacket(pkt, Track, TimeBase));
        }
    }

    public void Flush()
    {
        if (_flushed || _ctx == null) return;
        _flushed = true;
        Send(null);
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}
