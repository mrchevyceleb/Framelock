using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Framelock.Core;
using Framelock.Engine;

namespace Framelock.Ui;

/// <summary>
/// Bottom-right notifications. They live in one transparent, click-through-where-empty window that is
/// excluded from capture, so they never end up in a recording.
/// </summary>
public static class Toast
{
    private static Window? _host;
    private static StackPanel? _stack;
    private const int MaxVisible = 4;

    public static void Show(Notification n)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Show(n)); return; }
        try { ShowCore(n); }
        catch (Exception ex) { Log.Warn("Toast failed: " + ex.Message); }
    }

    private static void EnsureHost()
    {
        if (_host != null) return;
        _stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 16, 16) };
        _host = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 400,
            Content = _stack,
            Title = "Framelock notifications",
        };
        _host.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_host).Handle;
            Native.MakeToolWindow(h, true);
            Native.ExcludeFromCapture(h, true);
        };
        _host.Closed += (_, _) => { _host = null; _stack = null; };
    }

    private static void PositionHost()
    {
        var wa = SystemParameters.WorkArea; // primary monitor, DIPs
        _host!.Height = Math.Min(wa.Height, 640);
        _host.Left = wa.Right - _host.Width;
        _host.Top = wa.Bottom - _host.Height;
    }

    private static void ShowCore(Notification n)
    {
        EnsureHost();
        PositionHost();
        var card = BuildCard(n);
        while (_stack!.Children.Count >= MaxVisible) _stack.Children.RemoveAt(0);
        _stack.Children.Add(card);
        if (!_host!.IsVisible) _host.Show();

        card.Opacity = 0;
        var tt = new TranslateTransform(24, 0);
        card.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        var life = TimeSpan.FromSeconds(n.IsError ? 9 : n.FilePath != null ? 6 : 3.5);
        var timer = new DispatcherTimer { Interval = life };
        timer.Tick += (_, _) =>
        {
            if (card.IsMouseOver) return; // keep while hovered
            timer.Stop();
            Dismiss(card);
        };
        timer.Start();
    }

    private static void Dismiss(FrameworkElement card)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            _stack?.Children.Remove(card);
            if (_stack?.Children.Count == 0) _host?.Hide();
        };
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static FrameworkElement BuildCard(Notification n)
    {
        var accent = n.IsError ? Res("RecBrush") : n.FilePath != null ? Res("GoodBrush") : Res("AccentBrush");
        var title = new TextBlock { Text = n.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        var body = new StackPanel();
        body.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(n.Message))
            body.Children.Add(new TextBlock { Text = n.Message, FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 90 });

        var close = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Width = 24, Height = 24, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        close.Content = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 9 };

        if (n.FilePath != null && File.Exists(n.FilePath))
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var open = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = "Open", Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
            open.Click += (_, _) => { OpenFile(n.FilePath); };
            var reveal = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = "Show in folder", Padding = new Thickness(10, 3, 10, 3), FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
            reveal.Click += (_, _) => MainViewModel.RevealFile(n.FilePath);
            actions.Children.Add(open);
            actions.Children.Add(reveal);
            body.Children.Add(actions);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 2, 12, 2) };
        Grid.SetColumn(body, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(bar);
        grid.Children.Add(body);
        grid.Children.Add(close);

        var card = new Border
        {
            Background = Res("Bg2Brush"),
            BorderBrush = Res("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 8, 10),
            Margin = new Thickness(0, 8, 0, 0),
            Child = grid,
            Cursor = n.FilePath != null ? Cursors.Hand : Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
        };
        close.Click += (_, e) => { e.Handled = true; Dismiss(card); };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) != null) return;
            if (n.FilePath != null) MainViewModel.RevealFile(n.FilePath);
            else if (Application.Current.MainWindow is MainWindow mw) mw.BringToFront();
            Dismiss(card);
        };
        return card;
    }

    private static Button? FindButton(DependencyObject d)
    {
        while (d != null)
        {
            if (d is Button b) return b;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open failed: " + ex.Message); }
    }
}
