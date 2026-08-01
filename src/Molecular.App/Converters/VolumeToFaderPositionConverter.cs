using System.Globalization;
using System.Windows.Data;

namespace Molecular.App.Converters;

public sealed class VolumeToFaderPositionConverter : IMultiValueConverter
{
    // Must match VerticalFader template: Grid Margin top/bottom and Thumb.Height.
    private const double TrackTop = 18;
    private const double KnobHeight = 22;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var volume = values.Length > 0 && values[0] is double number ? number : 0;
        var availableHeight = values.Length > 1 && values[1] is double height ? height : 0;
        var normalized = Math.Clamp(volume, 0, 100) / 100d;
        var trackHeight = Math.Max(0, Math.Round(availableHeight - (TrackTop * 2)));
        var travel = Math.Max(0, trackHeight - KnobHeight);
        var thumbTop = TrackTop + ((1 - normalized) * travel);
        var thumbCenter = thumbTop + (KnobHeight / 2d);
        var fillBottom = TrackTop + trackHeight;
        var fillHeight = Math.Clamp(Math.Round(fillBottom - thumbCenter), 0, trackHeight);
        var fillTop = fillBottom - fillHeight;

        return (parameter as string) switch
        {
            "TrackHeight" => trackHeight,
            "FillTop" => Math.Round(fillTop),
            "FillHeight" => fillHeight,
            "KnobTop" => Math.Round(thumbTop),
            _ => 0d
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
