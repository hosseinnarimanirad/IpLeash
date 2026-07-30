namespace Leash.Models;

/// <summary>Progress of the public-IP lookup, so the UI can distinguish "waiting" from "failed".</summary>
public enum IpProbeState
{
    /// <summary>No lookup has been attempted yet.</summary>
    Unknown,

    /// <summary>A lookup is in flight.</summary>
    Checking,

    /// <summary>An address was resolved.</summary>
    Resolved,

    /// <summary>Every provider failed.</summary>
    Failed,
}

/// <summary>
/// An immutable view of everything the engine knows at one instant. The engine publishes one
/// of these on every change; the ViewModel is a pure projection of the latest snapshot.
/// </summary>
/// <param name="Status">Current enforcement state, derived from the global expected IP.</param>
/// <param name="PublicIp">Last successfully resolved public IP, or null if it could not be determined.</param>
/// <param name="ProbeState">Whether a lookup is pending, succeeded, or failed.</param>
/// <param name="Reason">Human-readable explanation of <paramref name="Status"/>.</param>
/// <param name="LastCheckedAt">When the last public-IP lookup completed, or null if none has.</param>
/// <param name="Proxy">System proxy configuration, which changes what the public IP represents.</param>
/// <param name="Apps">Per-app live state, including each executable's PIDs and block state.</param>
/// <param name="Adapters">Local adapter IPv4 addresses, display-only.</param>
/// <param name="IsRunning">Whether monitoring is active.</param>
public sealed record MonitorSnapshot(
    MonitorStatus Status,
    string? PublicIp,
    IpProbeState ProbeState,
    string Reason,
    DateTimeOffset? LastCheckedAt,
    ProxyInfo Proxy,
    IReadOnlyList<MonitoredAppState> Apps,
    IReadOnlyList<AdapterInfo> Adapters,
    bool IsRunning)
{
    public static MonitorSnapshot Initial { get; } = new(
        MonitorStatus.Idle,
        PublicIp: null,
        ProbeState: IpProbeState.Unknown,
        Reason: "Not monitoring.",
        LastCheckedAt: null,
        Proxy: ProxyInfo.None,
        Apps: Array.Empty<MonitoredAppState>(),
        Adapters: Array.Empty<AdapterInfo>(),
        IsRunning: false);
}
