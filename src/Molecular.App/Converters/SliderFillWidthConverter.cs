using System.Globalization;
using System.Windows.Data;

namespace Molecular.App.Converters;

/// <summary>
/// Keeps the horizontal fill ending at the center of the slider thumb.
/// WPF reserves the thumb width from its value track, so a separate
/// percentage-based ProgressBar cannot stay aligned near the endpoints.
/// </summary>
public sealed class SliderFillWidthConverter : IMultiValueConverter
{
    private const double ThumbWidth = 18;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var value = values.Length > 0 && values[0] is double current ? current : 0;
        var minimum = values.Length > 1 && values[1] is double min ? min : 0;
        var maximum = values.Length > 2 && values[2] is double max ? max : 100;
        var availableWidth = values.Length > 3 && values[3] is double width ? width : 0;

        var range = maximum - minimum;
        var normalized = range <= 0 ? 0 : Math.Clamp((value - minimum) / range, 0, 1);
        var travel = Math.Max(0, availableWidth - ThumbWidth);
        return (ThumbWidth / 2) + (normalized * travel);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
