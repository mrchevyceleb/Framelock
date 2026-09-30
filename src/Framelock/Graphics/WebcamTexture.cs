using Framelock.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Framelock.Graphics;

/// <summary>A reusable dynamic texture; only the pipeline's video thread touches these GPU resources.</summary>
public sealed class WebcamTexture(D3DContext d3d) : IDisposable
{
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;
    private WebcamCapture? _owner;
    public ID3D11ShaderResourceView? Srv => _srv;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public unsafe bool Update(WebcamCapture? camera)
    {
        if (_owner != camera)
        {
            Dispose();
            _owner = camera;
        }
        if (camera == null || !camera.IsReady || camera.IsStalled) { Dispose(); return false; }
        var frame = camera.TakeFrame();
        if (frame == null) return false;
        if (_texture == null || Width != frame.Width || Height != frame.Height)
        {
            Dispose();
            Width = frame.Width;
            Height = frame.Height;
            _texture = d3d.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write, 1, 0, ResourceOptionFlags.None));
            _srv = d3d.Device.CreateShaderResourceView(_texture);
        }
        var mapped = d3d.Context.Map(_texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = Width * 4;
            fixed (byte* src = frame.Pixels)
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy(src + y * rowBytes, (byte*)mapped.DataPointer + y * mapped.RowPitch, mapped.RowPitch, rowBytes);
        }
        finally { d3d.Context.Unmap(_texture, 0); }
        return true;
    }

    public void Dispose()
    {
        _srv?.Dispose(); _srv = null;
        _texture?.Dispose(); _texture = null;
        Width = Height = 0;
    }
}
