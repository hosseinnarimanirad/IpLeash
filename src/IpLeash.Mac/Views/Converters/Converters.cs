using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using IpLeash.Models;

namespace IpLeash.Views.Converters;

/// <summary>
/// Maps the enforcement state to the banner colour. Lives in the view layer so the ViewModel
/// never has to expose a <see cref="IBrush"/>.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly IBrush Idle = new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x73));
    private static readonly IBrush Allowed = new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x7F, 0x4B));
    private static readonly IBrush Blocked = new ImmutableSolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));
    private static readonly IBrush Unknown = new ImmutableSolidColorBrush(Color.FromRgb(0xB5, 0x71, 0x0E));

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
}

/// <summary>Maps the enforcement state to the banner headline.</summary>
public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MonitorStatus status
            ? status switch
            {
                MonitorStatus.Allowed => "ALLOWED",
                MonitorStatus.Blocked => "BLOCKED",
                MonitorStatus.Unknown => "UNKNOWN — BLOCKING (FAIL-CLOSED)",
                _ => "NOT MONITORING",
            }
            : "NOT MONITORING";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colours a log row by severity.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly IBrush Info = new ImmutableSolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly IBrush Warning = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x55, 0x00));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));

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
}

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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Both radios in a group write back: the one being cleared fires false right after the one
        // being set fires true. Anything but DoNothing for false would let the cleared radio
        // overwrite the value the checked one just chose.
        if (value is not true || parameter is not string name)
        {
            return BindingOperations.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        return enumType.IsEnum && Enum.TryParse(enumType, name, ignoreCase: false, out var parsed)
            ? parsed
            : BindingOperations.DoNothing;
    }
}

/// <summary>
/// Text plus a "hide addresses" flag to the text as it should be displayed. A multi-binding, as
/// on Windows, because several masked texts live in templates over plain models (log entries,
/// adapters) that have no ViewModel to add a property to.
/// </summary>
public sealed class MaskIpConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Count > 0 ? values[0] as string : null;

        // Anything other than an explicit true leaves the text alone: a half-initialised binding
        // must not blank out the address.
        var hide = values.Count > 1 && values[1] is true;

        return hide ? IpMasker.Mask(text) : text;
    }
}

/// <summary>
/// An ISO 3166-1 alpha-2 code to its flag, falling back to a neutral placeholder. Every bitmap is
/// decoded once and shared by every image bound through this converter.
/// </summary>
public sealed class CountryCodeToFlagConverter : IValueConverter
{
    private const string Prefix = "avares://IpLeash/Assets/Flags/";

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    private static readonly Bitmap? Fallback = Load("_unknown");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var code = (value as string)?.Trim().ToLowerInvariant();

        return code is { Length: 2 }
            ? Cache.GetOrAdd(code, Load) ?? Fallback
            : Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Bitmap? Load(string code)
    {
        var uri = new Uri(Prefix + code + ".png");
        if (!AssetLoader.Exists(uri))
        {
            return null;
        }

        using var stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
    }
}
