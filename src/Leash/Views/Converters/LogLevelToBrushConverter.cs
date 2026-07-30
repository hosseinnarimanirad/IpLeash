using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Leash.Models;

namespace Leash.Views.Converters;

/// <summary>Colours a log row by severity.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = Frozen(0x33, 0x33, 0x33);
    private static readonly SolidColorBrush Warning = Frozen(0x8A, 0x55, 0x00);
    private static readonly SolidColorBrush Error = Frozen(0xB3, 0x26, 0x1E);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LogLevel level
            ? level switch
            {
                LogLevel.Warning => Warning,
                LogLevel.Error => Error,
                _ => Info,
            }
            : Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
