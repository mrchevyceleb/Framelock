using System.Windows.Input;
using System.Windows.Interop;

namespace Framelock.Core;

/// <summary>A parsed global hotkey ("Ctrl+Alt+F9").</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        if (IsEmpty) return "None";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        Key.OemPlus => "Plus", Key.OemMinus => "Minus", Key.OemComma => "Comma", Key.OemPeriod => "Period",
        Key.Oem3 => "Tilde", Key.Capital => "CapsLock", Key.Next => "PageDown", Key.Prior => "PageUp",
        _ => k.ToString(),
    };

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "None") return default;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default: key = ParseKey(raw); break;
            }
        }
        return new Hotkey(mods, key);
    }

    public static Key ParseKey(string raw)
    {
        if (raw.Length == 1 && char.IsDigit(raw[0])) return Key.D0 + (raw[0] - '0');
        return raw.ToLowerInvariant() switch
        {
            "plus" => Key.OemPlus, "minus" => Key.OemMinus, "comma" => Key.OemComma, "period" => Key.OemPeriod,
            "tilde" => Key.Oem3, "capslock" => Key.Capital, "pagedown" => Key.Next, "pageup" => Key.Prior,
            _ => Enum.TryParse<Key>(raw, true, out var k) ? k : Key.None,
        };
    }

    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);
}

/// <summary>Registers system-wide hotkeys (work while a fullscreen game has focus).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("FramelockHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false when another app already owns the combination.</summary>
    public bool Register(Hotkey hk, Action action)
    {
        if (hk.IsEmpty) return true;
        uint mods = Native.MOD_NOREPEAT;
        if (hk.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        if (hk.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (hk.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
        if (hk.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Native.MOD_WIN;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, mods, (uint)hk.VirtualKey))
        {
            Log.Warn($"Hotkey {hk} is already in use by another application");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); } catch (Exception ex) { Log.Error("Hotkey action failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
