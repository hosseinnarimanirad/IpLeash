using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IpLeash.Models;
using IpLeash.Services;

namespace IpLeash.Platform;

/// <summary>
/// Enumerates IPv4 addresses on up interfaces, labelled with the names System Settings uses.
///
/// .NET reports BSD device names on macOS (en0, utun4), which say little to most people, so the
/// hardware-port names ("Wi-Fi", "Ethernet") are looked up once through networksetup and
/// cached. VPN tunnels have no hardware port and keep their device name, marked as a tunnel.
/// </summary>
public sealed class MacLocalIpService : ILocalIpService
{
    private const string NetworkSetupPath = "/usr/sbin/networksetup";

    /// <summary>
    /// The port table only changes when hardware does. Re-read at most this often, and only when
    /// an unknown device shows up.
    /// </summary>
    private static readonly TimeSpan PortRefreshInterval = TimeSpan.FromMinutes(1);

    private static readonly string[] TunnelPrefixes = ["utun", "ipsec", "ppp", "tun", "tap", "wg", "gif", "stf"];

    private readonly object _gate = new();
    private Dictionary<string, string> _portNames = new(StringComparer.Ordinal);
    private DateTimeOffset _portsReadAt = DateTimeOffset.MinValue;

    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var results = new List<AdapterInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var isTunnel = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                               || TunnelPrefixes.Any(p => nic.Name.StartsWith(p, StringComparison.Ordinal));

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var friendly = isTunnel ? null : PortNameFor(nic.Name);
                    results.Add(new AdapterInfo(
                        friendly ?? nic.Name,
                        friendly is null ? nic.Description : nic.Name,
                        unicast.Address.ToString(),
                        isTunnel));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Interface table unavailable mid-reconfiguration; report what we have.
        }

        return results;
    }

    private string? PortNameFor(string device)
    {
        lock (_gate)
        {
            if (_portNames.TryGetValue(device, out var name))
            {
                return name;
            }

            if (DateTimeOffset.UtcNow - _portsReadAt < PortRefreshInterval)
            {
                return null;
            }

            _portsReadAt = DateTimeOffset.UtcNow;
            _portNames = ReadHardwarePorts();
            return _portNames.GetValueOrDefault(device);
        }
    }

    /// <summary>
    /// Parses <c>networksetup -listallhardwareports</c>: pairs of "Hardware Port: Wi-Fi" and
    /// "Device: en0" lines.
    /// </summary>
    private static Dictionary<string, string> ReadHardwarePorts()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NetworkSetupPath,
                ArgumentList = { "-listallhardwareports" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return map;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            string? port = null;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Hardware Port:", StringComparison.Ordinal))
                {
                    port = line["Hardware Port:".Length..].Trim();
                }
                else if (line.StartsWith("Device:", StringComparison.Ordinal) && port is not null)
                {
                    map[line["Device:".Length..].Trim()] = port;
                    port = null;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            // Names are cosmetic; device names are shown instead.
        }

        return map;
    }
}
