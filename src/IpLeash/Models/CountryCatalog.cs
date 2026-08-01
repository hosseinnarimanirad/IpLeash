using System.Collections.Frozen;
using System.Globalization;

namespace IpLeash.Models;

/// <summary>
/// The countries that can be locked to, and the names shown for any country that turns up.
///
/// Two lists on purpose. <see cref="All"/> is what the picker offers: only officially assigned
/// ISO 3166-1 alpha-2 codes, so a user cannot lock to something no address will ever report.
/// <see cref="NameOf"/> also resolves the extra codes geolocation providers really do return
/// (EU for anycast ranges, AP for the APNIC region, XK for Kosovo), because those must still
/// display sensibly when observed.
///
/// The code list is fixed rather than derived from <see cref="CultureInfo.GetCultures"/>: that
/// call omits every country with no associated culture, and its contents shift with whichever
/// ICU version the machine happens to ship — a picker whose entries differ between two Windows
/// builds is worse than one that is simply written down.
/// </summary>
public static class CountryCatalog
{
    /// <summary>Officially assigned ISO 3166-1 alpha-2 codes.</summary>
    private static readonly string[] AssignedCodes =
    [
        "AD", "AE", "AF", "AG", "AI", "AL", "AM", "AO", "AQ", "AR", "AS", "AT", "AU", "AW", "AX", "AZ",
        "BA", "BB", "BD", "BE", "BF", "BG", "BH", "BI", "BJ", "BL", "BM", "BN", "BO", "BQ", "BR", "BS",
        "BT", "BV", "BW", "BY", "BZ",
        "CA", "CC", "CD", "CF", "CG", "CH", "CI", "CK", "CL", "CM", "CN", "CO", "CR", "CU", "CV", "CW",
        "CX", "CY", "CZ",
        "DE", "DJ", "DK", "DM", "DO", "DZ",
        "EC", "EE", "EG", "EH", "ER", "ES", "ET",
        "FI", "FJ", "FK", "FM", "FO", "FR",
        "GA", "GB", "GD", "GE", "GF", "GG", "GH", "GI", "GL", "GM", "GN", "GP", "GQ", "GR", "GS", "GT",
        "GU", "GW", "GY",
        "HK", "HM", "HN", "HR", "HT", "HU",
        "ID", "IE", "IL", "IM", "IN", "IO", "IQ", "IR", "IS", "IT",
        "JE", "JM", "JO", "JP",
        "KE", "KG", "KH", "KI", "KM", "KN", "KP", "KR", "KW", "KY", "KZ",
        "LA", "LB", "LC", "LI", "LK", "LR", "LS", "LT", "LU", "LV", "LY",
        "MA", "MC", "MD", "ME", "MF", "MG", "MH", "MK", "ML", "MM", "MN", "MO", "MP", "MQ", "MR", "MS",
        "MT", "MU", "MV", "MW", "MX", "MY", "MZ",
        "NA", "NC", "NE", "NF", "NG", "NI", "NL", "NO", "NP", "NR", "NU", "NZ",
        "OM",
        "PA", "PE", "PF", "PG", "PH", "PK", "PL", "PM", "PN", "PR", "PS", "PT", "PW", "PY",
        "QA",
        "RE", "RO", "RS", "RU", "RW",
        "SA", "SB", "SC", "SD", "SE", "SG", "SH", "SI", "SJ", "SK", "SL", "SM", "SN", "SO", "SR", "SS",
        "ST", "SV", "SX", "SY", "SZ",
        "TC", "TD", "TF", "TG", "TH", "TJ", "TK", "TL", "TM", "TN", "TO", "TR", "TT", "TV", "TW", "TZ",
        "UA", "UG", "UM", "US", "UY", "UZ",
        "VA", "VC", "VE", "VG", "VI", "VN", "VU",
        "WF", "WS",
        "YE", "YT",
        "ZA", "ZM", "ZW",
    ];

    /// <summary>
    /// Names for codes <see cref="RegionInfo"/> rejects or renders unhelpfully, plus the
    /// non-ISO codes providers return. Consulted before RegionInfo so these always win.
    /// </summary>
    private static readonly Dictionary<string, string> NameOverrides = new(StringComparer.Ordinal)
    {
        ["AQ"] = "Antarctica",
        ["BV"] = "Bouvet Island",
        ["EH"] = "Western Sahara",
        ["HM"] = "Heard & McDonald Islands",
        ["PN"] = "Pitcairn Islands",
        ["SJ"] = "Svalbard & Jan Mayen",
        ["TF"] = "French Southern Territories",
        ["UM"] = "U.S. Outlying Islands",

        // Not ISO-assigned, so absent from the picker, but genuinely returned by providers.
        ["XK"] = "Kosovo",
        ["EU"] = "European Union",
        ["AP"] = "Asia/Pacific Region",
    };

    private static readonly FrozenDictionary<string, string> NamesByCode = BuildNames();

    /// <summary>Every lockable country, sorted by display name.</summary>
    public static IReadOnlyList<CountryOption> All { get; } = AssignedCodes
        .Select(code => new CountryOption(code, NamesByCode[code]))
        .OrderBy(option => option.Name, StringComparer.InvariantCulture)
        .ToList();

    /// <summary>
    /// Display name for a country code. Falls back to the code itself, so an unrecognised value
    /// from a provider shows as "ZZ" rather than as blank text.
    /// </summary>
    public static string NameOf(string? code)
    {
        var normalized = CountryInfo.NormalizeCode(code);
        if (normalized is null)
        {
            return "unknown";
        }

        return NamesByCode.TryGetValue(normalized, out var name) ? name : normalized;
    }

    /// <summary>The picker entry for a code, or null when the code is not lockable.</summary>
    public static CountryOption? Find(string? code)
    {
        var normalized = CountryInfo.NormalizeCode(code);
        return normalized is null
            ? null
            : All.FirstOrDefault(option => option.Code == normalized);
    }

    private static FrozenDictionary<string, string> BuildNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var code in AssignedCodes.Concat(NameOverrides.Keys).Distinct(StringComparer.Ordinal))
        {
            names[code] = ResolveName(code);
        }

        return names.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string ResolveName(string code)
    {
        if (NameOverrides.TryGetValue(code, out var overridden))
        {
            return overridden;
        }

        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            // ICU does not know this region. The code is still a usable label.
            return code;
        }
    }
}
