using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Framelock.Core;
using Framelock.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace Framelock.Encoding;

public sealed record VideoEncoderSettings(RateControlMode RateControl, int Quality, int BitrateMbps, SpeedPreset Speed, int KeyframeSeconds, bool BFrames);

/// <summary>
/// H.264/HEVC/AV1 encoder. NVENC/AMF take the compositor's NV12 texture zero-copy through an FFmpeg D3D11 frame pool;
/// QSV/CPU encoders get a staged system-memory copy. Either way the encoder itself runs on a worker thread, so capture
/// never blocks: under a saturated GPU (a game at 100%) one hardware submit can wait a whole game frame for the GPU.
/// Encode() must be called from the video thread.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    private const uint D3D11_BIND_RENDER_TARGET = 0x20;

    public EncoderInfo Info { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public AVRational TimeBase { get; }
    public long FramesSubmitted => Interlocked.Read(ref _framesSubmitted);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public string Description { get; }
    public AVCodecContext* Context => _ctx;

    private readonly D3DContext _d3d;
    private readonly Action<EncodedPacket> _output;
    private AVCodecContext* _ctx;
    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVFrame* _hwFrame;
    private readonly bool _zeroCopy;
    private readonly Dictionary<IntPtr, ID3D11Texture2D> _poolTextures = new();
    private long _framesSubmitted, _framesDropped, _bytesOut;
    private bool _flushed;

    // ---- encoder worker (both paths) ----
    private readonly BlockingCollection<IntPtr> _queue; // AVFrame*; completing it flushes the encoder
    private readonly Thread _worker;
    private volatile Exception? _workerError;
    private bool _workerExited;

    // ---- system-memory path ----
    private readonly ID3D11Texture2D[]? _staging;
    private readonly (long pts, bool key, bool used)[]? _stagingInfo;
    private int _stagingWrite;
    private readonly bool _planar; // encoder wants yuv420p instead of nv12
    private readonly ConcurrentBag<IntPtr>? _framePool;

    public VideoEncoder(EncoderInfo info, D3DContext d3d, int width, int height, int fps, VideoEncoderSettings s, bool nv12Input, Action<EncodedPacket> output)
    {
        if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("Output size must be even");
        if (Math.Max(width, height) > info.MaxDimension)
            throw new FFmpegException($"{info.CodecLabel} supports at most {info.MaxDimension} px per side. Pick HEVC or AV1 for {width}×{height}.");

        Info = info;
        _d3d = d3d;
        Width = width;
        Height = height;
        Fps = fps;
        _output = output;
        TimeBase = new AVRational { num = 1, den = fps };
        _zeroCopy = info.UsesD3D11Frames;
        if (!_zeroCopy && !nv12Input) throw new FFmpegException("This GPU cannot produce NV12 frames for CPU/QSV encoding.");

        var codec = ffmpeg.avcodec_find_encoder_by_name(info.Id);
        if (codec == null) throw new FFmpegException($"Encoder {info.Id} is not in this FFmpeg build");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        try
        {
            _ctx->width = width;
            _ctx->height = height;
            _ctx->time_base = TimeBase;
            _ctx->framerate = new AVRational { num = fps, den = 1 };
            _ctx->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
            _ctx->gop_size = Math.Max(1, fps * Math.Clamp(s.KeyframeSeconds, 1, 10));
            _ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            _ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            _ctx->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            _ctx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            _ctx->chroma_sample_location = AVChromaLocation.AVCHROMA_LOC_LEFT;
            _ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            int lookahead = ConfigureRateAndOptions(s);

            if (_zeroCopy)
            {
                SetupHwFrames(nv12Input, lookahead + (s.BFrames ? 4 : 0));
                _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                _ctx->sw_pix_fmt = nv12Input ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA;
                _ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);
            }
            else
            {
                _planar = info.Id is "libx265" or "libsvtav1";
                _ctx->pix_fmt = _planar ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12;
                if (info.Family == EncoderFamily.Software) _ctx->thread_count = 0;
            }

            ffmpeg.avcodec_open2(_ctx, codec, null).Check($"Opening {info.Id}");
        }
        catch
        {
            FreeContext();
            throw;
        }

        if (_zeroCopy)
        {
            _hwFrame = ffmpeg.av_frame_alloc();
        }
        else
        {
            _staging = new ID3D11Texture2D[3];
            _stagingInfo = new (long, bool, bool)[3];
            for (int i = 0; i < _staging.Length; i++)
                _staging[i] = d3d.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            _framePool = new ConcurrentBag<IntPtr>();
        }
        // Zero-copy frames are pool textures (FFmpeg's pool is the real bound); system-memory frames are big, keep few.
        _queue = new BlockingCollection<IntPtr>(new ConcurrentQueue<IntPtr>(), _zeroCopy ? 10 : 6);
        _worker = new Thread(WorkerLoop) { Name = "Framelock video encoder", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _worker.Start();

        Description = $"{info.Label} {width}×{height}@{fps} {s.RateControl} q{s.Quality} {s.Speed}";
        Log.Info("Video encoder opened: " + Description);
    }

    // ------------------------------------------------------------------ options

    private void Opt(string name, string value)
    {
        int r = ffmpeg.av_opt_set(_ctx->priv_data, name, value, 0);
        if (r < 0) Log.Debug($"{Info.Id}: option {name}={value} not applied ({FFmpegSetup.ErrorText(r)})");
    }

    private int ConfigureRateAndOptions(VideoEncoderSettings s)
    {
        long bitrate = (long)Math.Clamp(s.BitrateMbps, 1, 1000) * 1_000_000;
        int q = Math.Clamp(s.Quality, 0, 51);
        int lookahead = 0;
        _ctx->max_b_frames = s.BFrames ? 2 : 0;

        switch (Info.Family)
        {
            case EncoderFamily.Nvenc:
            {
                Opt("preset", s.Speed switch { SpeedPreset.Performance => "p2", SpeedPreset.Balanced => "p4", SpeedPreset.Quality => "p5", _ => "p7" });
                Opt("tune", "hq");
                Opt("multipass", s.Speed switch { SpeedPreset.Performance => "disabled", SpeedPreset.MaxQuality => "fullres", _ => "qres" });
                lookahead = s.Speed switch { SpeedPreset.Performance => 0, SpeedPreset.Balanced => 8, SpeedPreset.Quality => 16, _ => 20 };
                if (lookahead > 0) Opt("rc-lookahead", lookahead.ToString());
                if (s.Speed != SpeedPreset.Performance)
                {
                    Opt("spatial-aq", "1");
                    Opt("temporal-aq", "1");
                    Opt("aq-strength", "8");
                }
                if (s.BFrames) Opt("b_ref_mode", "middle");
                Opt("forced-idr", "1");
                Opt("zerolatency", "0");
                if (Info.Codec == VideoCodec.Hevc) Opt("profile", "main");
                if (Info.Codec == VideoCodec.H264) Opt("profile", "high");
                switch (s.RateControl)
                {
                    case RateControlMode.ConstantQuality:
                        Opt("rc", "vbr");
                        _ctx->bit_rate = 0;
                        Opt("cq", (Info.Codec == VideoCodec.Av1 ? Math.Min(63, (int)Math.Round(q * 63 / 51.0)) : q).ToString());
                        break;
                    case RateControlMode.Cbr:
                        Opt("rc", "cbr");
                        _ctx->bit_rate = bitrate; _ctx->rc_max_rate = bitrate; _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
                        break;
                    default:
                        Opt("rc", "vbr");
                        _ctx->bit_rate = bitrate; _ctx->rc_max_rate = bitrate * 3 / 2; _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
                        break;
                }
                break;
            }
            case EncoderFamily.Amf:
            {
                Opt("usage", "transcoding");
                Opt("quality", s.Speed switch { SpeedPreset.Performance => "speed", SpeedPreset.Balanced => "balanced", _ => "quality" });
                if (Info.Codec != VideoCodec.H264) _ctx->max_b_frames = 0; // HEVC/AV1 B-frames only on newest AMD GPUs
                if (s.Speed >= SpeedPreset.Quality) Opt("preanalysis", "1");
                Opt("enforce_hrd", "0");
                // Cuts need forced keyframes to be IDR: AMF only flags IDRs as key, so a plain forced I-frame is ignored and
                // start/stop snap to the next natural GOP boundary (up to KeyframeSeconds late).
                Opt("forced_idr", "1");
                switch (s.RateControl)
                {
                    case RateControlMode.ConstantQuality:
                    {
                        Opt("rc", "cqp");
                        bool av1 = Info.Codec == VideoCodec.Av1;
                        int Scale(int v) => av1 ? Math.Clamp((int)Math.Round(v * 255 / 51.0), 0, 255) : Math.Clamp(v, 0, 51);
                        Opt("qp_i", Scale(q).ToString());
                        Opt("qp_p", Scale(q + 2).ToString());
                        if (_ctx->max_b_frames > 0) Opt("qp_b", Scale(q + 4).ToString());
                        break;
                    }
                    case RateControlMode.Cbr:
                        Opt("rc", "cbr");
                        _ctx->bit_rate = bitrate; _ctx->rc_max_rate = bitrate; _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
                        break;
                    default:
                        Opt("rc", "vbr_peak");
                        _ctx->bit_rate = bitrate; _ctx->rc_max_rate = bitrate * 3 / 2; _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
                        break;
                }
                break;
            }
            case EncoderFamily.Qsv:
            {
                Opt("preset", s.Speed switch { SpeedPreset.Performance => "veryfast", SpeedPreset.Balanced => "medium", SpeedPreset.Quality => "slow", _ => "veryslow" });
                Opt("forced_idr", "1"); // same as AMF: only IDRs are flagged key
                if (s.RateControl == RateControlMode.ConstantQuality) _ctx->global_quality = q;
                else { _ctx->bit_rate = bitrate; _ctx->rc_max_rate = s.RateControl == RateControlMode.Cbr ? bitrate : bitrate * 3 / 2; }
                break;
            }
            default:
            {
                if (Info.Id == "libsvtav1")
                {
                    Opt("preset", s.Speed switch { SpeedPreset.Performance => "12", SpeedPreset.Balanced => "10", SpeedPreset.Quality => "8", _ => "6" });
                    if (s.RateControl == RateControlMode.ConstantQuality) Opt("crf", Math.Min(63, (int)Math.Round(q * 63 / 51.0)).ToString());
                    else _ctx->bit_rate = bitrate;
                }
                else
                {
                    Opt("preset", s.Speed switch { SpeedPreset.Performance => "ultrafast", SpeedPreset.Balanced => "veryfast", SpeedPreset.Quality => "fast", _ => "medium" });
                    if (Info.Id == "libx265") Opt("x265-params", "log-level=error");
                    switch (s.RateControl)
                    {
                        case RateControlMode.ConstantQuality: Opt("crf", q.ToString()); break;
                        case RateControlMode.Cbr:
                            _ctx->bit_rate = bitrate; _ctx->rc_max_rate = bitrate; _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
                            if (Info.Id == "libx264") Opt("nal-hrd", "cbr");
                            break;
                        default:
                            _ctx->bit_rate = bitrate; _ctx->rc_max_rate = bitrate * 3 / 2; _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
                            break;
                    }
                }
                break;
            }
        }
        return lookahead;
    }

    private void SetupHwFrames(bool nv12, int extraFrames)
    {
        _hwDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (_hwDevice == null) throw new FFmpegException("av_hwdevice_ctx_alloc failed");
        var devCtx = (AVHWDeviceContext*)_hwDevice->data;
        var d3dCtx = (AVD3D11VADeviceContext*)devCtx->hwctx;
        // FFmpeg releases the device when the context is freed - give it its own reference.
        Marshal.AddRef(_d3d.Device.NativePointer);
        d3dCtx->device = (FFmpeg.AutoGen.ID3D11Device*)_d3d.Device.NativePointer;
        ffmpeg.av_hwdevice_ctx_init(_hwDevice).Check("D3D11 hw device init");

        long frameBytes = (long)Width * Height * (nv12 ? 3 : 8) / 2;
        int pool = Math.Max(20, 12 + extraFrames);
        if (frameBytes > 40_000_000) pool = Math.Max(12, pool / 2); // 8K: keep VRAM sane
        // Preferred: one texture array (static pool). Some drivers reject NV12 arrays (E_INVALIDARG) - then let FFmpeg
        // allocate single textures on demand (bounded by the encoder's own queue).
        if (!s_arrayPoolRejected && TryInitFrames(nv12, pool, 0)) return;
        s_arrayPoolRejected = true; // don't retry (and re-log) on every encoder open
        if (!TryInitFrames(nv12, 0, D3D11_BIND_RENDER_TARGET))
            throw new FFmpegException("D3D11 frame pool init failed");
    }

    private static bool s_arrayPoolRejected;

    private bool TryInitFrames(bool nv12, int poolSize, uint bindFlags)
    {
        _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
        if (_hwFrames == null) throw new FFmpegException("av_hwframe_ctx_alloc failed");
        var frames = (AVHWFramesContext*)_hwFrames->data;
        frames->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        frames->sw_format = nv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA;
        frames->width = Width;
        frames->height = Height;
        frames->initial_pool_size = poolSize;
        var fctx = (AVD3D11VAFramesContext*)frames->hwctx;
        fctx->BindFlags = bindFlags;
        int r = ffmpeg.av_hwframe_ctx_init(_hwFrames);
        if (r >= 0) return true;
        Log.Debug($"D3D11 frame pool (array={poolSize}, bind=0x{bindFlags:X}) failed: {FFmpegSetup.ErrorText(r)}");
        fixed (AVBufferRef** pf = &_hwFrames) ffmpeg.av_buffer_unref(pf);
        return false;
    }

    // ------------------------------------------------------------------ encode

    /// <summary>Encodes one output frame. <paramref name="pts"/> is the frame index since pipeline start.</summary>
    public void Encode(ID3D11Texture2D source, long pts, bool forceKeyframe)
    {
        if (_flushed) return;
        if (_zeroCopy) EncodeHardware(source, pts, forceKeyframe);
        else EncodeStaged(source, pts, forceKeyframe);
    }

    private void EncodeHardware(ID3D11Texture2D source, long pts, bool key)
    {
        if (_workerError != null) throw new FFmpegException("Encoder failed: " + _workerError.Message);
        int r = ffmpeg.av_hwframe_get_buffer(_hwFrames, _hwFrame, 0);
        if (r < 0)
        {
            Interlocked.Increment(ref _framesDropped);
            if (_framesDropped % 60 == 1) Log.Warn($"Encoder frame pool exhausted ({FFmpegSetup.ErrorText(r)}) - encoder can't keep up");
            return;
        }
        AVFrame* queued = null;
        try
        {
            var texPtr = (IntPtr)_hwFrame->data[0];
            int index = (int)(IntPtr)_hwFrame->data[1];
            if (!_poolTextures.TryGetValue(texPtr, out var poolTex))
            {
                Marshal.AddRef(texPtr);
                poolTex = new ID3D11Texture2D(texPtr);
                _poolTextures[texPtr] = poolTex;
            }
            _d3d.Context.CopySubresourceRegion(poolTex, (uint)index, 0, 0, 0, source, 0, null);

            _hwFrame->pts = pts;
            _hwFrame->pict_type = key ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
            _hwFrame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            _hwFrame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            _hwFrame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            _hwFrame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            // Hand the frame (it holds its pool texture) to the worker; the copy above is already queued on the GPU.
            queued = ffmpeg.av_frame_alloc();
            ffmpeg.av_frame_move_ref(queued, _hwFrame);
            if (!_queue.TryAdd((IntPtr)queued))
            {
                Interlocked.Increment(ref _framesDropped);
                if (_framesDropped % 60 == 1) Log.Warn("Encoder queue full - encoder can't keep up");
                return;
            }
            queued = null;
            Interlocked.Increment(ref _framesSubmitted);
        }
        finally
        {
            ffmpeg.av_frame_unref(_hwFrame);
            if (queued != null) ffmpeg.av_frame_free(&queued);
        }
    }

    private void Send(AVFrame* frame)
    {
        int r = ffmpeg.avcodec_send_frame(_ctx, frame);
        if (r == FFmpegSetup.AVERROR_EAGAIN)
        {
            Drain();
            r = ffmpeg.avcodec_send_frame(_ctx, frame);
        }
        if (r < 0 && r != ffmpeg.AVERROR_EOF) r.Check($"{Info.Id} send_frame");
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            var pkt = ffmpeg.av_packet_alloc();
            int r = ffmpeg.avcodec_receive_packet(_ctx, pkt);
            if (r < 0)
            {
                ffmpeg.av_packet_free(&pkt);
                if (r == FFmpegSetup.AVERROR_EAGAIN || r == ffmpeg.AVERROR_EOF) return;
                r.Check($"{Info.Id} receive_packet");
            }
            if (pkt->duration <= 0) pkt->duration = 1;
            Interlocked.Add(ref _bytesOut, pkt->size);
            _output(new EncodedPacket(pkt, 0, TimeBase));
        }
    }

    // ---- staged (system memory) path ----

    private void EncodeStaged(ID3D11Texture2D source, long pts, bool key)
    {
        if (_workerError != null) throw new FFmpegException("Encoder failed: " + _workerError.Message);
        var staging = _staging!;
        var info = _stagingInfo!;
        int slot = _stagingWrite % staging.Length;
        if (info[slot].used) ReadbackSlot(slot); // oldest frame (2 frames ago) - GPU is long done with it
        _d3d.Context.CopyResource(staging[slot], source);
        info[slot] = (pts, key, true);
        _stagingWrite++;
    }

    private void ReadbackSlot(int slot)
    {
        var info = _stagingInfo![slot];
        _stagingInfo[slot].used = false;
        if (!_framePool!.TryTake(out var fp))
        {
            if (_queue.Count >= 6) { Interlocked.Increment(ref _framesDropped); return; }
            var nf = ffmpeg.av_frame_alloc();
            nf->format = (int)_ctx->pix_fmt;
            nf->width = Width;
            nf->height = Height;
            ffmpeg.av_frame_get_buffer(nf, 64).Check("av_frame_get_buffer");
            fp = (IntPtr)nf;
        }
        var f = (AVFrame*)fp;
        ffmpeg.av_frame_make_writable(f);

        var m = _d3d.Context.Map(_staging![slot], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* src = (byte*)m.DataPointer;
            int pitch = (int)m.RowPitch;
            // Luma
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + (long)y * pitch, f->data[0] + (long)y * f->linesize[0], Width, Width);
            byte* uv = src + (long)pitch * Height;
            if (_planar)
            {
                int cw = Width / 2;
                for (int y = 0; y < Height / 2; y++)
                {
                    byte* s = uv + (long)y * pitch;
                    byte* u = f->data[1] + (long)y * f->linesize[1];
                    byte* v = f->data[2] + (long)y * f->linesize[2];
                    for (int x = 0; x < cw; x++) { u[x] = s[2 * x]; v[x] = s[2 * x + 1]; }
                }
            }
            else
            {
                for (int y = 0; y < Height / 2; y++)
                    Buffer.MemoryCopy(uv + (long)y * pitch, f->data[1] + (long)y * f->linesize[1], Width, Width);
            }
        }
        finally { _d3d.Context.Unmap(_staging[slot], 0); }

        f->pts = info.pts;
        f->pict_type = info.key ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        f->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        f->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        f->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        f->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        if (!_queue.TryAdd(fp))
        {
            Interlocked.Increment(ref _framesDropped);
            _framePool.Add(fp);
            return;
        }
        Interlocked.Increment(ref _framesSubmitted);
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var fp in _queue.GetConsumingEnumerable())
            {
                try { Send((AVFrame*)fp); }
                finally
                {
                    if (_framePool != null) _framePool.Add(fp);
                    else { var f = (AVFrame*)fp; ffmpeg.av_frame_free(&f); } // releases the pool texture
                }
            }
            // Flush() completed the queue and every frame is in: push the encoder's delayed frames out.
            SendFlush();
        }
        catch (Exception ex)
        {
            _workerError = ex;
            Log.Error("Video encoder worker failed", ex);
        }
    }

    private void SendFlush()
    {
        ffmpeg.avcodec_send_frame(_ctx, null);
        Drain();
    }

    /// <summary>Flushes delayed frames out of the encoder. After this the encoder accepts no more input.</summary>
    public void Flush()
    {
        if (_flushed) return;
        _flushed = true;
        try
        {
            if (!_zeroCopy)
            {
                // Read back what is still sitting in the staging ring, oldest first.
                for (int i = 0; i < _staging!.Length; i++)
                {
                    int slot = (_stagingWrite + i) % _staging.Length;
                    if (_stagingInfo![slot].used) ReadbackSlot(slot);
                }
            }
        }
        catch (Exception ex) { Log.Error("Encoder flush failed", ex); }
        _queue.CompleteAdding();
        _workerExited = _worker.Join(10_000);
        if (!_workerExited) Log.Warn("Encoder worker didn't finish in time");
    }

    private void FreeContext()
    {
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_hwFrames != null) { var b = _hwFrames; ffmpeg.av_buffer_unref(&b); _hwFrames = null; }
        if (_hwDevice != null) { var b = _hwDevice; ffmpeg.av_buffer_unref(&b); _hwDevice = null; }
    }

    public void Dispose()
    {
        Flush();
        if (!_workerExited) _workerExited = _worker.Join(5_000);
        if (_staging != null) foreach (var t in _staging) t?.Dispose();
        if (_hwFrame != null) { var f = _hwFrame; ffmpeg.av_frame_free(&f); _hwFrame = null; }
        if (!_workerExited)
        {
            // Stuck inside the driver. Freeing the encoder under it would crash the app, so leave its state allocated.
            Log.Warn("Encoder worker is stuck; leaving its encoder state allocated");
            return;
        }
        while (_queue.TryTake(out var q)) { var f = (AVFrame*)q; ffmpeg.av_frame_free(&f); }
        if (_framePool != null)
            while (_framePool.TryTake(out var p)) { var f = (AVFrame*)p; ffmpeg.av_frame_free(&f); }
        foreach (var t in _poolTextures.Values) t.Dispose();
        _poolTextures.Clear();
        FreeContext();
    }
}
