using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using KidBlockUI.Models;

namespace KidBlockUI.Views;

public sealed class LogKindToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Block        = new(Color.FromRgb(0xE5, 0x50, 0x50));
    public static readonly SolidColorBrush Allow        = new(Color.FromRgb(0x33, 0xCC, 0x66));
    public static readonly SolidColorBrush Override     = new(Color.FromRgb(0xFF, 0xC8, 0x33));
    public static readonly SolidColorBrush ScheduleTick = new(Color.FromRgb(0x99, 0x99, 0x99));
    public static readonly SolidColorBrush Install      = new(Color.FromRgb(0x60, 0xA0, 0xE0));
    public static readonly SolidColorBrush Error        = new(Color.FromRgb(0xFF, 0x40, 0x40));
    public static readonly SolidColorBrush Other        = new(Color.FromRgb(0x88, 0x88, 0x88));

    static LogKindToBrushConverter()
    {
        Block.Freeze(); Allow.Freeze(); Override.Freeze(); ScheduleTick.Freeze();
        Install.Freeze(); Error.Freeze(); Other.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogKind k ? k switch
        {
            LogKind.Block        => Block,
            LogKind.Allow        => Allow,
            LogKind.Override     => Override,
            LogKind.ScheduleTick => ScheduleTick,
            LogKind.Install      => Install,
            LogKind.Error        => Error,
            _                    => Other,
        } : (object)Other;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// MultiBinding converter: (message, searchText) -> highlight Brush.
// Yellow-translucent when searchText is non-empty AND message contains it.
public sealed class SearchHighlightConverter : IMultiValueConverter
{
    public static readonly SolidColorBrush Match = new(Color.FromArgb(0x55, 0xFF, 0xE0, 0x40));
    public static readonly SolidColorBrush None  = new(Colors.Transparent);

    static SearchHighlightConverter()
    {
        Match.Freeze(); None.Freeze();
    }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return None;
        var msg = values[0] as string ?? string.Empty;
        var needle = values[1] as string ?? string.Empty;
        if (needle.Length == 0) return None;
        return msg.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ? Match : None;
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
