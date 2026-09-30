using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Framelock.Core;

namespace Framelock.Graphics;

/// <summary>Rasterizes overlay content (images, styled text) to premultiplied BGRA on the UI thread.</summary>
public static class OverlayRenderer
{
    private const int MaxImageSide = 4096;

    public static OverlayBitmap LoadImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.PixelWidth > MaxImageSide || frame.PixelHeight > MaxImageSide)
        {
            double s = (double)MaxImageSide / Math.Max(frame.PixelWidth, frame.PixelHeight);
            frame = new TransformedBitmap(frame, new ScaleTransform(s, s));
        }
        // Normalize to sRGB-ish 96 DPI premultiplied BGRA.
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var px = new byte[w * h * 4];
        converted.CopyPixels(px, w * 4, 0);
        return new OverlayBitmap { Width = w, Height = h, Pixels = px };
    }

    public static OverlayBitmap RenderText(OverlayItem item)
    {
        const double fontSize = 160;
        var text = string.IsNullOrEmpty(item.Text) ? " " : item.Text;
        var typeface = new Typeface(new FontFamily(item.FontFamily), item.Italic ? FontStyles.Italic : FontStyles.Normal,
                                    item.Bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
        var fill = ParseBrush(item.TextColor, Colors.White);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, fill, 1.0);
        var geometry = ft.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds.IsEmpty ? new Rect(0, 0, 10, 10) : geometry.Bounds;

        double outline = Math.Max(0, item.OutlineWidth) * fontSize;
        bool hasBg = ParseColor(item.TextBackground, Colors.Transparent).A > 0;
        double padX = (hasBg ? fontSize * 0.35 : 0) + outline + (item.Shadow ? fontSize * 0.12 : 0) + 4;
        double padY = (hasBg ? fontSize * 0.22 : 0) + outline + (item.Shadow ? fontSize * 0.12 : 0) + 4;
        int w = (int)Math.Ceiling(bounds.Width + padX * 2);
        int h = (int)Math.Ceiling(bounds.Height + padY * 2);
        w = Math.Clamp(w, 8, 8192); h = Math.Clamp(h, 8, 4096);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (hasBg)
            {
                var bg = ParseBrush(item.TextBackground, Colors.Transparent);
                dc.DrawRoundedRectangle(bg, null, new Rect(0.5, 0.5, w - 1, h - 1), h * 0.22, h * 0.22);
            }
            dc.PushTransform(new TranslateTransform(padX - bounds.X, padY - bounds.Y));
            if (outline > 0)
            {
                var pen = new Pen(ParseBrush(item.OutlineColor, Colors.Black), outline * 2) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, geometry);
            }
            dc.DrawGeometry(fill, null, geometry);
            dc.Pop();
        }
        if (item.Shadow)
            visual.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = fontSize * 0.12, ShadowDepth = fontSize * 0.04, Opacity = 0.75, Direction = 315 };

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return new OverlayBitmap { Width = w, Height = h, Pixels = px };
    }

    /// <summary>(Re)builds the bitmap for an overlay. Returns false and sets LoadError on failure.</summary>
    public static bool Refresh(OverlayItem item)
    {
        try
        {
            if (item.Kind == OverlayKind.Webcam)
            {
                item.Bitmap = null;
                item.LoadError = null;
                return true;
            }
            if (item.Kind == OverlayKind.Image)
            {
                if (string.IsNullOrEmpty(item.ImagePath) || !File.Exists(item.ImagePath))
                {
                    item.Bitmap = null;
                    item.LoadError = "Image file not found";
                    return false;
                }
                item.Bitmap = LoadImage(item.ImagePath);
            }
            else item.Bitmap = RenderText(item);
            item.LoadError = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Overlay '{item.Name}' failed to load", ex);
            item.LoadError = ex.Message;
            item.Bitmap = null;
            return false;
        }
    }

    public static Color ParseColor(string? s, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(s) ? fallback : (Color)ColorConverter.ConvertFromString(s); }
        catch { return fallback; }
    }

    private static SolidColorBrush ParseBrush(string? s, Color fallback)
    {
        var b = new SolidColorBrush(ParseColor(s, fallback));
        b.Freeze();
        return b;
    }
}

/// <summary>Immutable per-frame copy of an overlay's layout (the UI edits OverlayItem; the video thread reads this).</summary>
public sealed record OverlayState(OverlayBitmap? Bitmap, double AspectRatio, OverlayAnchor Anchor, double X, double Y, double Width, double Margin,
                                  double Opacity, bool Visible, OverlayShowMode ShowMode, int IntervalSeconds, int DurationSeconds, bool Fade)
{
    public OverlayKind Kind { get; init; }
    public static OverlayState From(OverlayItem o) =>
        new(o.Bitmap, o.AspectRatio, o.Anchor, o.X, o.Y, o.Width, o.Margin, o.Opacity, o.Visible, o.ShowMode, o.IntervalSeconds, o.DurationSeconds, o.Fade) { Kind = o.Kind };
}

/// <summary>Pure layout math shared by the compositor and the on-preview overlay editor.</summary>
public static class OverlayLayout
{
    public static Rect GetRect(OverlayItem o, int outW, int outH) => GetRect(OverlayState.From(o), outW, outH);

    public static Rect GetRect(OverlayState o, int outW, int outH)
    {
        double w = Math.Max(0.001, o.Width) * outW;
        double h = w * o.AspectRatio;
        double m = o.Margin * Math.Min(outW, outH);
        double x, y;
        switch (o.Anchor)
        {
            case OverlayAnchor.TopLeft: x = m; y = m; break;
            case OverlayAnchor.TopCenter: x = (outW - w) / 2; y = m; break;
            case OverlayAnchor.TopRight: x = outW - w - m; y = m; break;
            case OverlayAnchor.MiddleLeft: x = m; y = (outH - h) / 2; break;
            case OverlayAnchor.Center: x = (outW - w) / 2; y = (outH - h) / 2; break;
            case OverlayAnchor.MiddleRight: x = outW - w - m; y = (outH - h) / 2; break;
            case OverlayAnchor.BottomLeft: x = m; y = outH - h - m; break;
            case OverlayAnchor.BottomCenter: x = (outW - w) / 2; y = outH - h - m; break;
            case OverlayAnchor.BottomRight: x = outW - w - m; y = outH - h - m; break;
            default: x = o.X * outW; y = o.Y * outH; break;
        }
        return new Rect(Math.Round(x), Math.Round(y), Math.Round(w), Math.Round(h));
    }

    /// <summary>Opacity for time-based overlays ("show my handle for 10 s every 5 minutes").</summary>
    public static double GetOpacity(OverlayState o, double seconds)
    {
        if (!o.Visible) return 0;
        if (o.ShowMode == OverlayShowMode.Always) return o.Opacity;
        double interval = Math.Max(1, o.IntervalSeconds);
        double dur = Math.Clamp(o.DurationSeconds, 1, interval);
        double t = ((seconds % interval) + interval) % interval;
        if (t >= dur) return 0;
        double a = 1;
        if (o.Fade)
        {
            const double f = 0.6;
            if (t < f) a = t / f;
            else if (t > dur - f) a = (dur - t) / f;
        }
        return o.Opacity * Math.Clamp(a, 0, 1);
    }
}
