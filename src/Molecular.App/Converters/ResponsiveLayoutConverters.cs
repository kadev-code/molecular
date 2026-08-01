using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Molecular.App.Converters;

public sealed class ResponsiveExpandedColumnsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double number ? number : 0;
        var itemCount = values.Length > 1 && values[1] is int count ? count : 1;
        var capacity = width >= 1320 ? 3 : width >= 760 ? 2 : 1;
        return Math.Max(1, Math.Min(itemCount, capacity));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class ExpandedCardHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var viewportHeight = value is double number ? number : 0;
        // Leave room for card margin so the expanded section fits without a scrollbar
        // at the default window size.
        return Math.Clamp(viewportHeight - 20, 380, 450);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ExpandedPanelWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width >= 820
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ExpandedPanelVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width >= 820 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class PeakToMeterHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var peakPercent = values.Length > 0 && values[0] is double peak ? peak : 0;
        var availableHeight = values.Length > 1 && values[1] is double height ? height : 0;
        var trackHeight = Math.Max(0, availableHeight - 12);
        if (peakPercent <= 0.1 || trackHeight <= 0) return 0d;

        var peakDb = DbMeterScale.PeakPercentToDb(peakPercent);
        return DbMeterScale.Normalize(peakDb) * trackHeight;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class DbToScalePositionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var availableHeight = value is double height ? height : 0;
        if (!double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            db = DbMeterScale.MinimumDb;

        const double trackPadding = 6;
        const double labelOffset = 4;
        const double labelHeight = 10;
        var trackHeight = Math.Max(0, availableHeight - (trackPadding * 2));
        var position = trackPadding + (DbMeterScale.PositionFromTop(db) * trackHeight) - labelOffset;
        return Math.Clamp(position, 0, Math.Max(0, availableHeight - labelHeight));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal static class DbMeterScale
{
    public const double MinimumDb = -60;
    public const double MaximumDb = 0;

    public static double PeakPercentToDb(double peakPercent) =>
        Math.Clamp(20 * Math.Log10(Math.Clamp(peakPercent, 0.1, 100) / 100d), MinimumDb, MaximumDb);

    public static double Normalize(double db) =>
        Math.Clamp((db - MinimumDb) / (MaximumDb - MinimumDb), 0, 1);

    public static double PositionFromTop(double db) => 1 - Normalize(db);
}
