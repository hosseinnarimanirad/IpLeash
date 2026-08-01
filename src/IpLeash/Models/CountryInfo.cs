namespace IpLeash.Models;

/// <summary>
/// Where an address geolocates to.
///
/// There is deliberately no "unknown" instance: callers receive <c>null</c> when the country
/// could not be determined. A sentinel with an empty code would compare equal to an expected
/// value that is also empty, and that comparison would read as a match — the one failure a kill
/// switch must never have.
/// </summary>
/// <param name="Code">ISO 3166-1 alpha-2, uppercase, always exactly two ASCII letters.</param>
/// <param name="Name">Display name, e.g. "United Kingdom".</param>
/// <param name="City">City, when the provider supplied one. Display only, never part of a decision.</param>
public sealed record CountryInfo(string Code, string Name, string? City)
{
    /// <summary>
    /// Normalises a country code from a provider or a settings file to uppercase alpha-2.
    /// Returns null for anything that is not exactly two ASCII letters, which is what keeps an
    /// empty-equals-empty comparison from ever being treated as a match.
    /// </summary>
    public static string? NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length != 2 || !char.IsAsciiLetter(trimmed[0]) || !char.IsAsciiLetter(trimmed[1]))
        {
            return null;
        }

        return trimmed.ToUpperInvariant();
    }
}
