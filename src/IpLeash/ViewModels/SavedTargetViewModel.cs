using CommunityToolkit.Mvvm.ComponentModel;
using IpLeash.Models;

namespace IpLeash.ViewModels;

/// <summary>
/// One saved lock target as a chip: either an exact address or a country, each with its flag.
///
/// A country chip knows its flag outright. An address chip has to be told, so
/// <see cref="FlagCode"/> starts from whatever the geolocation cache already holds — instant and
/// offline — and is filled in later by <see cref="MainViewModel"/> for anything still unknown.
/// </summary>
public sealed partial class SavedTargetViewModel : ObservableObject
{
    public SavedTargetViewModel(SavedTarget model, string? cachedCountryCode = null)
    {
        Kind = model.Kind;
        Ip = model.Ip.Trim();
        CountryCode = CountryInfo.NormalizeCode(model.CountryCode) ?? string.Empty;

        if (Kind == MatchMode.Country)
        {
            DisplayText = CountryCatalog.NameOf(CountryCode);
            ToolTipText = $"Lock to {DisplayText} ({CountryCode})";
            _flagCode = CountryCode;
        }
        else
        {
            DisplayText = Ip;
            ToolTipText = $"Use {Ip} as the expected address";
            _flagCode = CountryInfo.NormalizeCode(cachedCountryCode);
        }
    }

    public MatchMode Kind { get; }

    /// <summary>Only meaningful when <see cref="Kind"/> is <see cref="MatchMode.ExactIp"/>.</summary>
    public string Ip { get; }

    /// <summary>Only meaningful when <see cref="Kind"/> is <see cref="MatchMode.Country"/>.</summary>
    public string CountryCode { get; }

    public string DisplayText { get; }

    public string ToolTipText { get; }

    /// <summary>Switches the chip's font: an address wants the mono face, a country name does not.</summary>
    public bool IsCountry => Kind == MatchMode.Country;

    /// <summary>What the flag converter binds to. Null renders the neutral placeholder.</summary>
    [ObservableProperty]
    private string? _flagCode;

    /// <summary>True for an address chip whose country has not been established yet.</summary>
    public bool NeedsFlagLookup => Kind == MatchMode.ExactIp && FlagCode is null;

    public SavedTarget ToModel() => Kind == MatchMode.Country
        ? SavedTarget.ForCountry(CountryCode)
        : SavedTarget.ForIp(Ip);

    /// <summary>Identity for dedup: two chips are the same when they lock to the same thing.</summary>
    public bool Matches(SavedTargetViewModel other) =>
        Kind == other.Kind
        && (Kind == MatchMode.Country
            ? string.Equals(CountryCode, other.CountryCode, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Ip, other.Ip, StringComparison.OrdinalIgnoreCase));
}
