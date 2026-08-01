using System.Globalization;
using System.Windows.Data;
using IpLeash.Models;

namespace IpLeash.Views.Converters;

/// <summary>
/// Text plus a "hide addresses" flag to the text as it should be displayed.
///
/// A MultiValueConverter rather than a set of masked properties on the ViewModel: the addresses
/// on screen live in half a dozen places, several of them inside item templates over plain
/// models (log entries, adapters), which have no ViewModel to add a property to.
/// </summary>
public sealed class MaskIpConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Length > 0 ? values[0] as string : null;

        // Anything other than an explicit true — including UnsetValue while bindings are still
        // resolving — leaves the text alone. Failing open here is right: this is a display
        // preference, and a half-initialised binding must not blank out the address.
        var hide = values.Length > 1 && values[1] is true;

        return hide ? IpMasker.Mask(text) : text;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
