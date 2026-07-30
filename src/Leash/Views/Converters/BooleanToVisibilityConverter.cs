using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Leash.Views.Converters;

/// <summary>
/// True to Visible, false to Collapsed. Pass "Invert" as the converter parameter to reverse it.
/// (The framework's own converter has no inversion option, which the elevation banner needs.)
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
