using FFmpeg.AutoGen;
using Framelock.Core;
using Framelock.Graphics;
using Vortice.DXGI;

namespace Framelock.Encoding;

public enum VideoCodec { H264, Hevc, Av1 }
public enum EncoderFamily { Nvenc, Amf, Qsv, Software }

public sealed record EncoderInfo(string Id, string Label, VideoCodec Codec, EncoderFamily Family)
{
    public bool IsHardware => Family != EncoderFamily.Software;
    /// <summary>Hardware encoders that take GPU textures directly (zero-copy). QSV goes through system memory here.</summary>
    public bool UsesD3D11Frames => Family is EncoderFamily.Nvenc or EncoderFamily.Amf;
    public GpuVendor? Vendor => Family switch
    {
        EncoderFamily.Nvenc => GpuVendor.Nvidia,
        EncoderFamily.Amf => GpuVendor.Amd,
        EncoderFamily.Qsv => GpuVendor.Intel,
        _ => null,
    };
    public int MaxDimension => Codec == VideoCodec.H264 ? 4096 : 8192;
    public string CodecLabel => Codec switch { VideoCodec.H264 => "H.264", VideoCodec.Hevc => "HEVC", _ => "AV1" };
    public override string ToString() => Label;
}

public static class EncoderCatalog
{
    public static readonly EncoderInfo[] All =
    {
        new("hevc_nvenc", "NVIDIA NVENC · HEVC (H.265)", VideoCodec.Hevc, EncoderFamily.Nvenc),
        new("av1_nvenc", "NVIDIA NVENC · AV1", VideoCodec.Av1, EncoderFamily.Nvenc),
        new("h264_nvenc", "NVIDIA NVENC · H.264", VideoCodec.H264, EncoderFamily.Nvenc),
        new("hevc_amf", "AMD AMF · HEVC (H.265)", VideoCodec.Hevc, EncoderFamily.Amf),
        new("av1_amf", "AMD AMF · AV1", VideoCodec.Av1, EncoderFamily.Amf),
        new("h264_amf", "AMD AMF · H.264", VideoCodec.H264, EncoderFamily.Amf),
        new("hevc_qsv", "Intel Quick Sync · HEVC", VideoCodec.Hevc, EncoderFamily.Qsv),
        new("av1_qsv", "Intel Quick Sync · AV1", VideoCodec.Av1, EncoderFamily.Qsv),
        new("h264_qsv", "Intel Quick Sync · H.264", VideoCodec.H264, EncoderFamily.Qsv),
        new("libx264", "Software x264 · H.264 (CPU)", VideoCodec.H264, EncoderFamily.Software),
        new("libx265", "Software x265 · HEVC (CPU)", VideoCodec.Hevc, EncoderFamily.Software),
        new("libsvtav1", "Software SVT-AV1 (CPU)", VideoCodec.Av1, EncoderFamily.Software),
    };

    public static readonly EncoderInfo Auto = new("auto", "Automatic (best available)", VideoCodec.Hevc, EncoderFamily.Software);

    private static readonly Dictionary<string, long> _adapterFor = new();
    public static IReadOnlyList<EncoderInfo> Available { get; private set; } = Array.Empty<EncoderInfo>();
    public static bool Probed { get; private set; }
    public static event Action? ProbeCompleted;

    public static EncoderInfo? Find(string id) => All.FirstOrDefault(e => e.Id == id);

    /// <summary>LUID of the adapter a hardware encoder was verified on (0 = any / software).</summary>
    public static long AdapterFor(EncoderInfo e) => _adapterFor.TryGetValue(e.Id, out var l) ? l : 0;

    /// <summary>Picks the concrete encoder for a setting value ("auto" or an id), falling back gracefully.</summary>
    public static EncoderInfo Resolve(string id, int width, int height)
    {
        var list = Available.Count > 0 ? Available : All.Where(e => e.Family == EncoderFamily.Software).ToList();
        var chosen = id == "auto" ? null : list.FirstOrDefault(e => e.Id == id);
        if (chosen != null && Math.Max(width, height) <= chosen.MaxDimension) return chosen;
        // Auto: HEVC hardware first (H.264 can't do 4K120 within level limits or >4096 px), then AV1, then H.264, then CPU.
        var order = new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv", "av1_nvenc", "av1_amf", "av1_qsv", "h264_nvenc", "h264_amf", "h264_qsv", "libx264", "libx265", "libsvtav1" };
        foreach (var o in order)
        {
            var e = list.FirstOrDefault(x => x.Id == o);
            if (e != null && Math.Max(width, height) <= e.MaxDimension) return e;
        }
        return All.First(e => e.Id == "libx264");
    }

    /// <summary>Opens a tiny test session for every encoder on every GPU to find what really works on this machine.</summary>
    public static Task ProbeAsync() => Task.Run(() =>
    {
        var ok = new List<EncoderInfo>();
        try
        {
            var adapters = EnumerateHardwareAdapters();
            foreach (var adapter in adapters)
            {
                D3DContext? d3d = null;
                try
                {
                    d3d = D3DContext.Create(adapter);
                    foreach (var e in All.Where(e => e.IsHardware && e.Vendor == d3d.Vendor))
                    {
                        if (ok.Any(x => x.Id == e.Id)) continue;
                        if (TryOpen(e, d3d))
                        {
                            ok.Add(e);
                            _adapterFor[e.Id] = d3d.AdapterLuid;
                        }
                    }
                }
                catch (Exception ex) { Log.Warn($"Encoder probe on adapter failed: {ex.Message}"); adapter.Dispose(); }
                finally { d3d?.Dispose(); }
            }
            unsafe
            {
                foreach (var e in All.Where(e => !e.IsHardware))
                    if (ffmpeg.avcodec_find_encoder_by_name(e.Id) != null) ok.Add(e);
            }
        }
        catch (Exception ex) { Log.Error("Encoder probe failed", ex); }

        Available = All.Where(ok.Contains).ToList();
        Probed = true;
        Log.Info("Encoders available: " + string.Join(", ", Available.Select(e => e.Id)));
        ProbeCompleted?.Invoke();
    });

    private static bool TryOpen(EncoderInfo e, D3DContext d3d)
    {
        unsafe
        {
            if (ffmpeg.avcodec_find_encoder_by_name(e.Id) == null) return false;
        }
        try
        {
            var settings = new VideoEncoderSettings(RateControlMode.ConstantQuality, 23, 10, SpeedPreset.Performance, 2, true);
            using var enc = new VideoEncoder(e, d3d, 640, 360, 30, settings, nv12Input: true, _ => { });
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Encoder {e.Id} unavailable on {d3d.AdapterName}: {ex.Message}");
            return false;
        }
    }

    private static List<IDXGIAdapter1> EnumerateHardwareAdapters()
    {
        var list = new List<IDXGIAdapter1>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0) { adapter.Dispose(); continue; }
            list.Add(adapter);
        }
        return list;
    }
}
