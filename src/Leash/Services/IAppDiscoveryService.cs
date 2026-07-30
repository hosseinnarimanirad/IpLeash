using Leash.Models;

namespace Leash.Services;

/// <summary>
/// Locates known applications wherever they happen to be installed on this machine.
///
/// The point is portability of configuration: Claude's install path differs between systems
/// (npm global prefix, a native install under the profile, a per-user desktop install), so a
/// hand-typed path does not survive being moved to another machine. Discovery re-resolves it.
/// </summary>
public interface IAppDiscoveryService
{
    /// <summary>
    /// Returns every known app found here, each with all of its installs. Apps that are not
    /// installed are omitted rather than returned empty.
    /// </summary>
    IReadOnlyList<DiscoveredApp> DiscoverKnownApps();
}
