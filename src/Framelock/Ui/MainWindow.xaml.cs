using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Framelock.Capture;
using Framelock.Core;
using Framelock.Encoding;
using Framelock.Engine;
using Framelock.Graphics;
using Microsoft.Win32;

namespace Framelock.Ui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly RecorderController _rec;
    private readonly AppSettings _s;
    private readonly DispatcherTimer _previewTimer;
    private WriteableBitmap? _previewBmp;
    private long _lastFrameTicks;
    private HotkeyManager? _hotkeys;
    private bool _hotkeysSuspended, _hotkeyWarned, _trayHintShown, _reallyClosing, _pickingRegion;
    private TrayIcon? _tray;
    private HudWindow? _hud;
    private BorderWindow? _border;
    private bool _wasRecording;

    public MainWindow()
    {
        _rec = App.Recorder;
        _s = App.Store.Settings;
        _vm = new MainViewModel(_s, _rec);
        DataContext = _vm;
        InitializeComponent();

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _previewTimer.Tick += (_, _) => { UpdatePreview(); UpdateOverlayAdorner(); };

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => { OnWindowStateChanged(); SyncVisibility(); };
        IsVisibleChanged += (_, _) => SyncVisibility();
        _rec.Notify += n => Toast.Show(n);
        _rec.CountdownRequested += secs => CountdownWindow.ShowOn(CaptureScreenRect(), secs);
        _rec.PipelineChanged += () => { _vm.OnPipelineChanged(); _previewBmp = null; };
        _rec.PropertyChanged += OnRecorderChanged;
        _s.PropertyChanged += OnSettingChanged;
        HotkeyBox.CapturingChanged += capturing => { _hotkeysSuspended = capturing; RegisterHotkeys(); };

        _tray = new TrayIcon(this, _rec, _s);
        var ver = typeof(App).Assembly.GetName().Version;
        AboutText.Text = $"Framelock {ver?.ToString(3)} · FFmpeg {FFmpegSetup.Version}\nSettings and logs: {Paths.AppData}";
        AutostartToggle.IsChecked = Autostart.IsEnabled;
        UpdateStatusDot();
    }

    // ================================================================== window plumbing

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Native.ExcludeFromCapture(Hwnd, _s.HideAppFromCapture);
        Native.UseDarkTitleBar(Hwnd);
        RegisterHotkeys();
        _previewTimer.Start();
        SyncVisibility();
    }

    /// <summary>Creates the window handle (hotkeys, tray) without showing anything.</summary>
    public void StartHidden()
    {
        new WindowInteropHelper(this).EnsureHandle();
        SyncVisibility();
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        SyncVisibility();
    }

    public void HideToTray()
    {
        Hide();
        SyncVisibility();
        if (!_trayHintShown && _tray != null)
        {
            _trayHintShown = true;
            Toast.Show(new Notification("Framelock is in the tray", $"Hotkeys still work. {_s.HotkeyRecord} starts recording."));
        }
    }

    private void SyncVisibility()
    {
        if (_pickingRegion) return;
        _rec.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
    }

    private void OnWindowStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        // WindowChrome windows overhang the screen by the resize border when maximized.
        RootBorder.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Text = max ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing && _s.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _previewTimer.Stop();
        _hud?.Close();
        _border?.Close();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        ((App)Application.Current).Quit();
    }

    /// <summary>Exit for real (tray menu / explicit quit).</summary>
    public void ExitApp()
    {
        _reallyClosing = true;
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && _vm.SelectedOverlay != null && IsOverlayTab)
        {
            _vm.RemoveOverlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ================================================================== hotkeys

    private void RegisterHotkeys()
    {
        if (Hwnd == IntPtr.Zero) return;
        _hotkeys ??= new HotkeyManager();
        _hotkeys.UnregisterAll();
        if (_hotkeysSuspended) return;
        var failed = new List<string>();
        void Reg(string text, Action action, string label)
        {
            var hk = Hotkey.Parse(text);
            if (!hk.IsEmpty && !_hotkeys.Register(hk, action)) failed.Add($"{label} ({hk})");
        }
        Reg(_s.HotkeyRecord, () => _ = _rec.ToggleRecordingAsync(), "Record");
        Reg(_s.HotkeyPause, _rec.TogglePause, "Pause");
        Reg(_s.HotkeyReplay, () => _ = _rec.SaveReplayAsync(), "Save replay");
        Reg(_s.HotkeyScreenshot, () => _ = _rec.TakeScreenshotAsync(), "Screenshot");
        Reg(_s.HotkeyMarker, _rec.AddMarker, "Marker");
        Reg(_s.HotkeyMicMute, _rec.ToggleMicMute, "Mic mute");
        HotkeyStatus.Text = failed.Count == 0
            ? "Hotkeys work everywhere, even inside fullscreen games. Click a box and press the new keys; Backspace clears."
            : "Already used by another app: " + string.Join(", ", failed) + ". Pick a different combination.";
        HotkeyStatus.Foreground = failed.Count == 0 ? (Brush)FindResource("TextMutedBrush") : (Brush)FindResource("WarnBrush");
        if (failed.Count > 0 && !_hotkeyWarned)
        {
            _hotkeyWarned = true;
            Toast.Show(new Notification("Hotkey in use", string.Join(", ", failed) + " is taken by another app. Change it in the Hotkeys tab.", IsError: true));
        }
    }

    // ================================================================== state reactions

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.HotkeyRecord) or nameof(AppSettings.HotkeyPause) or nameof(AppSettings.HotkeyReplay)
                or nameof(AppSettings.HotkeyScreenshot) or nameof(AppSettings.HotkeyMarker) or nameof(AppSettings.HotkeyMicMute):
                _hotkeyWarned = false;
                RegisterHotkeys();
                break;
            case nameof(AppSettings.HideAppFromCapture):
                Native.ExcludeFromCapture(Hwnd, _s.HideAppFromCapture);
                break;
            case nameof(AppSettings.ShowHud) or nameof(AppSettings.ShowRegionBorder):
                SyncRecordingWindows();
                break;
            case nameof(AppSettings.SourceKind) or nameof(AppSettings.RegionX) or nameof(AppSettings.RegionY) or nameof(AppSettings.RegionWidth)
                or nameof(AppSettings.RegionHeight) or nameof(AppSettings.RegionDisplayId) or nameof(AppSettings.WindowHandle):
                Dispatcher.BeginInvoke(DispatcherPriority.Background, SyncRecordingWindows);
                break;
        }
    }

    private void OnRecorderChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RecorderController.State):
                UpdateStatusDot();
                bool rec = _rec.IsRecording;
                if (rec && !_wasRecording && _s.MinimizeWhileRecording && IsVisible) HideToTray();
                _wasRecording = rec;
                SyncRecordingWindows();
                _tray?.Update();
                CommandManager.InvalidateRequerySuggested();
                if (!rec) Title = "Framelock";
                break;
            case nameof(RecorderController.Elapsed):
                if (_rec.IsRecording) Title = $"{(_rec.IsPaused ? "❚❚" : "●")} {RecorderController.FormatDuration(_rec.Elapsed)} · Framelock";
                break;
            case nameof(RecorderController.ReplayActive):
                _tray?.Update();
                CommandManager.InvalidateRequerySuggested();
                break;
        }
    }

    private void UpdateStatusDot()
    {
        StatusDot.Fill = (Brush)FindResource(_rec.State switch
        {
            RecorderState.Recording => "RecBrush",
            RecorderState.Paused => "WarnBrush",
            RecorderState.Starting or RecorderState.Finalizing => "AccentBrush",
            _ => "GoodBrush",
        });
    }

    private void SyncRecordingWindows()
    {
        bool rec = _rec.IsRecording || _rec.State == RecorderState.Starting;
        if (rec && _s.ShowHud)
        {
            if (_hud == null) { _hud = new HudWindow(_rec, this); _hud.Closed += (_, _) => _hud = null; }
            if (!_hud.IsVisible) _hud.Show();
        }
        else _hud?.Hide();

        bool border = rec && _s.ShowRegionBorder && _s.SourceKind is SourceKind.Region or SourceKind.Window;
        if (border)
        {
            _border ??= new BorderWindow(CaptureScreenRect);
            _border.Refresh();
            if (!_border.IsVisible) _border.Show();
        }
        else _border?.Hide();
    }

    /// <summary>What's being captured, in physical screen pixels (null when unknown).</summary>
    private Native.RECT? CaptureScreenRect()
    {
        var t = _rec.CurrentTarget;
        var displays = DisplayInfo.GetAll();
        switch (_s.SourceKind)
        {
            case SourceKind.Webcam: return null;
            case SourceKind.Window when t != null && Native.IsWindow(t.Handle):
                return _s.WindowClientOnly ? Native.GetClientScreenRect(t.Handle) : Native.GetVisibleBounds(t.Handle);
            case SourceKind.Region:
            {
                var d = DisplayInfo.Find(displays, _s.RegionDisplayId) ?? displays.FirstOrDefault();
                if (d == null) return null;
                return new Native.RECT(d.Bounds.Left + _s.RegionX, d.Bounds.Top + _s.RegionY, d.Bounds.Left + _s.RegionX + _s.RegionWidth, d.Bounds.Top + _s.RegionY + _s.RegionHeight);
            }
            default:
            {
                var d = DisplayInfo.Find(displays, _s.DisplayId) ?? displays.FirstOrDefault();
                return d?.Bounds;
            }
        }
    }

    // ================================================================== preview

    private void UpdatePreview()
    {
        var p = _rec.Pipeline;
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (p == null)
        {
            ShowPlaceholder(_rec.StatusText is { Length: > 0 } st && st != "Ready" ? st : "Pick something to record");
            return;
        }
        if (_rec.IsRecording && !_s.PreviewWhileRecording)
        {
            ShowPlaceholder("Recording · live preview is off to save GPU time");
            return;
        }
        int ow = p.Config.Width, oh = p.Config.Height;
        if ((int)PreviewSurface.Width != ow || (int)PreviewSurface.Height != oh)
        {
            PreviewSurface.Width = ow;
            PreviewSurface.Height = oh;
        }
        var px = p.TakePreview();
        if (px == null)
        {
            if (Stopwatch.GetElapsedTime(_lastFrameTicks).TotalSeconds > 2)
                ShowPlaceholder(_rec.CurrentTarget == null ? _rec.StatusText : "Waiting for the picture… (minimized windows can't be captured)");
            return;
        }
        _lastFrameTicks = Stopwatch.GetTimestamp();
        int pw = p.PreviewWidth, ph = p.PreviewHeight;
        if (px.Length < pw * ph * 4) return;
        if (_previewBmp == null || _previewBmp.PixelWidth != pw || _previewBmp.PixelHeight != ph)
        {
            _previewBmp = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgr32, null);
            PreviewImage.Source = _previewBmp;
        }
        _previewBmp.WritePixels(new Int32Rect(0, 0, pw, ph), px, pw * 4, 0);
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
    }

    private void ShowPlaceholder(string text)
    {
        PreviewPlaceholderText.Text = text;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewImage.Visibility = Visibility.Hidden;
    }

    // ================================================================== overlay editor (drag on the preview)

    private enum DragMode { None, Move, Resize }
    private DragMode _drag;
    private OverlayItem? _dragItem;
    private Point _dragStart;
    private Rect _dragRect;

    private int OutW => Math.Max(1, (int)PreviewSurface.Width);
    private int OutH => Math.Max(1, (int)PreviewSurface.Height);
    private bool IsOverlayTab => SettingsTabs.SelectedIndex == 3;

    /// <summary>Screen pixels per output pixel (the Viewbox scale).</summary>
    private double PreviewScale
    {
        get
        {
            double w = PreviewBox.ActualWidth, h = PreviewBox.ActualHeight;
            if (w <= 0 || h <= 0) return 1;
            return Math.Max(0.01, Math.Min(w / OutW, h / OutH));
        }
    }

    private Rect RectOf(OverlayItem o) => OverlayLayout.GetRect(o, OutW, OutH);

    private static (int h, int v) AnchorSides(OverlayAnchor a) => a switch
    {
        // h: 0 left, 1 center, 2 right · v: 0 top, 1 middle, 2 bottom
        OverlayAnchor.TopLeft => (0, 0), OverlayAnchor.TopCenter => (1, 0), OverlayAnchor.TopRight => (2, 0),
        OverlayAnchor.MiddleLeft => (0, 1), OverlayAnchor.Center => (1, 1), OverlayAnchor.MiddleRight => (2, 1),
        OverlayAnchor.BottomLeft => (0, 2), OverlayAnchor.BottomCenter => (1, 2), OverlayAnchor.BottomRight => (2, 2),
        _ => (0, 0),
    };

    private static OverlayAnchor AnchorFrom(int h, int v) => (h, v) switch
    {
        (0, 0) => OverlayAnchor.TopLeft, (1, 0) => OverlayAnchor.TopCenter, (2, 0) => OverlayAnchor.TopRight,
        (0, 1) => OverlayAnchor.MiddleLeft, (1, 1) => OverlayAnchor.Center, (2, 1) => OverlayAnchor.MiddleRight,
        (0, 2) => OverlayAnchor.BottomLeft, (1, 2) => OverlayAnchor.BottomCenter, _ => OverlayAnchor.BottomRight,
    };

    /// <summary>Resize handle sits on the corner opposite the anchor, so the anchored edge stays put.</summary>
    private Point HandlePoint(OverlayItem o, Rect r)
    {
        var (h, v) = AnchorSides(o.Anchor);
        return new Point(h == 2 ? r.Left : r.Right, v == 2 ? r.Top : r.Bottom);
    }

    private OverlayItem? HitTest(Point pt)
    {
        for (int i = _s.Overlays.Count - 1; i >= 0; i--)
        {
            var o = _s.Overlays[i];
            if (o.Visible && (o.Bitmap != null || o.Kind == OverlayKind.Webcam) && RectOf(o).Contains(pt)) return o;
        }
        return null;
    }

    private bool OverHandle(Point pt)
    {
        var o = _vm.SelectedOverlay;
        if (o == null || OverlayHandle.Visibility != Visibility.Visible) return false;
        var hp = HandlePoint(o, RectOf(o));
        double r = 14 / PreviewScale;
        return Math.Abs(pt.X - hp.X) <= r && Math.Abs(pt.Y - hp.Y) <= r;
    }

    private void OverlayCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_rec.Pipeline == null) return;
        var pt = e.GetPosition(OverlayCanvas);
        if (OverHandle(pt))
        {
            _dragItem = _vm.SelectedOverlay;
            _drag = DragMode.Resize;
        }
        else
        {
            var hit = HitTest(pt);
            if (hit == null) return;
            _vm.SelectedOverlay = hit;
            _dragItem = hit;
            _drag = DragMode.Move;
            if (!IsOverlayTab) SettingsTabs.SelectedIndex = 3;
        }
        _dragStart = pt;
        _dragRect = RectOf(_dragItem!);
        OverlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pt = e.GetPosition(OverlayCanvas);
        if (_drag == DragMode.None || _dragItem == null)
        {
            OverlayCanvas.Cursor = OverHandle(pt) ? Cursors.SizeNWSE : HitTest(pt) != null ? Cursors.SizeAll : null;
            return;
        }
        var o = _dragItem;
        int W = OutW, H = OutH;
        if (_drag == DragMode.Move)
        {
            double w = _dragRect.Width, h = _dragRect.Height;
            double x = Math.Clamp(_dragRect.X + pt.X - _dragStart.X, 0, Math.Max(0, W - w));
            double y = Math.Clamp(_dragRect.Y + pt.Y - _dragStart.Y, 0, Math.Max(0, H - h));
            int sx = -1, sy = -1;
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                double m = o.Margin * Math.Min(W, H), thr = 10 / PreviewScale;
                double[] xs = { m, (W - w) / 2, W - w - m }, ys = { m, (H - h) / 2, H - h - m };
                for (int i = 0; i < 3; i++) if (Math.Abs(x - xs[i]) < thr) { sx = i; break; }
                for (int i = 0; i < 3; i++) if (Math.Abs(y - ys[i]) < thr) { sy = i; break; }
                if (sx >= 0) x = xs[sx];
                if (sy >= 0) y = ys[sy];
            }
            if (sx >= 0 && sy >= 0) o.Anchor = AnchorFrom(sx, sy); // snapped to a preset spot: keep it anchored for any resolution
            else
            {
                o.X = x / W;
                o.Y = y / H;
                o.Anchor = OverlayAnchor.Custom;
            }
            ShowGuides(sx >= 0 ? (sx == 0 ? x : sx == 1 ? W / 2.0 : x + w) : null, sy >= 0 ? (sy == 0 ? y : sy == 1 ? H / 2.0 : y + h) : null);
        }
        else
        {
            var (ah, av) = AnchorSides(o.Anchor);
            if (o.Anchor == OverlayAnchor.Custom) (ah, av) = (0, 0);
            double aspect = Math.Max(0.01, o.AspectRatio);
            double wx = ah switch { 2 => _dragRect.Right - pt.X, 1 => 2 * (pt.X - (_dragRect.Left + _dragRect.Width / 2)), _ => pt.X - _dragRect.Left };
            double hy = av switch { 2 => _dragRect.Bottom - pt.Y, 1 => 2 * (pt.Y - (_dragRect.Top + _dragRect.Height / 2)), _ => pt.Y - _dragRect.Top };
            double newW = Math.Max(wx, hy / aspect);
            o.Width = Math.Clamp(newW / W, 0.02, 1.0);
        }
        UpdateOverlayAdorner();
    }

    private void OverlayCanvas_MouseUp(object sender, MouseButtonEventArgs e) => EndDrag();
    private void OverlayCanvas_MouseLeave(object sender, MouseEventArgs e) { if (!OverlayCanvas.IsMouseCaptured) EndDrag(); }

    private void EndDrag()
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        _dragItem = null;
        OverlayCanvas.ReleaseMouseCapture();
        ShowGuides(null, null);
    }

    private void ShowGuides(double? x, double? y)
    {
        double t = 1.5 / PreviewScale;
        SnapGuideV.StrokeThickness = SnapGuideH.StrokeThickness = t;
        if (x is double gx)
        {
            SnapGuideV.X1 = SnapGuideV.X2 = gx;
            SnapGuideV.Y1 = 0;
            SnapGuideV.Y2 = OutH;
            SnapGuideV.Visibility = Visibility.Visible;
        }
        else SnapGuideV.Visibility = Visibility.Collapsed;
        if (y is double gy)
        {
            SnapGuideH.Y1 = SnapGuideH.Y2 = gy;
            SnapGuideH.X1 = 0;
            SnapGuideH.X2 = OutW;
            SnapGuideH.Visibility = Visibility.Visible;
        }
        else SnapGuideH.Visibility = Visibility.Collapsed;
    }

    private void UpdateOverlayAdorner()
    {
        var o = _vm.SelectedOverlay;
        bool show = o != null && o.Visible && (o.Bitmap != null || o.Kind == OverlayKind.Webcam) && _rec.Pipeline != null
                    && PreviewPlaceholder.Visibility != Visibility.Visible
                    && (IsOverlayTab || _drag != DragMode.None || PreviewHost.IsMouseOver);
        OverlayHint.Visibility = IsOverlayTab && _s.Overlays.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            OverlaySelection.Visibility = OverlayHandle.Visibility = Visibility.Collapsed;
            return;
        }
        var r = RectOf(o!);
        double s = PreviewScale, pad = 3 / s;
        OverlaySelection.BorderThickness = new Thickness(1.5 / s);
        Canvas.SetLeft(OverlaySelection, r.X - pad);
        Canvas.SetTop(OverlaySelection, r.Y - pad);
        OverlaySelection.Width = r.Width + 2 * pad;
        OverlaySelection.Height = r.Height + 2 * pad;
        OverlaySelection.Visibility = Visibility.Visible;

        double hs = 10 / s;
        var hp = HandlePoint(o!, r);
        OverlayHandle.Width = OverlayHandle.Height = hs;
        OverlayHandle.RadiusX = OverlayHandle.RadiusY = 2 / s;
        Canvas.SetLeft(OverlayHandle, hp.X - hs / 2);
        Canvas.SetTop(OverlayHandle, hp.Y - hs / 2);
        OverlayHandle.Visibility = Visibility.Visible;
        var (ah, av) = AnchorSides(o!.Anchor);
        OverlayHandle.Cursor = (ah == 2) ^ (av == 2) ? Cursors.SizeNESW : Cursors.SizeNWSE;
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != SettingsTabs) return;
        UpdateOverlayAdorner();
        if (SettingsTabs.SelectedItem == RecordingsTab) _vm.Recordings.Refresh(_s.OutputFolder);
    }

    private void RecordingsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(RecordingsList, d) is ListBoxItem { DataContext: RecordingItem r }
            && FindAncestor<Button>(d) == null)
            MainViewModel.OpenFile(r.Path);
    }

    private void RecordingsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (RecordingsList.SelectedItem is not RecordingItem r) return;
        if (e.Key == Key.Enter) { MainViewModel.OpenFile(r.Path); e.Handled = true; }
        else if (e.Key == Key.Delete) { _vm.RecycleRecordingCommand.Execute(r); e.Handled = true; }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ================================================================== source pickers

    private void Displays_DropDownOpened(object? sender, EventArgs e) => _vm.RefreshDisplays();
    private void Windows_DropDownOpened(object? sender, EventArgs e) => _vm.RefreshWindows();
    private void AudioDevices_DropDownOpened(object? sender, EventArgs e) => _vm.RefreshAudioDevices();
    private async void Webcams_DropDownOpened(object? sender, EventArgs e) => await _vm.RefreshWebcamsAsync();

    private async void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        _pickingRegion = true;
        var restore = WindowState;
        Hide();
        try
        {
            await Task.Delay(180); // let the window vanish before the selector appears
            (int, int)? output = _s.UseSourceResolution ? null : (_s.OutputWidth, _s.OutputHeight);
            var result = await RegionSelectorWindow.PickAsync(output, _s.RegionDisplayId, new Native.RECT(_s.RegionX, _s.RegionY, _s.RegionX + _s.RegionWidth, _s.RegionY + _s.RegionHeight));
            if (result is { } r)
            {
                _s.RegionDisplayId = r.DisplayId;
                _s.RegionX = r.Rect.Left;
                _s.RegionY = r.Rect.Top;
                _s.RegionWidth = r.Rect.Width;
                _s.RegionHeight = r.Rect.Height;
                _s.SourceKind = SourceKind.Region;
                if (r.ExactSize)
                {
                    // An exact-pixel box records 1:1: the video is exactly that size, nothing scaled.
                    _s.UseSourceResolution = false;
                    _s.OutputWidth = r.Rect.Width;
                    _s.OutputHeight = r.Rect.Height;
                }
            }
        }
        finally
        {
            _pickingRegion = false;
            Show();
            WindowState = restore;
            Activate();
            SyncVisibility();
        }
    }

    /// <summary>Called by the tray / hotkey path: pick a region, then show the main window.</summary>
    public void PickRegion() => SelectRegion_Click(this, new RoutedEventArgs());

    // ================================================================== general tab

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Save recordings to", InitialDirectory = Directory.Exists(_s.OutputFolder) ? _s.OutputFolder : null };
        if (dlg.ShowDialog(this) == true) _s.OutputFolder = dlg.FolderName;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenFolder(Paths.Logs);
    private void OpenOverlayFolder_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenFolder(Paths.OverlayLibrary);

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        Autostart.Set(AutostartToggle.IsChecked == true);
        AutostartToggle.IsChecked = Autostart.IsEnabled;
    }
}

