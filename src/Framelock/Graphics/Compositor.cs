using System.Numerics;
using System.Runtime.InteropServices;
using Framelock.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Framelock.Graphics;

/// <summary>One overlay draw for this frame, in output pixels.</summary>
public readonly record struct OverlayDraw(OverlayBitmap Bitmap, float X, float Y, float W, float H, float Opacity);

/// <summary>Where the captured image lands in the output frame.</summary>
public readonly record struct SourceLayout(float DstX, float DstY, float DstW, float DstH, float U0, float V0, float U1, float V1, float RatioX, float RatioY)
{
    public static SourceLayout Compute(int srcW, int srcH, int outW, int outH, ScaleMode mode)
    {
        if (srcW <= 0 || srcH <= 0) return new(0, 0, outW, outH, 0, 0, 1, 1, 1, 1);
        double sa = (double)srcW / srcH, da = (double)outW / outH;
        switch (mode)
        {
            case ScaleMode.Stretch:
                return new(0, 0, outW, outH, 0, 0, 1, 1, (float)srcW / outW, (float)srcH / outH);
            case ScaleMode.Fill:
            {
                if (Math.Abs(sa - da) < 1e-4) goto case ScaleMode.Stretch;
                if (sa > da)
                {
                    double visW = srcH * da; double u0 = (1 - visW / srcW) / 2;
                    return new(0, 0, outW, outH, (float)u0, 0, (float)(1 - u0), 1, (float)(visW / outW), (float)srcH / outH);
                }
                else
                {
                    double visH = srcW / da; double v0 = (1 - visH / srcH) / 2;
                    return new(0, 0, outW, outH, 0, (float)v0, 1, (float)(1 - v0), (float)srcW / outW, (float)(visH / outH));
                }
            }
            default: // Fit
            {
                if (Math.Abs(sa - da) < 1e-4) goto case ScaleMode.Stretch;
                if (sa > da)
                {
                    double h = Math.Round(outW / sa); double y = Math.Round((outH - h) / 2);
                    return new(0, (float)y, outW, (float)h, 0, 0, 1, 1, (float)srcW / outW, (float)(srcH / h));
                }
                else
                {
                    double w = Math.Round(outH * sa); double x = Math.Round((outW - w) / 2);
                    return new((float)x, 0, (float)w, outH, 0, 0, 1, 1, (float)(srcW / w), (float)srcH / outH);
                }
            }
        }
    }
}

