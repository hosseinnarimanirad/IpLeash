using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Win32;
using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>
/// Reads proxy configuration from the three places Windows and .NET actually look:
/// the WinINET per-user settings (what the Settings app and most browsers write), the
/// proxy environment variables, and the effective proxy .NET resolves for a given URL.
///
/// The last one is the authoritative answer for our own IP probe — configuration can exist but
/// be bypassed for a specific host, so only a resolved proxy really changes the reading.
/// </summary>
public sealed class ProxyService : IProxyService
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>Matches the first provider in <see cref="PublicIpService"/>.</summary>
    private static readonly Uri ProbeUri = new("https://api.ipify.org");

    public ProxyInfo GetProxyInfo()
    {
        var detail = new StringBuilder();
        var configured = false;
        string? summary = null;

        var (proxyServer, autoConfigUrl) = ReadWinInetSettings();

        if (!string.IsNullOrWhiteSpace(proxyServer))
        {
            configured = true;
            summary = proxyServer;
            detail.AppendLine($"System proxy (WinINET): {proxyServer}");
        }

        if (!string.IsNullOrWhiteSpace(autoConfigUrl))
        {
            configured = true;
            summary ??= "PAC script";
            detail.AppendLine($"Auto-config script: {autoConfigUrl}");
        }

        foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                detail.AppendLine($"{name}={value}");

                if (!string.Equals(name, "NO_PROXY", StringComparison.OrdinalIgnoreCase))
                {
                    configured = true;
                    summary ??= value;
                }
            }
        }

        var effective = ResolveEffectiveProxy();
        var affectsProbe = effective is not null;

        if (affectsProbe)
        {
            configured = true;
            summary = effective!.IsDefaultPort ? effective.Host : $"{effective.Host}:{effective.Port}";
            detail.AppendLine($"Effective for {ProbeUri.Host}: {effective}");
        }
        else if (configured)
        {
            detail.AppendLine($"Effective for {ProbeUri.Host}: direct (proxy is bypassed for this host)");
        }

        if (!configured)
        {
            return ProxyInfo.None;
        }

        if (affectsProbe)
        {
            detail.AppendLine();
            detail.AppendLine(
                "The public IP below is reported by an external service reached through this proxy, " +
                "so it is the proxy's exit address rather than this machine's own.");
        }

        return new ProxyInfo(
            IsConfigured: true,
            Summary: summary ?? "configured",
            Detail: detail.ToString().TrimEnd(),
            AffectsPublicIpCheck: affectsProbe);
    }

    private static (string? ProxyServer, string? AutoConfigUrl) ReadWinInetSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            if (key is null)
            {
                return (null, null);
            }

            // ProxyServer is only honoured when ProxyEnable is non-zero, so a stale server
            // string on a disabled proxy must not be reported as active.
            var enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
            var server = enabled ? key.GetValue("ProxyServer") as string : null;
            var autoConfig = key.GetValue("AutoConfigURL") as string;

            return (server, autoConfig);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Asks .NET what it would actually use for the probe URL. Returns null for a direct
    /// connection. This is what makes the difference between "a proxy exists" and "a proxy is in
    /// the path of the measurement we display".
    /// </summary>
    private static Uri? ResolveEffectiveProxy()
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(ProbeUri))
            {
                return null;
            }

            return proxy.GetProxy(ProbeUri);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or WebException)
        {
            return null;
        }
    }
}
