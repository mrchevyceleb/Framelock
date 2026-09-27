using Framelock.Core;
using Framelock.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Framelock.Capture;

public enum CaptureTargetKind { Monitor, Window }

/// <summary>What to capture. <see cref="Crop"/> is in physical pixels relative to the monitor (monitor targets only).</summary>
public sealed record CaptureTarget(CaptureTargetKind Kind, IntPtr Handle, Native.RECT? Crop, bool ClientOnly, bool Cursor, bool Hdr, double SdrWhiteNits, string Name)
{
    public static CaptureTarget ForDisplay(DisplayInfo d, bool cursor, bool hdr) =>
        new(CaptureTargetKind.Monitor, d.Handle, null, false, cursor, hdr && d.IsHdr, d.SdrWhiteNits, $"Display {d.Index}");

    public static CaptureTarget ForRegion(DisplayInfo d, Native.RECT crop, bool cursor, bool hdr) =>
        new(CaptureTargetKind.Monitor, d.Handle, crop, false, cursor, hdr && d.IsHdr, d.SdrWhiteNits, "Region");
}

/// <summary>
/// Windows.Graphics.Capture source. Frames arrive on a free-threaded pool; the video thread polls the newest one and copies
/// just the wanted box (region / client area) into an owned texture that the compositor samples.
/// </summary>
public sealed class WgcSource : IDisposable
{
    private const int BufferCount = 2;

    private readonly D3DContext _d3d;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly DirectXPixelFormat _format;
    private SizeInt32 _poolSize;

    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;
    private long _lastClientProbe;
    private Native.RECT _clientBox;

    public CaptureTarget Target { get; }
    public bool Hdr { get; }
    /// <summary>scRGB → SDR scale: 80 nits / SDR white level.</summary>
    public float HdrScale { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11ShaderResourceView? Srv => _srv;
    public bool HasFrame => _srv != null;
    public bool IsClosed { get; private set; }
    public long FramesReceived { get; private set; }

    public WgcSource(D3DContext d3d, CaptureTarget target)
    {
        _d3d = d3d;
        Target = target;
        Hdr = target.Hdr;
        HdrScale = (float)(80.0 / Math.Clamp(target.SdrWhiteNits <= 0 ? 80 : target.SdrWhiteNits, 80, 1000));
        _format = Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;

        _item = target.Kind == CaptureTargetKind.Monitor ? WgcInterop.CreateForMonitor(target.Handle) : WgcInterop.CreateForWindow(target.Handle);
        _item.Closed += (_, _) => IsClosed = true;
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3d.WinRtDevice, _format, BufferCount, _poolSize);
        _session = _pool.CreateCaptureSession(_item);

        try { _session.IsCursorCaptureEnabled = target.Cursor; } catch { }
        try { _session.IsBorderRequired = false; } catch { }
        // Without this Windows caps delivery at the display refresh / 60 Hz on some builds.
        try { _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1); } catch { }
        if (target.Kind == CaptureTargetKind.Window)
            try { _session.IncludeSecondaryWindows = true; } catch { }

        _session.StartCapture();
        Log.Info($"Capture started: {target.Name} ({_poolSize.Width}x{_poolSize.Height}, {(Hdr ? "HDR FP16" : "SDR BGRA")})");
    }

    /// <summary>Pulls the newest frame (if any) into <see cref="Srv"/>. Returns true when the image changed.</summary>
    public bool Update()
    {
        Direct3D11CaptureFrame? latest = null;
        try
        {
            while (true)
            {
                var f = _pool.TryGetNextFrame();
                if (f == null) break;
                latest?.Dispose();
                latest = f;
            }
        }
        catch (Exception ex)
        {
            latest?.Dispose();
            Log.Warn("TryGetNextFrame failed: " + ex.Message);
            IsClosed = true;
            return false;
        }
        if (latest == null) return false;

        using (latest)
        {
            var content = latest.ContentSize;
            bool resized = content.Width != _poolSize.Width || content.Height != _poolSize.Height;

            using (var frameTex = WgcInterop.GetTexture(latest.Surface))
            {
                var desc = frameTex.Description;
                int cw = Math.Min(content.Width, (int)desc.Width), ch = Math.Min(content.Height, (int)desc.Height);
                var box = ComputeBox(cw, ch);
                if (box.Width >= 2 && box.Height >= 2)
                {
                    EnsureTexture(box.Width, box.Height, desc.Format);
                    _d3d.Context.CopySubresourceRegion(_tex!, 0, 0, 0, 0, frameTex, 0, new Vortice.Mathematics.Box(box.Left, box.Top, 0, box.Right, box.Bottom, 1));
                    FramesReceived++;
                }
            }

            if (resized && content.Width > 0 && content.Height > 0)
            {
                _poolSize = content;
                _pool.Recreate(_d3d.WinRtDevice, _format, BufferCount, _poolSize);
            }
        }
        return true;
    }

    private Native.RECT ComputeBox(int cw, int ch)
    {
        Native.RECT r;
        if (Target.Kind == CaptureTargetKind.Monitor)
        {
            r = Target.Crop ?? new Native.RECT(0, 0, cw, ch);
        }
        else if (Target.ClientOnly)
        {
            // Client rect relative to the captured visual bounds; re-probed a few times per second (window moves/resizes).
            long now = Environment.TickCount64;
            if (now - _lastClientProbe > 200 || _clientBox.Width == 0)
            {
                _lastClientProbe = now;
                var vis = Native.GetVisibleBounds(Target.Handle);
                var cl = Native.GetClientScreenRect(Target.Handle);
                // Maximized windows hang off-screen by the border width; the capture is clipped to the visible frame.
                int ox = cl.Left - vis.Left, oy = cl.Top - vis.Top;
                _clientBox = cl.Width > 0 && cl.Height > 0 ? new Native.RECT(ox, oy, ox + cl.Width, oy + cl.Height) : new Native.RECT(0, 0, cw, ch);
            }
            r = _clientBox;
        }
        else r = new Native.RECT(0, 0, cw, ch);

        int l = Math.Clamp(r.Left, 0, cw), t = Math.Clamp(r.Top, 0, ch);
        int rr = Math.Clamp(r.Right, l, cw), b = Math.Clamp(r.Bottom, t, ch);
        return new Native.RECT(l, t, rr, b);
    }

    private void EnsureTexture(int w, int h, Format format)
    {
        if (_tex != null && Width == w && Height == h) return;
        _srv?.Dispose();
        _tex?.Dispose();
        _tex = _d3d.Device.CreateTexture2D(new Texture2DDescription(format, (uint)w, (uint)h, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _srv = _d3d.Device.CreateShaderResourceView(_tex, null);
        Width = w;
        Height = h;
    }

    /// <summary>Asks Windows once for permission to hide the yellow capture border (Windows 11).</summary>
    public static async void RequestBorderlessAccess()
    {
        try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); }
        catch (Exception ex) { Log.Debug("Borderless capture access unavailable: " + ex.Message); }
    }

    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        _srv?.Dispose();
        _tex?.Dispose();
    }
}
