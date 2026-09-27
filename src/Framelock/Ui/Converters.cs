using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Framelock.Ui;

/// <summary>bool → Visible/Collapsed. ConverterParameter "invert" flips it.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is true;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

/// <summary>Enum ⇄ bool for segmented radio buttons: IsChecked="{Binding Mode, Converter={StaticResource EnumBool}, ConverterParameter=Fit}".</summary>
public sealed class EnumToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p as string;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && p is string s ? Enum.Parse(t, s) : Binding.DoNothing;
}

/// <summary>Visible when the value's string form equals one of the "|"-separated parameters ("!" prefix negates).</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = p as string ?? "";
        bool negate = s.StartsWith('!');
        if (negate) s = s[1..];
        bool match = s.Split('|').Contains(value?.ToString());
        return match ^ negate ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value != null && (value is not string s || s.Length > 0);
        if (p as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>0..1 → "85%".</summary>
public sealed class Percent : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? $"{Math.Round(d * 100)}%" : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Linear 0..1 amplitude → 0..1 meter position on a -60..0 dB scale.</summary>
public sealed class LevelToMeter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is float f ? f : value is double d ? d : 0;
        if (v <= 0.001) return 0.0;
        double db = 20 * Math.Log10(v);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>TimeSpan → "mm:ss" / "h:mm:ss".</summary>
public sealed class DurationText : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is TimeSpan ts ? Engine.RecorderController.FormatDuration(ts) : "00:00";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>PascalCase enum → "Pascal case" for display.</summary>
public sealed class EnumText : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = value?.ToString() ?? "";
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append(' ').Append(char.ToLowerInvariant(s[i]));
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
