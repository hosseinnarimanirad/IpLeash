using Leash.Models;

namespace Leash.Services;

/// <summary>Reports the system HTTP proxy configuration.</summary>
public interface IProxyService
{
    /// <summary>
    /// Reads current proxy configuration. Can block for a second or two when a PAC script is
    /// configured, because resolving the effective proxy runs the script — call it off the UI
    /// thread and not on a fast timer.
    /// </summary>
    ProxyInfo GetProxyInfo();
}
