namespace IpLeash.Models;

/// <summary>One IPv4 address bound to one up network interface. Display-only.</summary>
/// <param name="Name">Adapter name as shown in Windows, e.g. "Wi-Fi" or "ProtonVPN".</param>
/// <param name="Description">Driver description, useful for telling virtual adapters apart.</param>
/// <param name="IpAddress">The IPv4 address assigned to the adapter.</param>
/// <param name="IsTunnel">True for VPN-style tunnel adapters.</param>
public sealed record AdapterInfo(string Name, string Description, string IpAddress, bool IsTunnel);
