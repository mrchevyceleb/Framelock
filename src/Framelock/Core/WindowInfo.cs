using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Framelock.Core;

public sealed class WindowInfo
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public required string ClassName { get; init; }
    public required uint ProcessId { get; init; }
    public required string ExeName { get; init; }
    public string? ExePath { get; init; }
    public Native.RECT Bounds { get; init; }
    public ImageSource? Icon { get; set; }

    public string Label => string.IsNullOrEmpty(ExeName) ? Title : $"{Title}  ·  {ExeName}";
    public string SizeLabel => $"{Bounds.Width}×{Bounds.Height}";
    public override string ToString() => Label;

    /// <summary>Enumerates top-level windows that Windows Graphics Capture can record (same filter as the system picker).</summary>
    public static List<WindowInfo> GetCapturable(bool withIcons = true)
    {
        var self = (uint)Environment.ProcessId;
        var shell = Native.GetShellWindow();
        var result = new List<WindowInfo>();
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (h == shell || !Native.IsWindowVisible(h)) return true;
                if (Native.GetAncestor(h, Native.GA_ROOTOWNER) != h) return true;
                long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((style & Native.WS_DISABLED) != 0) return true;
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                if (Native.IsCloaked(h)) return true;
                var title = Native.GetWindowTitle(h);
                if (string.IsNullOrWhiteSpace(title)) return true;
                var cls = Native.GetWindowClass(h);
                if (cls is "Progman" or "Button" or "Windows.UI.Core.CoreWindow") return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == self) return true;
                var path = Native.GetProcessPath(pid);
                var b = Native.GetVisibleBounds(h);
                if (b.Width < 32 || b.Height < 32) return true;
                result.Add(new WindowInfo
                {
                    Handle = h, Title = title, ClassName = cls, ProcessId = pid,
                    ExeName = path != null ? Path.GetFileName(path) : "", ExePath = path, Bounds = b,
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        if (withIcons)
            foreach (var w in result) w.Icon = GetIcon(w.Handle);
        return result;
    }

    public static WindowInfo? FromHandle(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return null;
        Native.GetWindowThreadProcessId(h, out uint pid);
        var path = Native.GetProcessPath(pid);
        return new WindowInfo
        {
            Handle = h, Title = Native.GetWindowTitle(h), ClassName = Native.GetWindowClass(h), ProcessId = pid,
            ExeName = path != null ? Path.GetFileName(path) : "", ExePath = path, Bounds = Native.GetVisibleBounds(h),
        };
    }

    /// <summary>Finds the best match for a previously selected window (handle first, then exe + title, then exe/class).</summary>
    public static WindowInfo? Resolve(long handle, string? exe, string? title, string? cls)
    {
        var h = new IntPtr(handle);
        if (h != IntPtr.Zero && Native.IsWindow(h))
        {
            var w = FromHandle(h);
            if (w != null && (exe == null || string.Equals(w.ExeName, exe, StringComparison.OrdinalIgnoreCase))) return w;
        }
        var all = GetCapturable(false);
        return all.FirstOrDefault(w => string.Equals(w.ExeName, exe, StringComparison.OrdinalIgnoreCase) && w.Title == title)
            ?? all.FirstOrDefault(w => string.Equals(w.ExeName, exe, StringComparison.OrdinalIgnoreCase) && w.ClassName == cls)
            ?? all.FirstOrDefault(w => string.Equals(w.ExeName, exe, StringComparison.OrdinalIgnoreCase));
    }

    public static ImageSource? GetIcon(IntPtr hwnd)
    {
        try
        {
            var h = Native.SendMessage(hwnd, Native.WM_GETICON, new IntPtr(2), IntPtr.Zero); // ICON_SMALL2
            if (h == IntPtr.Zero) h = Native.SendMessage(hwnd, Native.WM_GETICON, new IntPtr(0), IntPtr.Zero);
            if (h == IntPtr.Zero) h = Native.SendMessage(hwnd, Native.WM_GETICON, new IntPtr(1), IntPtr.Zero);
            if (h == IntPtr.Zero) h = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
            if (h == IntPtr.Zero) h = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
            if (h == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    public static IEnumerable<(string exe, string title, uint pid)> GetAudioCandidateProcesses()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in GetCapturable(false))
        {
            if (string.IsNullOrEmpty(w.ExeName) || !seen.Add(w.ExeName)) continue;
            yield return (w.ExeName, w.Title, w.ProcessId);
        }
    }

    public static uint? FindProcessId(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            // Prefer a process that owns a visible window (the "main" game/app process).
            var fromWindow = GetCapturable(false).FirstOrDefault(w => string.Equals(w.ExeName, exe, StringComparison.OrdinalIgnoreCase));
            if (fromWindow != null) return fromWindow.ProcessId;
            var p = Process.GetProcessesByName(name).OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
            return p != null ? (uint)p.Id : null;
        }
        catch { return null; }
    }
}
