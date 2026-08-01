namespace IpLeash.Models;

/// <summary>One entry in the country picker.</summary>
/// <param name="Code">ISO 3166-1 alpha-2, uppercase.</param>
/// <param name="Name">Display name, and what the ComboBox's type-ahead matches on.</param>
public sealed record CountryOption(string Code, string Name)
{
    public override string ToString() => Name;
}
