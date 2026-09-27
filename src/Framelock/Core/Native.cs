using System.Runtime.InteropServices;
using System.Text;

namespace Framelock.Core;

public static partial class Native
{
    // ---------- structs ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public override string ToString() => $"{Left},{Top} {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ---------- user32 ----------
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_DISABLED = 0x08000000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    public const uint GA_ROOTOWNER = 3;
    public const uint WDA_NONE = 0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_FRAMECHANGED = 0x20;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_GETICON = 0x007F;
    public const int GCLP_HICON = -14, GCLP_HICONSM = -34;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    // ---------- dwmapi ----------
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // ---------- shcore ----------
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------- kernel32 ----------
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    // ---------- avrt / winmm ----------
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] public static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint taskIndex);
    [DllImport("avrt.dll")] public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    // ---------- d3d11 / gdi32 ----------
    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("gdi32.dll")] public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priorityClass);
    public const int D3DKMT_SCHEDULINGPRIORITYCLASS_ABOVE_NORMAL = 3;
    public const int D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH = 4;

    // ---------- display config (SDR white level) ----------
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SDR_WHITE_LEVEL { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint SDRWhiteLevel; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL info);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO info);
    public const uint QDC_ONLY_ACTIVE_PATHS = 2;

    // ---------- helpers ----------
    public static string GetWindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetWindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The visible window bounds (excluding the invisible resize border / shadow) in physical pixels.</summary>
    public static RECT GetVisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static RECT GetClientScreenRect(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var c);
        var p = new POINT(0, 0);
        ClientToScreen(hwnd, ref p);
        return new RECT(p.X, p.Y, p.X + c.Width, p.Y + c.Height);
    }

    public static void ExcludeFromCapture(IntPtr hwnd, bool exclude) =>
        SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    public static void MakeClickThrough(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void MakeToolWindow(IntPtr hwnd, bool noActivate)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }
}
