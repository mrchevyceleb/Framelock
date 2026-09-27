using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Framelock.Capture;

/// <summary>Raw COM interop for Windows.Graphics.Capture (HWND/HMONITOR items, surface → ID3D11Texture2D).</summary>
internal static unsafe class WgcInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    private static IntPtr GetInteropFactory()
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hs));
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out var f));
            return f;
        }
        finally { WindowsDeleteString(hs); }
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd) => Create(hwnd, 3);
    public static GraphicsCaptureItem CreateForMonitor(IntPtr hmon) => Create(hmon, 4);

    private static GraphicsCaptureItem Create(IntPtr handle, int slot)
    {
        var factory = GetInteropFactory();
        try
        {
            var iid = IID_IGraphicsCaptureItem;
            IntPtr item;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(IntPtr**)factory + slot));
            int hr = fn(factory, handle, &iid, &item);
            Marshal.ThrowExceptionForHR(hr);
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(factory); }
    }

    /// <summary>Returns the frame's backing texture (caller disposes the wrapper - releases one ref).</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iid = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in iid, out var access));
            try
            {
                var texIid = IID_ID3D11Texture2D;
                IntPtr tex;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(IntPtr**)access + 3));
                Marshal.ThrowExceptionForHR(fn(access, &texIid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(abi); }
    }
}
