using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using KidBlockUI.Models;

namespace KidBlockUI.Views;

// DM15: the LogKind dot/line colours and the search-highlight brush now live in
// Themes/Theme.xaml, the single semantic-token source of truth. These converters
// resolve the brushes BY KEY from the merged application resources at convert
// time, so the LogTailPanel filter legend (Ellipse Fill) and the live log lines
// always share one colour source. A frozen fallback covers the design-time /
// no-Application case so a converter never returns null.
internal static class ThemeBrush
{
    public static readonly SolidColorBrush Transparent = Frozen(Colors.Transparent);
    private static readonly SolidColorBrush Neutral = Frozen(Colors.Gray);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // LogKind dot/line colour by key; a visible neutral grey if resources are
    // unavailable (design-time / no Application).
    public static Brush Kind(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Neutral;

    // Search-highlight brush; transparent (no highlight) if resources are unavailable.
    public static Brush Highlight()
        => Application.Current?.TryFindResource("SearchHighlight") as Brush ?? Transparent;
}

public sealed class LogKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogKind k ? k switch
        {
            LogKind.Block        => ThemeBrush.Kind("LogKindBlock"),
            LogKind.Allow        => ThemeBrush.Kind("LogKindAllow"),
            LogKind.Override     => ThemeBrush.Kind("LogKindOverride"),
            LogKind.ScheduleTick => ThemeBrush.Kind("LogKindTick"),
            LogKind.Install      => ThemeBrush.Kind("LogKindInstall"),
            LogKind.Error        => ThemeBrush.Kind("LogKindError"),
            LogKind.Dns          => ThemeBrush.Kind("LogKindDns"),
            _                    => ThemeBrush.Kind("LogKindOther"),
        } : ThemeBrush.Kind("LogKindOther");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// MultiBinding converter: (message, searchText) -> highlight Brush.
// SearchHighlight (translucent yellow) when searchText is non-empty AND message
// contains it; Transparent otherwise.
public sealed class SearchHighlightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return ThemeBrush.Transparent;
        var msg = values[0] as string ?? string.Empty;
        var needle = values[1] as string ?? string.Empty;
        if (needle.Length == 0) return ThemeBrush.Transparent;
        return msg.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
            ? ThemeBrush.Highlight()
            : ThemeBrush.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Maps LogKind -> bold font weight (only Error renders bold; everything else normal).
public sealed class LogKindToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogKind.Error ? FontWeights.Bold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
