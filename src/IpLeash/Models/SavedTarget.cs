namespace IpLeash.Models;

/// <summary>
/// One remembered lock target, offered back as a one-click chip. Either an exact address or a
/// country, so switching between known exit nodes does not mean retyping anything.
///
/// Settable properties rather than a positional record, matching <see cref="MonitoredApp"/>:
/// System.Text.Json fills these without constructor matching, so a partially written entry
/// degrades to defaults instead of failing the whole file.
/// </summary>
public sealed class SavedTarget
{
    public MatchMode Kind { get; set; } = MatchMode.ExactIp;

    /// <summary>Only meaningful when <see cref="Kind"/> is <see cref="MatchMode.ExactIp"/>.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2. Only meaningful when <see cref="Kind"/> is <see cref="MatchMode.Country"/>.</summary>
    public string CountryCode { get; set; } = string.Empty;

    public static SavedTarget ForIp(string ip) => new()
    {
        Kind = MatchMode.ExactIp,
        Ip = ip.Trim(),
    };

    public static SavedTarget ForCountry(string code) => new()
    {
        Kind = MatchMode.Country,
        CountryCode = CountryInfo.NormalizeCode(code) ?? string.Empty,
    };

    /// <summary>False for entries that survived a salvaged load with nothing usable in them.</summary>
    public bool IsUsable() => Kind switch
    {
        MatchMode.ExactIp => !string.IsNullOrWhiteSpace(Ip),
        MatchMode.Country => CountryInfo.NormalizeCode(CountryCode) is not null,
        _ => false,
    };
}
