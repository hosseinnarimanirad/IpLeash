using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace IpLeash.Models;

/// <summary>
/// Replaces IP addresses in displayed text with a fixed mask, for screenshots and screen sharing.
///
/// Applied to whole strings rather than to individual address properties on purpose: most of the
/// places an address appears are inside a sentence the engine composed ("Public IP 1.2.3.4 does
/// not match expected 5.6.7.8"). Masking centrally here means a new log message cannot quietly
/// reintroduce a leak the way a per-property approach would.
///
/// Display only. Nothing that is compared, stored, or written to the firewall ever passes
/// through this.
/// </summary>
public static partial class IpMasker
{
    /// <summary>Fixed width regardless of the real value, so the mask does not leak digit counts.</summary>
    private const string MaskedV4 = "***.***.***.***";

    private const string MaskedV6 = "****:****:****:****";

    /// <summary>
    /// Deliberately loose: it only has to propose candidates, because every match is then checked
    /// with <see cref="IPAddress.TryParse"/> before anything is replaced. That two-stage approach
    /// is what keeps a clock time like "10:52:59" — which looks a lot like an IPv6 fragment — from
    /// being mangled.
    ///
    /// Three alternatives, in this order:
    ///   1. IPv4-mapped IPv6 (::ffff:1.2.3.4), first so the trailing IPv4 is not matched alone,
    ///      which would leave half the address on screen.
    ///   2. Plain IPv4.
    ///   3. Plain IPv6. Its leading edge is a lookbehind rather than \b, because \b cannot match
    ///      before the colon that starts "::1".
    /// </summary>
    [GeneratedRegex(
        // The tail is "not followed by another octet" rather than "not followed by a dot", so a
        // sentence-ending period does not force a backtrack that would mask only the first half.
        @"(?<![0-9A-Fa-f:.])(?:[0-9A-Fa-f]{0,4}:)+\d{1,3}(?:\.\d{1,3}){3}(?!\.?\d)" +
        @"|\b\d{1,3}(?:\.\d{1,3}){3}\b" +
        @"|(?<![0-9A-Fa-f:.])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![0-9A-Fa-f:])")]
    private static partial Regex CandidateRegex();

    /// <summary>Masks every address in a string. Returns the input unchanged when it holds none.</summary>
    public static string? Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return CandidateRegex().Replace(text, static match =>
            IPAddress.TryParse(match.Value, out var address) ? MaskFor(address) : match.Value);
    }

    /// <summary>Masks a value that is known to be an address on its own.</summary>
    public static string? MaskAddress(string? value) =>
        IPAddress.TryParse(value?.Trim(), out var address) ? MaskFor(address) : value;

    private static string MaskFor(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? MaskedV6 : MaskedV4;
}
