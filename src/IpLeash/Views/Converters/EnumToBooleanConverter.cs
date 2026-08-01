using System.Globalization;
using System.Windows.Data;

namespace IpLeash.Views.Converters;

/// <summary>
/// Binds one radio button to one value of an enum: checked when the bound value equals the
/// converter parameter.
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null
        && parameter is string name
        && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Both radios in a group write back: the one being cleared fires false immediately after
        // the one being set fires true. Returning anything but DoNothing for false would let the
        // cleared radio overwrite the value the checked one just chose.
        if (value is not true || parameter is not string name)
        {
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        return enumType.IsEnum && Enum.TryParse(enumType, name, ignoreCase: false, out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}
