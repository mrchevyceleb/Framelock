using System.Runtime.InteropServices;
using Framelock.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace Framelock.Graphics;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft }

/// <summary>A D3D11 device on a specific adapter plus its WinRT wrapper for Windows.Graphics.Capture.</summary>
public sealed class D3DContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIAdapter1 Adapter { get; }
    public string AdapterName { get; }
    public long AdapterLuid { get; }
    public GpuVendor Vendor { get; }
    public Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice WinRtDevice { get; }

    private D3DContext(IDXGIAdapter1 adapter, ID3D11Device device, ID3D11DeviceContext context)
    {
        Adapter = adapter;
        Device = device;
        Context = context;
        var desc = adapter.Description1;
        AdapterName = desc.Description;
        AdapterLuid = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;
        Vendor = desc.VendorId switch { 0x10DE => GpuVendor.Nvidia, 0x1002 or 0x1022 => GpuVendor.Amd, 0x8086 => GpuVendor.Intel, 0x1414 => GpuVendor.Microsoft, _ => GpuVendor.Unknown };

        // Encoders (AMF/QSV) may touch the immediate context from their own threads.
        using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        try { WinRtDevice = MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    /// <summary>Create a device on the GPU that drives the given monitor (fast path for capture), or the best GPU as a fallback.</summary>
    public static D3DContext CreateForMonitor(IntPtr hmonitor)
    {
        var adapter = FindAdapterForMonitor(hmonitor) ?? FindBestAdapter();
        return Create(adapter);
    }

    public static D3DContext Create(IDXGIAdapter1 adapter)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels, out ID3D11Device? device, out ID3D11DeviceContext? ctx);
        if (hr.Failure || device == null)
        {
            Log.Warn($"D3D11CreateDevice with video support failed ({hr}); retrying without");
            hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out device, out ctx);
        }
        hr.CheckError();
        var d = new D3DContext(adapter, device!, ctx!);
        TryRaiseGpuPriority(d);
        Log.Info($"D3D11 device on '{d.AdapterName}' ({d.Vendor}), feature level {device!.FeatureLevel}");
        return d;
    }

    private static void TryRaiseGpuPriority(D3DContext d)
    {
        // Keeps capture/encode responsive when a game saturates the GPU. Needs admin for the process class; harmless otherwise.
        try { Native.D3DKMTSetProcessSchedulingPriorityClass(Native.GetCurrentProcess(), Native.D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH); } catch { }
        try
        {
            using var dxgi = d.Device.QueryInterface<IDXGIDevice>();
            dxgi.SetGPUThreadPriority(7);
        }
        catch { }
    }

    public static IDXGIAdapter1? FindAdapterForMonitor(IntPtr hmonitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                using (output)
                    if (output.Description.Monitor == hmonitor) return adapter;
            }
            adapter.Dispose();
        }
        return null;
    }

    public static IDXGIAdapter1? FindAdapterByLuid(long luid)
    {
        if (luid == 0) return null;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var l = adapter.Description1.Luid;
            if ((((long)l.HighPart << 32) | l.LowPart) == luid) return adapter;
            adapter.Dispose();
        }
        return null;
    }

    public static IDXGIAdapter1 FindBestAdapter()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? best = null;
        ulong bestMem = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var desc = adapter.Description1;
            if ((desc.Flags & AdapterFlags.Software) != 0) { adapter.Dispose(); continue; }
            ulong mem = (ulong)desc.DedicatedVideoMemory;
            if (best == null || mem > bestMem) { best?.Dispose(); best = adapter; bestMem = mem; }
            else adapter.Dispose();
        }
        return best ?? throw new InvalidOperationException("No Direct3D 11 GPU found.");
    }

    /// <summary>All hardware adapters that drive at least one display.</summary>
    public static List<IDXGIAdapter1> EnumerateDisplayAdapters()
    {
        var list = new List<IDXGIAdapter1>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var desc = adapter.Description1;
            bool hasOutput = adapter.EnumOutputs(0, out var o).Success;
            o?.Dispose();
            if ((desc.Flags & AdapterFlags.Software) == 0 && hasOutput) list.Add(adapter);
            else adapter.Dispose();
        }
        return list;
    }

    public void Dispose()
    {
        try { (WinRtDevice as IDisposable)?.Dispose(); } catch { }
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}
