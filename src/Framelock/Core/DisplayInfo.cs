using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Framelock.Core;

public sealed class DisplayInfo
{
    public required IntPtr Handle { get; init; }
    public required string DeviceName { get; init; }
    public required Native.RECT Bounds { get; init; }
    public required Native.RECT WorkArea { get; init; }
    public required bool IsPrimary { get; init; }
    public required int RefreshRate { get; init; }
    public required double Scale { get; init; }
    public int Index { get; init; }
    public bool IsHdr { get; init; }
    public double SdrWhiteNits { get; init; } = 80;
    public string AdapterName { get; init; } = "";

    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    public string Label => $"Display {Index} — {Width}×{Height} @ {RefreshRate} Hz{(IsPrimary ? " · Primary" : "")}{(IsHdr ? " · HDR" : "")}";
    public override string ToString() => Label;

    public static List<DisplayInfo> GetAll()
    {
        var hdr = GetHdrInfo();
        var dxgi = GetDxgiOutputs();
        var list = new List<(IntPtr h, Native.MONITORINFOEX info)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi)) list.Add((h, mi));
            return true;
        }, IntPtr.Zero);

        // Order: primary first, then left-to-right.
        var ordered = list.OrderByDescending(m => (m.info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0)
                          .ThenBy(m => m.info.rcMonitor.Left).ThenBy(m => m.info.rcMonitor.Top).ToList();
        var result = new List<DisplayInfo>();
        int idx = 1;
        foreach (var (h, mi) in ordered)
        {
            var dm = new Native.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
            int hz = Native.EnumDisplaySettings(mi.szDevice, -1, ref dm) ? (int)dm.dmDisplayFrequency : 60;
            Native.GetDpiForMonitor(h, 0, out uint dpiX, out _);
            hdr.TryGetValue(mi.szDevice, out var hi);
            dxgi.TryGetValue(h, out var d);
            result.Add(new DisplayInfo
            {
                Handle = h,
                DeviceName = mi.szDevice,
                Bounds = mi.rcMonitor,
                WorkArea = mi.rcWork,
                IsPrimary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                RefreshRate = hz <= 1 ? 60 : hz,
                Scale = dpiX == 0 ? 1.0 : dpiX / 96.0,
                Index = idx++,
                IsHdr = hi.hdr || d.hdr,
                SdrWhiteNits = hi.white > 0 ? hi.white : 80,
                AdapterName = d.adapter ?? "",
            });
        }
        return result;
    }

    public static DisplayInfo? Find(List<DisplayInfo> all, string? deviceName) =>
        all.FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(d => d.IsPrimary) ?? all.FirstOrDefault();

    public static DisplayInfo? FromHandle(List<DisplayInfo> all, IntPtr hmon) => all.FirstOrDefault(d => d.Handle == hmon);

    private static Dictionary<IntPtr, (bool hdr, string? adapter)> GetDxgiOutputs()
    {
        var map = new Dictionary<IntPtr, (bool, string?)>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    var name = adapter.Description1.Description;
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            bool hdr = false;
                            try
                            {
                                using var o6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                                if (o6 != null) hdr = o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            }
                            catch { }
                            map[output.Description.Monitor] = (hdr, name);
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn("DXGI output enumeration failed: " + ex.Message); }
        return map;
    }

    /// <summary>Reads advanced-color state and the "SDR content brightness" white level per GDI device name.</summary>
    private static Dictionary<string, (bool hdr, double white)> GetHdrInfo()
    {
        var result = new Dictionary<string, (bool, double)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[np];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[nm];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = 1; // GET_SOURCE_NAME
                src.header.size = Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                src.header.adapterId = p.sourceInfo.adapterId;
                src.header.id = p.sourceInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var color = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                color.header.type = 9; // GET_ADVANCED_COLOR_INFO
                color.header.size = Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                color.header.adapterId = p.targetInfo.adapterId;
                color.header.id = p.targetInfo.id;
                bool hdr = Native.DisplayConfigGetDeviceInfo(ref color) == 0 && (color.value & 0x3) == 0x3; // supported + enabled

                var white = new Native.DISPLAYCONFIG_SDR_WHITE_LEVEL();
                white.header.type = 11; // GET_SDR_WHITE_LEVEL
                white.header.size = Marshal.SizeOf<Native.DISPLAYCONFIG_SDR_WHITE_LEVEL>();
                white.header.adapterId = p.targetInfo.adapterId;
                white.header.id = p.targetInfo.id;
                double nits = Native.DisplayConfigGetDeviceInfo(ref white) == 0 ? white.SDRWhiteLevel / 1000.0 * 80.0 : 80;
                result[src.viewGdiDeviceName] = (hdr, nits);
            }
        }
        catch (Exception ex) { Log.Warn("HDR query failed: " + ex.Message); }
        return result;
    }
}