/// <summary>
/// GPU composition: captured source (scaled with the chosen filter, HDR tone-mapped if needed) + overlays into an
/// output-sized BGRA frame, then BT.709 NV12 conversion for zero-copy hardware encoding, plus a small preview readback.
/// All methods must be called from the video thread.
/// </summary>
public sealed class Compositor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector4 DstRect, SrcRect, SrcSize, Misc, Misc2;
    }

    private readonly D3DContext _d3d;
    private readonly ID3D11Device _dev;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psBilinear, _psBicubic, _psLanczos, _psArea, _psOverlay, _psSolid, _psLuma, _psChroma;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11SamplerState _linear, _point, _trilinear;
    private readonly ID3D11BlendState _opaque, _premul;
    private readonly ID3D11RasterizerState _raster;

    public int Width { get; }
    public int Height { get; }

    private readonly ID3D11Texture2D _composite;
    private readonly ID3D11RenderTargetView _compositeRtv;
    private readonly ID3D11ShaderResourceView _compositeSrv;

    public bool Nv12Supported { get; }
    private readonly ID3D11Texture2D? _nv12;
    private readonly ID3D11RenderTargetView? _lumaRtv, _chromaRtv;
    public ID3D11Texture2D? Nv12Texture => _nv12;
    public ID3D11Texture2D CompositeTexture => _composite;

    // Preview
    public int PreviewWidth { get; }
    public int PreviewHeight { get; }
    private readonly ID3D11Texture2D _previewRt;
    private readonly ID3D11RenderTargetView _previewRtv;
    private readonly ID3D11Texture2D[] _previewStaging = new ID3D11Texture2D[3];
    private readonly long[] _previewStagingSerial = new long[3];
    private long _previewSerial;

    private readonly Dictionary<OverlayBitmap, (ID3D11Texture2D tex, ID3D11ShaderResourceView srv, long lastUsed)> _overlayCache = new(ReferenceEqualityComparer.Instance);
    private long _frameCounter;

    public Compositor(D3DContext d3d, int width, int height)
    {
        _d3d = d3d;
        _dev = d3d.Device;
        _ctx = d3d.Context;
        Width = width;
        Height = height;

        var vsCode = Compile("VS", "vs_5_0");
        _vs = _dev.CreateVertexShader(vsCode.Span);
        _psBilinear = _dev.CreatePixelShader(Compile("PSBilinear", "ps_5_0").Span);
        _psBicubic = _dev.CreatePixelShader(Compile("PSBicubic", "ps_5_0").Span);
        _psLanczos = _dev.CreatePixelShader(Compile("PSLanczos", "ps_5_0").Span);
        _psArea = _dev.CreatePixelShader(Compile("PSArea", "ps_5_0").Span);
        _psOverlay = _dev.CreatePixelShader(Compile("PSOverlay", "ps_5_0").Span);
        _psSolid = _dev.CreatePixelShader(Compile("PSSolid", "ps_5_0").Span);
        _psLuma = _dev.CreatePixelShader(Compile("PSLuma", "ps_5_0").Span);
        _psChroma = _dev.CreatePixelShader(Compile("PSChroma", "ps_5_0").Span);

        _cb = _dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _linear = _dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, 0));
        _point = _dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, 0));
        _trilinear = _dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));

        _opaque = _dev.CreateBlendState(BlendDescription.Opaque);
        var premul = new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha);
        _premul = _dev.CreateBlendState(premul);
        _raster = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { ScissorEnable = false, DepthClipEnable = false });

        _composite = _dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _compositeRtv = _dev.CreateRenderTargetView(_composite, null);
        _compositeSrv = _dev.CreateShaderResourceView(_composite, null);

        // NV12 render target (luma + chroma plane views). Supported on all modern GPUs; fall back to BGRA otherwise.
        var fs = _dev.CheckFormatSupport(Format.NV12);
        Nv12Supported = (fs & FormatSupport.RenderTarget) != 0 && (fs & FormatSupport.Texture2D) != 0;
        if (Nv12Supported)
        {
            try
            {
                _nv12 = _dev.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
                _lumaRtv = _dev.CreateRenderTargetView(_nv12, new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.R8_UNorm, 0, 0, 1));
                _chromaRtv = _dev.CreateRenderTargetView(_nv12, new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.R8G8_UNorm, 0, 0, 1));
            }
            catch (Exception ex)
            {
                Log.Warn("NV12 render targets unavailable: " + ex.Message);
                Nv12Supported = false;
                _lumaRtv?.Dispose(); _chromaRtv?.Dispose(); _nv12?.Dispose();
                _nv12 = null; _lumaRtv = null; _chromaRtv = null;
            }
        }

        // Preview fits in 960x540 (keeps readback cheap even at 8K output).
        double s = Math.Min(960.0 / width, 540.0 / height);
        s = Math.Min(s, 1.0);
        PreviewWidth = Math.Max(2, (int)Math.Round(width * s));
        PreviewHeight = Math.Max(2, (int)Math.Round(height * s));
        _previewRt = _dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)PreviewWidth, (uint)PreviewHeight, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _previewRtv = _dev.CreateRenderTargetView(_previewRt, null);
        for (int i = 0; i < _previewStaging.Length; i++)
            _previewStaging[i] = _dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)PreviewWidth, (uint)PreviewHeight, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
    }

    private static ReadOnlyMemory<byte> Compile(string entry, string profile) =>
        Compiler.Compile(Shaders.Source, entry, "framelock.hlsl", profile, ShaderFlags.OptimizationLevel3, EffectFlags.None);

    private void SetParams(in Params p)
    {
        var m = _ctx.Map(_cb, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, m.DataPointer, false);
        _ctx.Unmap(_cb, 0);
    }

    private Vector4 Ndc(float x, float y, float w, float h, int rtW, int rtH) =>
        new(x / rtW * 2f - 1f, 1f - y / rtH * 2f, (x + w) / rtW * 2f - 1f, 1f - (y + h) / rtH * 2f);

    private void BeginPass(ID3D11RenderTargetView rtv, int w, int h)
    {
        _ctx.OMSetRenderTargets(rtv, null);
        _ctx.RSSetViewport(new Viewport(0, 0, w, h, 0, 1));
        _ctx.RSSetState(_raster);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _ctx.IASetInputLayout(null);
        _ctx.VSSetShader(_vs);
        _ctx.VSSetConstantBuffer(0, _cb);
        _ctx.PSSetConstantBuffer(0, _cb);
        _ctx.PSSetSampler(0, _linear);
        _ctx.PSSetSampler(1, _point);
        _ctx.PSSetSampler(2, _trilinear);
    }

    /// <summary>Renders the output frame into the composite texture.</summary>
    public void Compose(ID3D11ShaderResourceView? source, int srcW, int srcH, bool hdr, float hdrScale,
                        SourceLayout layout, ScaleFilter filter, Color4 background, IReadOnlyList<OverlayDraw> overlays)
    {
        _frameCounter++;
        BeginPass(_compositeRtv, Width, Height);
        _ctx.ClearRenderTargetView(_compositeRtv, background);

        if (source != null && srcW > 0 && srcH > 0)
        {
            var mode = filter;
            float ratio = Math.Max(layout.RatioX, layout.RatioY);
            if (mode == ScaleFilter.Auto)
            {
                bool exact = Math.Abs(layout.RatioX - 1f) < 1e-3f && Math.Abs(layout.RatioY - 1f) < 1e-3f;
                mode = exact ? ScaleFilter.Bilinear : ratio > 1.4f ? ScaleFilter.Area : ScaleFilter.Bicubic;
            }
            _ctx.OMSetBlendState(_opaque);
            _ctx.PSSetShader(mode switch
            {
                ScaleFilter.Bilinear => _psBilinear,
                ScaleFilter.Lanczos => _psLanczos,
                ScaleFilter.Area => _psArea,
                _ => _psBicubic,
            });
            _ctx.PSSetShaderResource(0, source);
            SetParams(new Params
            {
                DstRect = Ndc(layout.DstX, layout.DstY, layout.DstW, layout.DstH, Width, Height),
                SrcRect = new Vector4(layout.U0, layout.V0, layout.U1, layout.V1),
                SrcSize = new Vector4(srcW, srcH, 1f / srcW, 1f / srcH),
                Misc = new Vector4(1f, layout.RatioX, layout.RatioY, hdrScale),
                Misc2 = new Vector4(hdr ? 1f : 0f, 1f / Width, 1f / Height, 0),
            });
            _ctx.Draw(4, 0);
        }

        if (overlays.Count > 0)
        {
            _ctx.OMSetBlendState(_premul);
            _ctx.PSSetShader(_psOverlay);
            foreach (var o in overlays)
            {
                if (o.Opacity <= 0.001f || o.W < 1 || o.H < 1) continue;
                var srv = GetOverlaySrv(o.Bitmap);
                if (srv == null) continue;
                _ctx.PSSetShaderResource(0, srv);
                SetParams(new Params
                {
                    DstRect = Ndc(o.X, o.Y, o.W, o.H, Width, Height),
                    SrcRect = new Vector4(0, 0, 1, 1),
                    SrcSize = new Vector4(o.Bitmap.Width, o.Bitmap.Height, 1f / o.Bitmap.Width, 1f / o.Bitmap.Height),
                    Misc = new Vector4(Math.Clamp(o.Opacity, 0, 1), 1, 1, 1),
                    Misc2 = new Vector4(0, 1f / Width, 1f / Height, 0),
                });
                _ctx.Draw(4, 0);
            }
        }
        _ctx.PSSetShaderResource(0, null!);
        if (_frameCounter % 600 == 0) PruneOverlayCache();
    }

    /// <summary>Draws a (pre-built) texture region with premultiplied alpha - used for the webcam overlay.</summary>
    public void DrawTexture(ID3D11ShaderResourceView srv, int texW, int texH, float x, float y, float w, float h, float u0, float v0, float u1, float v1, float opacity)
    {
        BeginPass(_compositeRtv, Width, Height);
        _ctx.OMSetBlendState(_premul);
        _ctx.PSSetShader(_psOverlay);
        _ctx.PSSetShaderResource(0, srv);
        SetParams(new Params
        {
            DstRect = Ndc(x, y, w, h, Width, Height),
            SrcRect = new Vector4(u0, v0, u1, v1),
            SrcSize = new Vector4(texW, texH, 1f / texW, 1f / texH),
            Misc = new Vector4(opacity, 1, 1, 1),
            Misc2 = new Vector4(0, 1f / Width, 1f / Height, 0),
        });
        _ctx.Draw(4, 0);
        _ctx.PSSetShaderResource(0, null!);
    }

    /// <summary>Converts the composite into the NV12 texture (BT.709, limited range).</summary>
    public void ConvertToNv12()
    {
        if (!Nv12Supported || _lumaRtv == null || _chromaRtv == null) return;
        var full = new Params
        {
            DstRect = new Vector4(-1, 1, 1, -1),
            SrcRect = new Vector4(0, 0, 1, 1),
            SrcSize = new Vector4(Width, Height, 1f / Width, 1f / Height),
            Misc = new Vector4(1, 1, 1, 1),
            Misc2 = new Vector4(0, 1f / Width, 1f / Height, 0),
        };
        BeginPass(_lumaRtv, Width, Height);
        _ctx.OMSetBlendState(_opaque);
        _ctx.PSSetShader(_psLuma);
        _ctx.PSSetShaderResource(0, _compositeSrv);
        SetParams(full);
        _ctx.Draw(4, 0);

        BeginPass(_chromaRtv, Width / 2, Height / 2);
        _ctx.PSSetShader(_psChroma);
        _ctx.Draw(4, 0);
        _ctx.PSSetShaderResource(0, null!);
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    /// <summary>
    /// Renders a downscaled preview and returns the pixels of the preview rendered two calls ago (no GPU stall).
    /// </summary>
    public bool RenderPreview(byte[] dest)
    {
        BeginPass(_previewRtv, PreviewWidth, PreviewHeight);
        _ctx.OMSetBlendState(_opaque);
        _ctx.PSSetShader(_psArea);
        _ctx.PSSetShaderResource(0, _compositeSrv);
        float rx = (float)Width / PreviewWidth, ry = (float)Height / PreviewHeight;
        SetParams(new Params
        {
            DstRect = new Vector4(-1, 1, 1, -1),
            SrcRect = new Vector4(0, 0, 1, 1),
            SrcSize = new Vector4(Width, Height, 1f / Width, 1f / Height),
            Misc = new Vector4(1, rx, ry, 1),
            Misc2 = new Vector4(0, 1f / Width, 1f / Height, 0),
        });
        _ctx.Draw(4, 0);
        _ctx.PSSetShaderResource(0, null!);
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);

        int slot = (int)(_previewSerial % _previewStaging.Length);
        _ctx.CopyResource(_previewStaging[slot], _previewRt);
        _previewStagingSerial[slot] = ++_previewSerial;

        // Read the slot written two calls ago - the GPU has finished with it, so Map never stalls.
        int readSlot = (int)(_previewSerial % _previewStaging.Length);
        if (_previewSerial < 3 || _previewStagingSerial[readSlot] == 0) return false;
        return ReadStaging(_previewStaging[readSlot], PreviewWidth, PreviewHeight, dest);
    }

    private unsafe bool ReadStaging(ID3D11Texture2D staging, int w, int h, byte[] dest)
    {
        MappedSubresource m;
        try { m = _ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None); }
        catch { return false; }
        try
        {
            int rowBytes = w * 4;
            fixed (byte* d = dest)
            {
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)m.DataPointer + (long)y * m.RowPitch, d + (long)y * rowBytes, rowBytes, rowBytes);
            }
            return true;
        }
        finally { _ctx.Unmap(staging, 0); }
    }

    /// <summary>Synchronous full-resolution readback of the current composite (for screenshots).</summary>
    public byte[] ReadCompositeFull()
    {
        using var staging = _dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        _ctx.CopyResource(staging, _composite);
        var buf = new byte[Width * Height * 4];
        ReadStaging(staging, Width, Height, buf);
        return buf;
    }

    private ID3D11ShaderResourceView? GetOverlaySrv(OverlayBitmap bmp)
    {
        if (_overlayCache.TryGetValue(bmp, out var e))
        {
            _overlayCache[bmp] = (e.tex, e.srv, _frameCounter);
            return e.srv;
        }
        try
        {
            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)bmp.Width, (uint)bmp.Height, 1, 0,
                BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips);
            var tex = _dev.CreateTexture2D(desc);
            unsafe
            {
                fixed (byte* p = bmp.Pixels)
                    _ctx.UpdateSubresource(tex, 0, null, (IntPtr)p, (uint)(bmp.Width * 4), 0);
            }
            var srv = _dev.CreateShaderResourceView(tex, null);
            _ctx.GenerateMips(srv);
            _overlayCache[bmp] = (tex, srv, _frameCounter);
            return srv;
        }
        catch (Exception ex)
        {
            Log.Error("Overlay texture upload failed", ex);
            return null;
        }
    }

    private void PruneOverlayCache()
    {
        foreach (var key in _overlayCache.Where(kv => _frameCounter - kv.Value.lastUsed > 600).Select(kv => kv.Key).ToList())
        {
            var e = _overlayCache[key];
            e.srv.Dispose();
            e.tex.Dispose();
            _overlayCache.Remove(key);
        }
    }

    public void Dispose()
    {
        foreach (var e in _overlayCache.Values) { e.srv.Dispose(); e.tex.Dispose(); }
        _overlayCache.Clear();
        foreach (var s in _previewStaging) s?.Dispose();
        _previewRtv.Dispose(); _previewRt.Dispose();
        _lumaRtv?.Dispose(); _chromaRtv?.Dispose(); _nv12?.Dispose();
        _compositeSrv.Dispose(); _compositeRtv.Dispose(); _composite.Dispose();
        _raster.Dispose(); _premul.Dispose(); _opaque.Dispose();
        _trilinear.Dispose(); _point.Dispose(); _linear.Dispose(); _cb.Dispose();
        _psChroma.Dispose(); _psLuma.Dispose(); _psSolid.Dispose(); _psOverlay.Dispose(); _psArea.Dispose();
        _psLanczos.Dispose(); _psBicubic.Dispose(); _psBilinear.Dispose(); _vs.Dispose();
    }
}
