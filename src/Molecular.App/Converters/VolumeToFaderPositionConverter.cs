using System.Globalization;
using System.Windows.Data;

namespace Molecular.App.Converters;

public sealed class VolumeToFaderPositionConverter : IMultiValueConverter
{
    private const double TrackTop = 18;
    private const double KnobRadius = 18;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var volume = values.Length > 0 && values[0] is double number ? number : 0;
        var availableHeight = values.Length > 1 && values[1] is double height ? height : 0;
        var normalized = Math.Clamp(volume, 0, 100) / 100d;
        var trackHeight = Math.Max(0, Math.Round(availableHeight - (TrackTop * 2)));
        var fillHeight = Math.Clamp(Math.Round(normalized * trackHeight), 0, trackHeight);

        return (parameter as string) switch
        {
            "TrackHeight" => trackHeight,
            "FillTop" => TrackTop + trackHeight - fillHeight,
            "FillHeight" => fillHeight,
            "KnobTop" => Math.Round(TrackTop + ((1 - normalized) * trackHeight) - KnobRadius),
            _ => 0d
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
