using System.Globalization;
using System.Windows.Data;

namespace Leash.Views.Converters;

/// <summary>
/// Negates a boolean. Used to disable the configuration fields while monitoring is running,
/// so the settings the engine was started with cannot drift out from under it.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
