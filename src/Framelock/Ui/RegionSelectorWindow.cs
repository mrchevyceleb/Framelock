using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Framelock.Core;

namespace Framelock.Ui;

/// <param name="ExactSize">The user picked an exact pixel size (4K, 1080p…): the video should be that size too, 1:1.</param>
public sealed record RegionResult(string DisplayId, Native.RECT Rect, bool ExactSize);

/// <summary>
/// Full-screen region picker, one window per monitor over a frozen screenshot.
/// Drag to draw, drag inside to move, drag corners to resize, click a window to snap to it,
/// arrows nudge (Shift = 10 px), Enter / double-click confirms, Esc cancels. All maths in physical pixels.
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private sealed record AspectChoice(string Label, double? Ratio, int ExactW = 0, int ExactH = 0);

    private sealed class Session
    {
        public readonly TaskCompletionSource<RegionResult?> Tcs = new();
        public readonly List<RegionSelectorWindow> Windows = new();
        public List<WindowInfo> TopWindows = new();
        public List<AspectChoice> Aspects = new();
        public int AspectIndex;
        public AspectChoice Aspect => Aspects[AspectIndex];

        public void Finish(RegionResult? r)
        {
            if (Tcs.Task.IsCompleted) return;
            Tcs.TrySetResult(r);
            foreach (var w in Windows.ToArray()) if (!w._closed) w.Close();
        }
    }

    /// <summary>Exact capture sizes YouTube takes as-is (the box is locked to these pixels).</summary>
    private static readonly (string Label, int W, int H)[] ExactSizes =
    {
        ("4K", 3840, 2160), ("1440p", 2560, 1440), ("1080p", 1920, 1080), ("720p", 1280, 720), ("Shorts", 1080, 1920),
    };

    /// <param name="output">The video size, or null when it follows the source.</param>
    public static Task<RegionResult?> PickAsync((int W, int H)? output, string? lastDisplayId, Native.RECT lastRect)
    {
        var displays = DisplayInfo.GetAll();
        var s = BuildSession(output, displays);
        foreach (var d in displays)
        {
            Native.RECT? initial = d.DeviceName == lastDisplayId && lastRect.Left >= 0 && lastRect.Top >= 0
                                   && lastRect.Width > 16 && lastRect.Height > 16 && lastRect.Right <= d.Width && lastRect.Bottom <= d.Height ? lastRect : null;
            var w = new RegionSelectorWindow(s, d, initial);
            s.Windows.Add(w);
            w.Show();
        }
        // Keyboard focus goes to the monitor under the cursor.
        Native.GetCursorPos(out var cp);
        var active = s.Windows.FirstOrDefault(w => w.Contains(cp)) ?? s.Windows.FirstOrDefault();
        // An exact size with nothing to restore starts as a centred box, ready to drag into place.
        if (s.Aspect.ExactW > 0 && s.Windows.All(w => w._sel == null))
        {
            var host = active != null && active.Fits(s.Aspect) ? active : s.Windows.FirstOrDefault(w => w.Fits(s.Aspect));
            if (host != null) { host._sel = host.ExactAt(host._display.Width / 2.0, host._display.Height / 2.0, s.Aspect); host.Redraw(); active = host; }
        }
        active?.Activate();
        return s.Tcs.Task;
    }

    private static Session BuildSession((int W, int H)? output, IReadOnlyList<DisplayInfo> displays)
    {
        var s = new Session { TopWindows = WindowInfo.GetCapturable(false) };
        double? outputAspect = output is { } o ? (double)o.W / o.H : null;
        if (outputAspect is double oa && !IsCommon(oa)) s.Aspects.Add(new AspectChoice($"Output {oa:0.##}:1", oa));
        s.Aspects.AddRange(new[]
        {
            new AspectChoice("Free", null), new AspectChoice("16:9", 16 / 9.0), new AspectChoice("9:16", 9 / 16.0), new AspectChoice("4:3", 4 / 3.0),
            new AspectChoice("1:1", 1), new AspectChoice("21:9", 21 / 9.0),
        });
        foreach (var (label, w, h) in ExactSizes) s.Aspects.Add(new AspectChoice($"{label} · {w}×{h}", (double)w / h, w, h));
        if (output is { } c && c.W >= 16 && c.H >= 16 && c.W % 2 == 0 && c.H % 2 == 0 && !s.Aspects.Any(a => a.ExactW == c.W && a.ExactH == c.H))
            s.Aspects.Add(new AspectChoice($"Video · {c.W}×{c.H}", (double)c.W / c.H, c.W, c.H));

        // Default: the video's exact size when a monitor can hold it (1:1, no scaling); otherwise lock to its shape so there are no bars.
        int exact = output is { } e ? s.Aspects.FindIndex(a => a.ExactW == e.W && a.ExactH == e.H && displays.Any(d => a.ExactW <= d.Width && a.ExactH <= d.Height)) : -1;
        if (exact >= 0) s.AspectIndex = exact;
        else if (outputAspect is double r0) s.AspectIndex = Math.Max(0, s.Aspects.FindIndex(a => a.Ratio is double r && Math.Abs(r - r0) < 0.01 && a.ExactW == 0));
        else s.AspectIndex = s.Aspects.FindIndex(a => a.Ratio == null);
        return s;
    }

    /// <summary>Dev aid for <c>--uishot</c>: renders the picker's toolbar to a PNG without showing the full-screen picker.</summary>
    internal static void RenderToolbar(string file, (int W, int H)? output)
    {
        var displays = DisplayInfo.GetAll();
        var w = new RegionSelectorWindow(BuildSession(output, displays), displays[0], null);
        var tb = w._toolbar;
        w._canvas.Children.Remove(tb);
        w.Close();
        // Laid out in a small window far off-screen (layout doesn't run on a never-shown tree).
        var host = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Left = -20000, Top = -20000, SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.Black, Content = tb,
        };
        host.Show();
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(tb.ActualWidth), (int)Math.Ceiling(tb.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tb);
        host.Close();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }

    private static bool IsCommon(double r) => new[] { 16 / 9.0, 9 / 16.0, 4 / 3.0, 1, 21 / 9.0 }.Any(c => Math.Abs(c - r) < 0.01);

    // ------------------------------------------------------------------ instance

    private enum Mode { None, Drawing, Moving, Resizing }

    private readonly Session _session;
    private readonly DisplayInfo _display;
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Path _dim;
    private readonly Rectangle _selRect, _hoverRect;
    private readonly Line _crossH, _crossV;
    private readonly Border _sizeTag, _actions;
    private readonly TextBlock _sizeText;
    private readonly Rectangle[] _handles = new Rectangle[4];
    private readonly Border _toolbar;
    private readonly StackPanel _shapeRow, _exactRow;
    private double _scale = 1;

    private Native.RECT? _sel;           // monitor-relative physical pixels
    private Native.RECT? _hover;         // window under the cursor (monitor-relative)
    private Mode _mode;
    private Point _anchorPx, _startPx;   // physical
    private Native.RECT _startSel;
    private int _corner;                 // 0 TL, 1 TR, 2 BR, 3 BL
    private bool _moved, _closed;

    private RegionSelectorWindow(Session session, DisplayInfo display, Native.RECT? initial)
    {
        _session = session;
        _display = display;
        _sel = initial;
        // An exact size stays exact: a restored selection of another size becomes that size, same centre. On a monitor
        // too small for it the old selection is dropped, so the box starts on a monitor that can hold it.
        var a0 = session.Aspect;
        if (initial is { } r0 && a0.ExactW > 0 && (r0.Width != a0.ExactW || r0.Height != a0.ExactH))
            _sel = Fits(a0) ? ExactAt(r0.Left + r0.Width / 2.0, r0.Top + r0.Height / 2.0, a0) : null;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Select region";
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        var shot = CaptureScreen(display.Bounds);
        var img = new Image { Source = shot, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

        _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
        var accent = Placement.Res("AccentBrush");
        _selRect = new Rectangle { Stroke = accent, StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _hoverRect = new Rectangle { Stroke = Placement.Res("GoodBrush"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var crossBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        _crossH = new Line { Stroke = crossBrush, StrokeThickness = 1, IsHitTestVisible = false };
        _crossV = new Line { Stroke = crossBrush, StrokeThickness = 1, IsHitTestVisible = false };
        _sizeText = new TextBlock { Foreground = Brushes.White, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
        _sizeTag = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x11, 0x14)), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 3, 7, 3), Child = _sizeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Rectangle { Width = 10, Height = 10, Fill = Brushes.White, Stroke = accent, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        }

        var ok = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(12, 5, 12, 5), Content = "Record this area", Cursor = Cursors.Hand };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Padding = new Thickness(10, 5, 10, 5), Content = "Cancel", Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
        cancel.Click += (_, _) => _session.Finish(null);
        var actionsRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionsRow.Children.Add(ok);
        actionsRow.Children.Add(cancel);
        _actions = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x16, 0x18, 0x1D)), CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = actionsRow, Visibility = Visibility.Collapsed, Cursor = Cursors.Arrow };

        _shapeRow = new StackPanel { Orientation = Orientation.Horizontal };
        _exactRow = new StackPanel { Orientation = Orientation.Horizontal };
        BuildAspectButtons();
        var hint = new TextBlock
        {
            Text = "Drag to select · click a window to snap to it · drag the box to move it · Enter to confirm · Esc to cancel",
            Foreground = Placement.Res("TextDimBrush"), FontSize = 12, Margin = new Thickness(4, 8, 4, 0), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var rows = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition());
        rows.RowDefinitions.Add(new RowDefinition());
        AddToolbarRow(rows, 0, "Shape", _shapeRow);
        AddToolbarRow(rows, 1, "Exact pixels", _exactRow);
        var tbStack = new StackPanel();
        tbStack.Children.Add(rows);
        tbStack.Children.Add(hint);
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x16, 0x18, 0x1D)), BorderBrush = Placement.Res("StrokeBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Child = tbStack, Cursor = Cursors.Arrow,
        };

        _canvas.Children.Add(img);
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_hoverRect);
        _canvas.Children.Add(_crossH);
        _canvas.Children.Add(_crossV);
        _canvas.Children.Add(_selRect);
        foreach (var h in _handles) _canvas.Children.Add(h);
        _canvas.Children.Add(_sizeTag);
        _canvas.Children.Add(_actions);
        _canvas.Children.Add(_toolbar);
        Content = _canvas;
        _canvas.Loaded += (_, _) =>
        {
            img.Width = _canvas.ActualWidth;
            img.Height = _canvas.ActualHeight;
        };
        _canvas.SizeChanged += (_, _) => { img.Width = _canvas.ActualWidth; img.Height = _canvas.ActualHeight; Redraw(); };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.ExcludeFromCapture(h, true);
            Placement.SetPhysical(this, display.Bounds);
            Dispatcher.BeginInvoke(() => Placement.SetPhysical(this, display.Bounds)); // again after any DPI change
        };
        Loaded += (_, _) => { _scale = VisualTreeHelper.GetDpi(this).DpiScaleX; Redraw(); };
        DpiChanged += (_, e) => { _scale = e.NewDpi.DpiScaleX; Redraw(); };

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
        Closed += (_, _) => { _closed = true; _session.Finish(null); }; // Alt+F4 etc. cancels
    }

    private bool Contains(Native.POINT p) => p.X >= _display.Bounds.Left && p.X < _display.Bounds.Right && p.Y >= _display.Bounds.Top && p.Y < _display.Bounds.Bottom;

    private static BitmapSource? CaptureScreen(Native.RECT r)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(r.Width, r.Height), System.Drawing.CopyPixelOperation.SourceCopy);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, r.Width, r.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                var src = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * r.Height, data.Stride);
                src.Freeze();
                return src;
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            Log.Warn("Region picker screenshot failed: " + ex.Message);
            return null;
        }
    }

    private static void AddToolbarRow(Grid grid, int row, string label, StackPanel buttons)
    {
        var text = new TextBlock
        {
            Text = label, Foreground = Placement.Res("TextDimBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right,
        };
        var seg = new Border
        {
            Background = Placement.Res("Bg2Brush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = buttons,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, row == 0 ? 0 : 6, 0, 0),
        };
        Grid.SetRow(text, row);
        Grid.SetRow(seg, row);
        Grid.SetColumn(seg, 1);
        grid.Children.Add(text);
        grid.Children.Add(seg);
    }

    private void BuildAspectButtons()
    {
        _shapeRow.Children.Clear();
        _exactRow.Children.Clear();
        for (int i = 0; i < _session.Aspects.Count; i++)
        {
            var a = _session.Aspects[i];
            bool fits = Fits(a);
            var rb = new RadioButton
            {
                Style = (Style)Application.Current.FindResource("SegmentedRadio"), Content = a.Label, IsChecked = i == _session.AspectIndex,
                GroupName = "aspect", // one choice across both rows
                Padding = new Thickness(10, 5, 10, 5), IsEnabled = fits, Cursor = Cursors.Hand,
                ToolTip = a.ExactW == 0 ? (a.Ratio == null ? "Any shape" : $"Lock to {a.Label}")
                        : fits ? $"Exactly {a.ExactW}×{a.ExactH} pixels. The video is set to the same size, so nothing is scaled."
                        : $"{a.ExactW}×{a.ExactH} is bigger than this monitor ({_display.Width}×{_display.Height})",
            };
            ToolTipService.SetShowOnDisabled(rb, true);
            int idx = i;
            rb.Checked += (_, _) => SetAspect(idx);
            (a.ExactW > 0 ? _exactRow : _shapeRow).Children.Add(rb);
        }
    }

    private bool Fits(AspectChoice a) => a.ExactW == 0 || (a.ExactW <= _display.Width && a.ExactH <= _display.Height);

    /// <summary>The chosen exact size fits this monitor. On a smaller monitor it only locks the shape (and isn't exact).</summary>
    private bool ExactHere => _session.Aspect.ExactW > 0 && Fits(_session.Aspect);

    /// <summary>An exact-size box centred on (cx, cy), kept on this monitor.</summary>
    private Native.RECT ExactAt(double cx, double cy, AspectChoice a) =>
        ClampMove(new Native.RECT((int)(cx - a.ExactW / 2.0), (int)(cy - a.ExactH / 2.0), 0, 0), a.ExactW, a.ExactH);

    private void SetAspect(int idx)
    {
        if (_session.AspectIndex == idx) return;
        _session.AspectIndex = idx;
        foreach (var w in _session.Windows) { if (w != this) w.BuildAspectButtons(); }
        var a = _session.Aspect;
        if (ExactHere)
        {
            // Exact pixel size: centre on the current selection (or the monitor).
            var c = _sel is { } s ? new Point(s.Left + s.Width / 2.0, s.Top + s.Height / 2.0) : new Point(_display.Width / 2.0, _display.Height / 2.0);
            _sel = ExactAt(c.X, c.Y, a);
        }
        else if (a.Ratio is double r && _sel is { } s)
        {
            // Re-shape around the centre, keeping the area roughly the same.
            double area = (double)s.Width * s.Height;
            int w = (int)Math.Sqrt(area * r), h = (int)(w / r);
            if (w > _display.Width) { w = _display.Width; h = (int)(w / r); }
            if (h > _display.Height) { h = _display.Height; w = (int)(h * r); }
            int cx = s.Left + s.Width / 2, cy = s.Top + s.Height / 2;
            _sel = ClampMove(new Native.RECT(cx - w / 2, cy - h / 2, 0, 0), w, h);
        }
        ClearOthers();
        Redraw();
        Activate();
    }

    /// <summary>Dev aid for <c>--uishot</c>: a centred 16:9 selection so the chrome can be rendered.</summary>
    internal void SelectForPreview()
    {
        int w = _display.Width / 2 & ~1, h = (int)(w * 9 / 16.0) & ~1;
        _sel = ClampMove(new Native.RECT((_display.Width - w) / 2, (_display.Height - h) / 2, 0, 0), w, h);
        Redraw();
    }

    private Native.RECT ClampMove(Native.RECT topLeft, int w, int h)
    {
        w = Math.Min(w, _display.Width);
        h = Math.Min(h, _display.Height);
        int x = Math.Clamp(topLeft.Left, 0, _display.Width - w), y = Math.Clamp(topLeft.Top, 0, _display.Height - h);
        return new Native.RECT(x, y, x + w, y + h);
    }

    private void ClearOthers()
    {
        foreach (var w in _session.Windows)
            if (w != this && w._sel != null) { w._sel = null; w.Redraw(); }
    }

    // ------------------------------------------------------------------ input

    private Point Px(MouseEventArgs e) { var p = e.GetPosition(_canvas); return new Point(p.X * _scale, p.Y * _scale); }

    private bool OverUi(MouseEventArgs e) =>
        (_toolbar.IsMouseOver) || (_actions.Visibility == Visibility.Visible && _actions.IsMouseOver);

    private int CornerAt(Point px)
    {
        if (_sel is not { } s) return -1;
        double r = 12 * _scale;
        Point[] c = { new(s.Left, s.Top), new(s.Right, s.Top), new(s.Right, s.Bottom), new(s.Left, s.Bottom) };
        for (int i = 0; i < 4; i++) if (Math.Abs(px.X - c[i].X) <= r && Math.Abs(px.Y - c[i].Y) <= r) return i;
        return -1;
    }

    private static bool Inside(Native.RECT r, Point p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (OverUi(e)) return;
        var p = Px(e);
        if (e.ClickCount == 2 && _sel is { } s0 && Inside(s0, p)) { Confirm(); return; }
        _startPx = p;
        _moved = false;
        int corner = CornerAt(p);
        if (corner >= 0 && !ExactHere)
        {
            _mode = Mode.Resizing;
            _corner = corner;
            var s = _sel!.Value;
            _anchorPx = corner switch { 0 => new Point(s.Right, s.Bottom), 1 => new Point(s.Left, s.Bottom), 2 => new Point(s.Left, s.Top), _ => new Point(s.Right, s.Top) };
        }
        else if (_sel is { } s && Inside(s, p))
        {
            _mode = Mode.Moving;
            _startSel = s;
        }
        else
        {
            _mode = Mode.Drawing;
            _anchorPx = p;
        }
        ClearOthers();
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = Px(e);
        if (_mode == Mode.None)
        {
            UpdateHover(p);
            Cursor = OverUi(e) ? Cursors.Arrow
                : CornerAt(p) is int c and >= 0 && !ExactHere ? (c is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW)
                : _sel is { } s && Inside(s, p) ? Cursors.SizeAll : Cursors.Cross;
            Redraw(p);
            return;
        }
        if (!_moved && (Math.Abs(p.X - _startPx.X) > 4 * _scale || Math.Abs(p.Y - _startPx.Y) > 4 * _scale)) _moved = true;
        if (!_moved) return;
        switch (_mode)
        {
            case Mode.Moving:
            {
                var s = _startSel;
                int dx = (int)Math.Round(p.X - _startPx.X), dy = (int)Math.Round(p.Y - _startPx.Y);
                _sel = ClampMove(new Native.RECT(s.Left + dx, s.Top + dy, 0, 0), s.Width, s.Height);
                break;
            }
            case Mode.Drawing when ExactHere:
                _sel = ExactAt(p.X, p.Y, _session.Aspect);
                break;
            default:
                _sel = RectFrom(_anchorPx, p);
                break;
        }
        _hover = null;
        Redraw(p);
    }

    /// <summary>Rectangle from a fixed corner to the cursor, honouring the aspect lock and the monitor edges.</summary>
    private Native.RECT RectFrom(Point a, Point p)
    {
        double maxW = p.X >= a.X ? _display.Width - a.X : a.X, maxH = p.Y >= a.Y ? _display.Height - a.Y : a.Y;
        double w = Math.Min(Math.Abs(p.X - a.X), maxW), h = Math.Min(Math.Abs(p.Y - a.Y), maxH);
        if (_session.Aspect.Ratio is double r)
        {
            if (w / r > h) h = w / r; else w = h * r;
            if (w > maxW) { w = maxW; h = w / r; }
            if (h > maxH) { h = maxH; w = h * r; }
        }
        double x = p.X >= a.X ? a.X : a.X - w, y = p.Y >= a.Y ? a.Y : a.Y - h;
        return new Native.RECT((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(x + w), (int)Math.Round(y + h));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == Mode.None) return;
        var mode = _mode;
        _mode = Mode.None;
        ReleaseMouseCapture();
        if (!_moved && mode == Mode.Drawing)
        {
            // A click: snap to the window under the cursor, or the whole monitor.
            var target = _hover ?? new Native.RECT(0, 0, _display.Width, _display.Height);
            var a = _session.Aspect;
            if (ExactHere)
            {
                // Exact size, centred on what was clicked.
                _sel = ExactAt(target.Left + target.Width / 2.0, target.Top + target.Height / 2.0, a);
            }
            else if (a.Ratio is double r)
            {
                // Aspect locked: the largest rectangle of that shape inside what was clicked, centred.
                int w = target.Width, h = target.Height;
                if (w / (double)h > r) w = (int)(h * r); else h = (int)(w / r);
                w &= ~1; h &= ~1;
                _sel = ClampMove(new Native.RECT(target.Left + (target.Width - w) / 2, target.Top + (target.Height - h) / 2, 0, 0), w, h);
            }
            else _sel = target;
        }
        if (_sel is { } s && (s.Width < 16 || s.Height < 16)) _sel = null;
        Redraw(Px(e));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_mode != Mode.None) { _mode = Mode.None; ReleaseMouseCapture(); Redraw(); }
                else _session.Finish(null);
                e.Handled = true;
                break;
            case Key.Enter:
                Confirm();
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when _sel is { } s:
            {
                int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                _sel = ClampMove(new Native.RECT(s.Left + dx, s.Top + dy, 0, 0), s.Width, s.Height);
                Redraw();
                e.Handled = true;
                break;
            }
        }
    }

    private void UpdateHover(Point px)
    {
        _hover = null;
        if (_sel != null) return;
        int sx = _display.Bounds.Left + (int)px.X, sy = _display.Bounds.Top + (int)px.Y;
        foreach (var w in _session.TopWindows)
        {
            var b = w.Bounds;
            if (sx < b.Left || sx >= b.Right || sy < b.Top || sy >= b.Bottom) continue;
            int l = Math.Max(b.Left, _display.Bounds.Left) - _display.Bounds.Left, t = Math.Max(b.Top, _display.Bounds.Top) - _display.Bounds.Top;
            int r = Math.Min(b.Right, _display.Bounds.Right) - _display.Bounds.Left, bt = Math.Min(b.Bottom, _display.Bounds.Bottom) - _display.Bounds.Top;
            if (r - l >= 16 && bt - t >= 16) _hover = new Native.RECT(l, t, r, bt);
            return;
        }
    }

    private void Confirm()
    {
        if (_sel is not { } s)
        {
            var other = _session.Windows.FirstOrDefault(w => w._sel != null);
            if (other != null) { other.Confirm(); return; }
            s = new Native.RECT(0, 0, _display.Width, _display.Height);
        }
        // Even sizes keep every encoder happy.
        int w = s.Width & ~1, h = s.Height & ~1;
        var a = _session.Aspect;
        _session.Finish(new RegionResult(_display.DeviceName, new Native.RECT(s.Left, s.Top, s.Left + w, s.Top + h), a.ExactW == w && a.ExactH == h));
    }

    // ------------------------------------------------------------------ drawing (DIPs = px / scale)

    private void Redraw(Point? cursorPx = null)
    {
        double W = _canvas.ActualWidth, H = _canvas.ActualHeight;
        if (W <= 0) return;
        double k = 1 / _scale;
        var full = new RectangleGeometry(new Rect(0, 0, W, H));
        var shown = _sel ?? (_mode == Mode.None ? _hover : null);
        if (shown is { } r)
        {
            var hole = new Rect(r.Left * k, r.Top * k, r.Width * k, r.Height * k);
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole));
        }
        else _dim.Data = full;

        if (_sel is { } s)
        {
            var rect = new Rect(s.Left * k, s.Top * k, s.Width * k, s.Height * k);
            Place(_selRect, rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2);
            _selRect.Visibility = Visibility.Visible;
            _hoverRect.Visibility = Visibility.Collapsed;
            bool resizable = !ExactHere;
            Point[] c = { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft };
            for (int i = 0; i < 4; i++)
            {
                _handles[i].Visibility = resizable ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(_handles[i], c[i].X - 5);
                Canvas.SetTop(_handles[i], c[i].Y - 5);
            }
            var ea = _session.Aspect;
            string exact = ea.ExactW == s.Width && ea.ExactH == s.Height ? "  ✓ exact" : "";
            _sizeText.Text = $"{s.Width} × {s.Height}{exact}   at {s.Left}, {s.Top}";
            _sizeTag.Visibility = Visibility.Visible;
            _sizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double ty = rect.Y - _sizeTag.DesiredSize.Height - 6;
            if (ty < 4) ty = rect.Y + 6;
            Canvas.SetLeft(_sizeTag, Math.Clamp(rect.X, 4, Math.Max(4, W - _sizeTag.DesiredSize.Width - 4)));
            Canvas.SetTop(_sizeTag, ty);

            _actions.Visibility = _mode == Mode.None ? Visibility.Visible : Visibility.Collapsed;
            _actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var ad = _actions.DesiredSize;
            double ax = Math.Clamp(rect.Right - ad.Width, 4, Math.Max(4, W - ad.Width - 4));
            double ay = rect.Bottom + 8;
            if (ay + ad.Height > H - 4) ay = Math.Max(4, rect.Bottom - ad.Height - 8);
            Canvas.SetLeft(_actions, ax);
            Canvas.SetTop(_actions, ay);
            _crossH.Visibility = _crossV.Visibility = Visibility.Collapsed;
        }
        else
        {
            _selRect.Visibility = Visibility.Collapsed;
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
            _actions.Visibility = Visibility.Collapsed;
            if (_hover is { } hv && _mode == Mode.None)
            {
                Place(_hoverRect, hv.Left * k, hv.Top * k, hv.Width * k, hv.Height * k);
                _hoverRect.Visibility = Visibility.Visible;
                _sizeText.Text = $"{hv.Width} × {hv.Height}  · click to use this window's area";
                _sizeTag.Visibility = Visibility.Visible;
                Canvas.SetLeft(_sizeTag, hv.Left * k + 6);
                Canvas.SetTop(_sizeTag, hv.Top * k + 6);
            }
            else
            {
                _hoverRect.Visibility = Visibility.Collapsed;
                _sizeTag.Visibility = Visibility.Collapsed;
            }
            if (cursorPx is Point cp)
            {
                double x = cp.X * k, y = cp.Y * k;
                _crossH.X1 = 0; _crossH.X2 = W; _crossH.Y1 = _crossH.Y2 = Math.Round(y) + 0.5;
                _crossV.Y1 = 0; _crossV.Y2 = H; _crossV.X1 = _crossV.X2 = Math.Round(x) + 0.5;
                _crossH.Visibility = _crossV.Visibility = Visibility.Visible;
            }
        }

        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_toolbar, (W - _toolbar.DesiredSize.Width) / 2);
        Canvas.SetTop(_toolbar, 18);
    }

    private static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }
}
