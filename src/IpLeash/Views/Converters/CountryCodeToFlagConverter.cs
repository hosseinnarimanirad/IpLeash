using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace IpLeash.Views.Converters;

/// <summary>
/// An ISO 3166-1 alpha-2 code to its flag image, falling back to a neutral placeholder for
/// anything missing or unrecognised.
///
/// Every bitmap is decoded once and frozen, then shared by every Image bound through this
/// converter — the saved-chip list alone would otherwise decode the same flag repeatedly.
/// </summary>
public sealed class CountryCodeToFlagConverter : IValueConverter
{
    private const string Prefix = "pack://application:,,,/IpLeash;component/Assets/Flags/";

    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new(StringComparer.Ordinal);

    private static readonly BitmapImage? Fallback = Load("_unknown");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Lowercased to match the asset names: MSBuild stores resource paths in the .g.resources
        // lowercased, and pack-URI lookup follows suit, so anything else silently misses.
        var code = (value as string)?.Trim().ToLowerInvariant();

        return code is { Length: 2 }
            ? Cache.GetOrAdd(code, Load) ?? Fallback
            : Fallback;
    }

    private static BitmapImage? Load(string code)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(Prefix + code + ".png", UriKind.Absolute);

            // Decode now and release the stream. Without this the BitmapImage holds the resource
            // open and decodes lazily on the render thread.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException
                                     or UriFormatException or NotSupportedException)
        {
            // WPF reports a missing pack resource as an IOException, so this catch is the
            // unknown-country path rather than defensive padding.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
