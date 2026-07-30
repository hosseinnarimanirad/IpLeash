namespace Leash.Models;

/// <summary>
/// The enforcement state of the monitored application.
/// </summary>
public enum MonitorStatus
{
    /// <summary>Monitoring is not running; no rules are managed.</summary>
    Idle,

    /// <summary>Public IP matched the expected value; no firewall block is in place.</summary>
    Allowed,

    /// <summary>Public IP did not match; inbound and outbound block rules are in place.</summary>
    Blocked,

    /// <summary>
    /// The public IP could not be determined. Treated as a mismatch (fail-closed), so the
    /// block rules are in place, but surfaced distinctly so the user can tell it apart
    /// from a genuine IP mismatch.
    /// </summary>
    Unknown,
}
