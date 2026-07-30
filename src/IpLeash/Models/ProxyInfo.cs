namespace IpLeash.Models;

/// <summary>
/// The system HTTP proxy configuration, as it affects this machine's outbound requests.
///
/// This is shown because a proxy changes what "public IP" even means: when the IP probe goes
/// through a proxy, the address reported back is the proxy's exit address, not this machine's.
/// Silently comparing that against an expected VPN IP would be misleading.
/// </summary>
/// <param name="IsConfigured">True when any proxy configuration was found.</param>
/// <param name="Summary">One-line value for the UI, e.g. "10.0.0.1:8080" or "not configured".</param>
/// <param name="Detail">Full breakdown for the tooltip: every source and its value.</param>
/// <param name="AffectsPublicIpCheck">
/// True when the IP-probe URL actually resolves through a proxy. A proxy can be configured but
/// bypassed for the probe host, in which case the reading is still this machine's own IP.
/// </param>
public sealed record ProxyInfo(
    bool IsConfigured,
    string Summary,
    string Detail,
    bool AffectsPublicIpCheck)
{
    public static ProxyInfo None { get; } = new(
        IsConfigured: false,
        Summary: "not configured",
        Detail: "No system proxy is configured, and no proxy environment variables are set.",
        AffectsPublicIpCheck: false);
}
