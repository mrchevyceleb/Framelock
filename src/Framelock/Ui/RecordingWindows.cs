using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Framelock.Core;
using Framelock.Engine;

namespace Framelock.Ui;

internal static class Placement
{
    /// <summary>Positions a window in physical pixels (reliable across monitors with different scaling).</summary>
    public static void SetPhysical(Window w, Native.RECT r, bool topmost = true)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        Native.SetWindowPos(h, topmost ? Native.HWND_TOPMOST : IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
            Native.SWP_NOACTIVATE | (topmost ? 0 : Native.SWP_NOZORDER));
    }

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    public static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");
}

/// <summary>Floating pill shown while recording: timer, size, pause, marker, stop. Never appears in the video.</summary>
public sealed class HudWindow : Window
{
    private readonly RecorderController _rec;
    private static Point? _lastPos;

    public HudWindow(RecorderController rec, Window owner)
    {
        _rec = rec;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Framelock HUD";
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var dot = new Ellipse { Width = 10, Height = 10, Fill = Placement.Res("RecBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        var time = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Placement.Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };
        time.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecorderController.Elapsed)) { Source = rec, Converter = new DurationConverter() });
        var size = new TextBlock { FontSize = 11.5, Foreground = Placement.Res("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 6, 0), MinWidth = 48 };
        size.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecorderController.FileSize)) { Source = rec });

        var pauseGlyph = new TextBlock { FontFamily = Placement.IconFont, FontSize = 13 };
        pauseGlyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecorderController.IsPaused)) { Source = rec, Converter = new PauseGlyphConverter() });
        var pause = MakeButton(pauseGlyph, "Pause / resume", () => rec.TogglePause());
        var marker = MakeButton(new TextBlock { Text = "", FontFamily = Placement.IconFont, FontSize = 13 }, "Add marker", rec.AddMarker);
        var stopIcon = new Rectangle { Width = 11, Height = 11, RadiusX = 2, RadiusY = 2, Fill = Placement.Res("RecBrush") };
        var stop = MakeButton(stopIcon, "Stop recording", () => _ = rec.StopRecordingAsync());

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(time);
        row.Children.Add(size);
        row.Children.Add(pause);
        row.Children.Add(marker);
        row.Children.Add(stop);

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x16, 0x18, 0x1D)),
            BorderBrush = Placement.Res("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(12, 5, 6, 5),
            Margin = new Thickness(10),
            Child = row,
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move",
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 },
        };
        pill.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } _lastPos = new Point(Left, Top); } };
        Content = pill;

        rec.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecorderController.State))
                dot.Fill = Placement.Res(rec.IsPaused ? "WarnBrush" : "RecBrush");
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(h, true);
            Native.ExcludeFromCapture(h, true);
        };
        Loaded += (_, _) =>
        {
            if (_lastPos is Point p) { Left = p.X; Top = p.Y; return; }
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 8;
        };
    }

    private static Button MakeButton(object content, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
            Content = content, ToolTip = tip, Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private sealed class DurationConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is TimeSpan ts ? RecorderController.FormatDuration(ts) : "";
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    private sealed class PauseGlyphConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is true ? "" : "";
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}

/// <summary>Click-through outline around the recorded area (follows a moving window). Excluded from capture.</summary>
public sealed class BorderWindow : Window
{
    private const int Pad = 3; // physical px outside the captured area
    private readonly Func<Native.RECT?> _rect;
    private readonly DispatcherTimer _timer;
    private readonly Border _frame;
    private Native.RECT _last;

    public BorderWindow(Func<Native.RECT?> rect)
    {
        _rect = rect;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Framelock region";
        _frame = new Border { BorderBrush = Placement.Res("RecBrush"), BorderThickness = new Thickness(2), IsHitTestVisible = false };
        Content = _frame;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.MakeClickThrough(h);
            Native.ExcludeFromCapture(h, true);
        };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { _last = default; Refresh(); _timer.Start(); } else _timer.Stop(); };
        Closed += (_, _) => _timer.Stop();
    }

    public void Refresh()
    {
        if (_rect() is not { } r || r.Width <= 0) { Opacity = 0; return; }
        Opacity = 1;
        var outer = new Native.RECT(r.Left - Pad, r.Top - Pad, r.Right + Pad, r.Bottom + Pad);
        if (outer.Equals(_last)) return;
        _last = outer;
        Placement.SetPhysical(this, outer);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _frame.BorderThickness = new Thickness(2.0 / scale); // 2 physical pixels at any scaling
    }
}

/// <summary>3-2-1 before recording starts, centred on what's about to be recorded. Excluded from capture.</summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _number;
    private int _left;
    private readonly DispatcherTimer _timer;

    public static void ShowOn(Native.RECT? area, int seconds)
    {
        if (seconds <= 0) return;
        var w = new CountdownWindow(seconds);
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            Native.MakeClickThrough(h);
            Native.ExcludeFromCapture(h, true);
            if (area is { } a)
            {
                double scale = w.DpiScaleForPoint(a.Left + a.Width / 2, a.Top + a.Height / 2);
                int size = (int)(220 * scale);
                int cx = a.Left + a.Width / 2, cy = a.Top + a.Height / 2;
                Placement.SetPhysical(w, new Native.RECT(cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2));
            }
        };
        w.Show();
    }

    private double DpiScaleForPoint(int x, int y)
    {
        var mon = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 ? dx / 96.0 : 1.0;
    }

    private CountdownWindow(int seconds)
    {
        _left = seconds;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Framelock countdown";

        _number = new TextBlock
        {
            Text = seconds.ToString(), FontSize = 96, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        };
        var ring = new Ellipse { Stroke = Placement.Res("RecBrush"), StrokeThickness = 6, Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x11, 0x14)) };
        var grid = new Grid { Margin = new Thickness(10) };
        grid.Children.Add(ring);
        grid.Children.Add(_number);
        Content = grid;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _left--;
            if (_left <= 0) { _timer.Stop(); Close(); return; }
            _number.Text = _left.ToString();
            Pop();
        };
        Loaded += (_, _) => { Pop(); _timer.Start(); };
    }

    private void Pop()
    {
        var st = new ScaleTransform(1.25, 1.25);
        _number.RenderTransformOrigin = new Point(0.5, 0.5);
        _number.RenderTransform = st;
        var a = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
