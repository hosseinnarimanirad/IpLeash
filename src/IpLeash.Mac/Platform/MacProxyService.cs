using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using IpLeash.Models;
using IpLeash.Services;

namespace IpLeash.Platform;

/// <summary>
/// Reads proxy configuration from the places macOS and .NET actually look: the System Settings
/// network proxies (via <c>scutil --proxy</c>), the proxy environment variables, and the
/// effective proxy .NET resolves for the probe URL.
///
/// The last is the authoritative one for the IP probe — on macOS .NET reads the same system
/// settings through CFNetwork, including PAC scripts and the bypass list — so only a resolved
/// proxy really changes the reading. The rest is there to explain it.
/// </summary>
public sealed class MacProxyService : IProxyService
{
    private const string ScutilPath = "/usr/sbin/scutil";

    /// <summary>Matches the first provider in <see cref="PublicIpService"/>.</summary>
    private static readonly Uri ProbeUri = new("https://api.ipify.org");

    public ProxyInfo GetProxyInfo()
    {
        var detail = new StringBuilder();
        var configured = false;
        string? summary = null;

        var system = ReadSystemProxies();

        // HTTPS first, since that is what the probe uses.
        foreach (var (label, enableKey, hostKey, portKey) in new[]
                 {
                     ("HTTPS proxy", "HTTPSEnable", "HTTPSProxy", "HTTPSPort"),
                     ("HTTP proxy", "HTTPEnable", "HTTPProxy", "HTTPPort"),
                     ("SOCKS proxy", "SOCKSEnable", "SOCKSProxy", "SOCKSPort"),
                 })
        {
            if (system.GetValueOrDefault(enableKey) == "1" && system.GetValueOrDefault(hostKey) is { Length: > 0 } host)
            {
                var value = system.GetValueOrDefault(portKey) is { Length: > 0 } port ? $"{host}:{port}" : host;
                configured = true;
                summary ??= value;
                detail.AppendLine($"System {label}: {value}");
            }
        }

        if (system.GetValueOrDefault("ProxyAutoConfigEnable") == "1")
        {
            configured = true;
            summary ??= "PAC script";
            detail.AppendLine($"Auto-config script: {system.GetValueOrDefault("ProxyAutoConfigURLString") ?? "(no URL)"}");
        }

        if (system.GetValueOrDefault("ProxyAutoDiscoveryEnable") == "1")
        {
            configured = true;
            summary ??= "auto-discovery (WPAD)";
            detail.AppendLine("Auto proxy discovery (WPAD) is on");
        }

        foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY" })
        {
            var value = Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name.ToLowerInvariant());
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

    /// <summary>
    /// The top-level <c>Key : Value</c> pairs of <c>scutil --proxy</c>. Nested arrays (the bypass
    /// list) are skipped; they only matter through the effective-proxy resolution below.
    /// </summary>
    private static Dictionary<string, string> ReadSystemProxies()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var psi = new ProcessStartInfo
            {
                // Absolute path so PATH cannot substitute a different scutil.
                FileName = ScutilPath,
                ArgumentList = { "--proxy" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return values;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            var depth = 0;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.EndsWith('{'))
                {
                    depth++;
                    continue;
                }

                if (line == "}")
                {
                    depth--;
                    continue;
                }

                // Depth 1 is the outer <dictionary>; anything deeper belongs to an array.
                var separator = line.IndexOf(" : ", StringComparison.Ordinal);
                if (depth == 1 && separator > 0)
                {
                    values[line[..separator].Trim()] = line[(separator + 3)..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No reading is not a failure worth surfacing; the effective-proxy check still runs.
        }

        return values;
    }

    /// <summary>
    /// Asks .NET what it would actually use for the probe URL. Returns null for a direct
    /// connection.
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

            var resolved = proxy.GetProxy(ProbeUri);
            return resolved is null || resolved == ProbeUri ? null : resolved;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or WebException)
        {
            return null;
        }
    }
}
