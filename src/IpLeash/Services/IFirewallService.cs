namespace IpLeash.Services;

/// <summary>Outcome of a firewall mutation. netsh output is localized, so only the exit code is trusted.</summary>
/// <param name="Success">True when every underlying netsh invocation exited zero.</param>
/// <param name="Detail">Raw netsh output, surfaced in the log when <paramref name="Success"/> is false.</param>
public sealed record FirewallResult(bool Success, string Detail)
{
    public static FirewallResult Ok { get; } = new(true, string.Empty);
}

/// <summary>
/// Creates and removes Windows Firewall rules that block a single executable in both directions.
/// All operations are idempotent.
/// </summary>
public interface IFirewallService
{
    /// <summary>True when the current process holds the administrator rights netsh needs.</summary>
    bool IsElevated { get; }

    /// <summary>Adds inbound and outbound block rules for <paramref name="exePath"/>.</summary>
    Task<FirewallResult> ApplyBlockAsync(string exePath, CancellationToken ct = default);

    /// <summary>Removes the rules previously added for <paramref name="exePath"/>.</summary>
    Task<FirewallResult> RemoveBlockAsync(string exePath, CancellationToken ct = default);

    /// <summary>True when this app's block rules for <paramref name="exePath"/> exist right now.</summary>
    Task<bool> IsBlockedAsync(string exePath, CancellationToken ct = default);

    /// <summary>
    /// Removes rules left behind by a previous run that was killed before it could clean up.
    /// Returns the executable paths whose rules were removed.
    /// </summary>
    Task<IReadOnlyList<string>> RemoveOrphanedRulesAsync(CancellationToken ct = default);
}
