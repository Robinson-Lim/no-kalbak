using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DnfItemChecker.Core.Comparison;

namespace DnfItemChecker.App.Converters;

/// <summary>Maps a <see cref="ComparisonOutcome"/> to its badge brush (green match / red below / muted otherwise).</summary>
public sealed class OutcomeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Match = Freeze("#7FB069");
    private static readonly SolidColorBrush Below = Freeze("#C75D5D");
    private static readonly SolidColorBrush Muted = Freeze("#6E5436");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ComparisonOutcome outcome
            ? outcome switch
            {
                ComparisonOutcome.Match => Match,
                ComparisonOutcome.Below => Below,
                _ => Muted,
            }
            : Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Maps a <see cref="ComparisonOutcome"/> to a short Korean badge label.</summary>
public sealed class OutcomeToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ComparisonOutcome outcome
            ? outcome switch
            {
                ComparisonOutcome.Match => "★ 최상급",
                ComparisonOutcome.Below => "최상급 미만",
                ComparisonOutcome.NotFound => "판별 실패",
                ComparisonOutcome.Unmeasured => "미측정/재인식 필요",
                _ => "판정 불가",
            }
            : "-";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Collapses an element when the bound string is null/empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Collapses an element when the bound value is false (optionally inverted with parameter "invert").</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