public partial class MainWindow
{
    /// <summary>Dev aid (<c>--uishot=folder</c>): renders every settings tab to PNG. The window itself is excluded from screen capture.</summary>
    public async Task SaveUiShotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await Task.Delay(2500);
        for (int i = 0; i < SettingsTabs.Items.Count; i++)
        {
            SettingsTabs.SelectedIndex = i;
            await Task.Delay(SettingsTabs.SelectedItem == RecordingsTab ? 3000 : 400); // thumbnails load in the background
            Snap(Path.Combine(folder, $"tab{i}.png"));
        }

        // The overlay editor only shows with an overlay selected: add a temporary one.
        int before = _s.Overlays.Count;
        _vm.AddTextOverlayCommand.Execute(null);
        SettingsTabs.SelectedIndex = 3;
        await Task.Delay(600);
        Snap(Path.Combine(folder, "overlay-editor.png"));
        if (_s.Overlays.Count > before) _vm.RemoveOverlayCommand.Execute(null);
        ExitApp();
    }

    private void Snap(string file)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var target = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)(target.ActualWidth * dpi.DpiScaleX), (int)(target.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(target);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}

/// <summary>"Start with Windows" via the per-user Run key (starts hidden in the tray).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Framelock";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(Name) is string v && v.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(Name, false);
        }
        catch (Exception ex) { Log.Warn("Autostart change failed: " + ex.Message); }
    }
}
