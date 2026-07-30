using System.Net.NetworkInformation;
using System.Net.Sockets;
using IpLeash.Models;

namespace IpLeash.Services;

/// <inheritdoc cref="ILocalIpService"/>
public sealed class LocalIpService : ILocalIpService
{
    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var results = new List<AdapterInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                // Loopback is noise. Tunnel adapters are kept deliberately — that is the VPN.
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var isTunnel = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    results.Add(new AdapterInfo(
                        nic.Name,
                        nic.Description,
                        unicast.Address.ToString(),
                        isTunnel));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Adapter table unavailable mid-reconfiguration; report what we have.
        }

        return results;
    }
}
