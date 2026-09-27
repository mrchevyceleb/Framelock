using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Framelock.Core;

namespace Framelock.Ui;

/// <summary>Click, then press a key combination. Esc cancels, Backspace/Delete clears.</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).ShowValue()));

    /// <summary>Set to true for push-to-talk style single keys (modifiers optional, any key allowed).</summary>
    public bool AllowBareKeys { get; set; }

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    /// <summary>Raised while capturing so the app can suspend its global hotkeys (otherwise they'd fire instead).</summary>
    public static event Action<bool>? CapturingChanged;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the keys. Backspace clears.";
        GotKeyboardFocus += (_, _) => { Text = "Press keys…"; CapturingChanged?.Invoke(true); };
        LostKeyboardFocus += (_, _) => { ShowValue(); CapturingChanged?.Invoke(false); };
    }

    private void ShowValue() => Text = string.IsNullOrWhiteSpace(Hotkey) || Hotkey == "None" ? "None" : Hotkey;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Text = new Core.Hotkey(Keyboard.Modifiers, Key.None).ToString().Replace("None", "").TrimEnd('+') + "+…";
            return;
        }
        if (key == Key.Escape) { MoveFocusAway(); return; }
        if (key is Key.Back or Key.Delete) { Hotkey = "None"; MoveFocusAway(); return; }

        var mods = Keyboard.Modifiers;
        bool fKey = key is >= Key.F1 and <= Key.F24;
        if (!AllowBareKeys && mods == ModifierKeys.None && !fKey)
        {
            Text = "Add Ctrl, Alt or Shift…";
            return;
        }
        Hotkey = new Core.Hotkey(mods, key).ToString();
        MoveFocusAway();
    }

    private void MoveFocusAway()
    {
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }
}
