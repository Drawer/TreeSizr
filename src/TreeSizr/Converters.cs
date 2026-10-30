using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TreeSizr;

/// <summary>
/// Gives each tree row a width that ends at the same x position regardless of indentation,
/// so the size/percent/bar/file columns line up across levels.
/// Inputs: the TreeView's ActualWidth and the row's depth.
/// </summary>
public sealed class TreeRowWidthConverter : IMultiValueConverter
{
    public const double Indent = 19;          // TreeViewItem expander column width
    public const double RightReserve = 26;    // vertical scrollbar plus borders

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is [double width, int depth])
            return Math.Max(510, width - Indent * (depth + 1) - RightReserve);
        return 510.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns a 0–100 percentage into a star width, so a two-column grid splits proportionally.
/// ConverterParameter "rest" gives the remainder (100 − value) for the second column.
/// </summary>
public sealed class PercentToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value is double d ? Math.Clamp(d, 0, 100) : 0;
        return new GridLength(parameter as string == "rest" ? 100 - percent : percent, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A byte count as text, such as "1,5 GB".</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? Format.Bytes(bytes) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows or hides an optional column: true gives the width in ConverterParameter, false gives 0.
/// Works for both a Grid ColumnDefinition (GridLength) and a GridViewColumn (double).
/// </summary>
public sealed class BoolToColumnWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double width = value is true ? double.Parse((string)parameter, CultureInfo.InvariantCulture) : 0;
        return targetType == typeof(GridLength) ? new GridLength(width) : width;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
