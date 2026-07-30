using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Leash.Models;

namespace Leash.Views.Converters;

/// <summary>
/// Maps the enforcement state to the banner colour. Lives in the view layer so the ViewModel
/// never has to expose a <see cref="Brush"/>.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Idle = Frozen(0x5A, 0x63, 0x73);
    private static readonly SolidColorBrush Allowed = Frozen(0x1E, 0x7F, 0x4B);
    private static readonly SolidColorBrush Blocked = Frozen(0xB3, 0x26, 0x1E);
    private static readonly SolidColorBrush Unknown = Frozen(0xB5, 0x71, 0x0E);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MonitorStatus status
            ? status switch
            {
                MonitorStatus.Allowed => Allowed,
                MonitorStatus.Blocked => Blocked,
                MonitorStatus.Unknown => Unknown,
                _ => Idle,
            }
            : Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
