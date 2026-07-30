namespace Leash.Models;

/// <summary>A known application located on this machine by <see cref="Services.IAppDiscoveryService"/>.</summary>
/// <param name="Name">Display name to use for the list entry, e.g. "Claude Code".</param>
/// <param name="ExecutablePaths">Every install of that app found here, deduplicated.</param>
/// <param name="Sources">Where each path came from, for the log — e.g. "npm global prefix", "running process".</param>
public sealed record DiscoveredApp(
    string Name,
    IReadOnlyList<string> ExecutablePaths,
    IReadOnlyList<string> Sources);
