namespace Leash.Models;

/// <summary>
/// One entry in the monitored list: a display name plus every executable that belongs to it.
///
/// An app is a group rather than a single path because one product can ship several binaries
/// (different install methods, a CLI plus a desktop build), and they should be blocked and
/// unblocked as a unit. A firewall rule is still created per executable — the grouping exists
/// so the user manages one thing instead of five.
/// </summary>
public sealed class MonitoredApp
{
    /// <summary>Stable identity across saves, so UI selection survives a reload.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<string> ExecutablePaths { get; set; } = [];

    /// <summary>Disabled entries stay in the list but are never blocked.</summary>
    public bool Enabled { get; set; } = true;
}
