namespace Framelock.Core;

public sealed record ResolutionPreset(string Name, int Width, int Height, string Aspect)
{
    public bool IsSource => Width == 0;
    public bool IsCustom => Width < 0;
    public string Label => IsSource ? Name : IsCustom ? Name : $"{Name} — {Width}×{Height}";
    public override string ToString() => Label;
}

public sealed record AspectPreset(string Name, int W, int H)
{
    public double Ratio => H == 0 ? 0 : (double)W / H;
    public override string ToString() => Name;
}

public sealed record QuickPreset(string Name, string Description, int Width, int Height, int Fps, int Quality);

public static class Presets
{
    public static readonly ResolutionPreset[] Resolutions =
    {
        new("Match source (no scaling)", 0, 0, ""),
        new("4K UHD", 3840, 2160, "16:9"),
        new("1440p QHD", 2560, 1440, "16:9"),
        new("1080p Full HD", 1920, 1080, "16:9"),
        new("720p HD", 1280, 720, "16:9"),
        new("5K", 5120, 2880, "16:9"),
        new("8K UHD", 7680, 4320, "16:9"),
        new("Ultrawide 1440p", 3440, 1440, "21:9"),
        new("Ultrawide 1080p", 2560, 1080, "21:9"),
        new("Super ultrawide", 5120, 1440, "32:9"),
        new("Dual 4K", 7680, 2160, "32:9"),
        new("Vertical 1080 (Shorts / TikTok)", 1080, 1920, "9:16"),
        new("Vertical 4K", 2160, 3840, "9:16"),
        new("Square 1080", 1080, 1080, "1:1"),
        new("Square 4K", 2160, 2160, "1:1"),
        new("Classic 4:3 1440", 1920, 1440, "4:3"),
        new("Custom…", -1, -1, ""),
    };

    public static readonly int[] FrameRates = { 24, 25, 30, 48, 50, 60, 90, 100, 120, 144, 165, 240 };

    public static readonly AspectPreset[] Aspects =
    {
        new("Free", 0, 0),
        new("Match output", -1, -1),
        new("16:9", 16, 9),
        new("21:9", 21, 9),
        new("32:9", 32, 9),
        new("9:16", 9, 16),
        new("4:3", 4, 3),
        new("1:1", 1, 1),
        new("4:5", 4, 5),
    };

    public static readonly QuickPreset[] Quick =
    {
        new("YouTube 4K · 60", "3840×2160, 60 fps, very high quality", 3840, 2160, 60, 20),
        new("YouTube 4K · 120", "3840×2160, 120 fps for smooth gameplay", 3840, 2160, 120, 21),
        new("YouTube 1440p · 120", "2560×1440, 120 fps", 2560, 1440, 120, 20),
        new("YouTube 1080p · 60", "1920×1080, 60 fps, small files", 1920, 1080, 60, 20),
        new("Shorts / TikTok", "1080×1920 vertical, 60 fps", 1080, 1920, 60, 20),
    };

    public static readonly int[] AudioBitrates = { 128, 160, 192, 256, 320, 384 };
    public static readonly int[] ReplayLengths = { 15, 30, 60, 90, 120, 180, 300, 600 };
    public static readonly int[] Countdowns = { 0, 3, 5, 10 };

    /// <summary>YouTube's recommended upload bitrates (SDR, high frame rate) with ~1.5× headroom for a master recording.</summary>
    public static int SuggestedBitrateMbps(int w, int h, int fps)
    {
        double pixels = (double)w * h;
        double baseMbps = pixels switch
        {
            >= 7680 * 4320 * 0.9 => 120,
            >= 3840 * 2160 * 0.9 => 53,
            >= 2560 * 1440 * 0.9 => 24,
            >= 1920 * 1080 * 0.9 => 12,
            _ => 7.5,
        };
        double fpsFactor = fps <= 30 ? 0.66 : fps <= 60 ? 1.0 : fps / 60.0 * 0.8;
        return (int)Math.Round(baseMbps * fpsFactor * 1.5);
    }

    public static string QualityLabel(int q) => q switch
    {
        <= 14 => "Near-lossless (huge files)",
        <= 18 => "Visually lossless",
        <= 22 => "Very high",
        <= 26 => "High",
        <= 30 => "Good",
        _ => "Small files",
    };
}
