using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Leash.Services;

/// <summary>
/// Windows Firewall enforcement via <c>netsh advfirewall</c>.
///
/// netsh is used rather than the INetFwPolicy2 COM API because it needs no interop surface and
/// every action is a single auditable command line. The trade-off is that netsh's output is
/// localized, so success is decided purely by exit code and stdout is only ever surfaced as
/// diagnostic text — never parsed.
/// </summary>
public sealed class FirewallService : IFirewallService
{
    /// <summary>Prefix for every rule this app creates, so rules are identifiable in wf.msc.</summary>
    public const string RuleNamePrefix = "Leash Block - ";

    private static readonly string NetshPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

    private readonly IBlockStateStore _stateStore;
    private readonly Lock _trackedGate = new();

    /// <summary>Paths believed to be blocked right now; mirrored to <see cref="IBlockStateStore"/>.</summary>
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);

    public FirewallService(IBlockStateStore stateStore)
    {
        _stateStore = stateStore;

        foreach (var path in _stateStore.Load())
        {
            _tracked.Add(path);
        }
    }

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Both direction rules share one name, so a single delete removes the pair.
    /// The file name keeps it readable in the Windows Firewall UI; the path hash keeps it unique,
    /// which matters now that a list can hold two different installs of the same executable name
    /// (an npm claude.exe and a native-install claude.exe would otherwise collide on one rule).
    /// </summary>
    public static string RuleNameFor(string exePath) =>
        $"{RuleNamePrefix}{Path.GetFileName(exePath)} [{PathToken(exePath)}]";

    /// <summary>
    /// Prefixes this app has written rules under in the past. These must never be edited to match
    /// the current name: a rule is only ever findable by the exact name it was created with, so
    /// dropping a historical prefix would strand it — leaving an application blocked with nothing
    /// in the UI able to release it.
    /// </summary>
    private static readonly string[] LegacyRuleNamePrefixes = ["NetMonitor Block - "];

    /// <summary>
    /// Every name a previous version could have used for this executable: each historical prefix
    /// in both the original un-scoped form and the later path-hashed form.
    /// </summary>
    private static IEnumerable<string> LegacyRuleNamesFor(string exePath)
    {
        var fileName = Path.GetFileName(exePath);
        var token = PathToken(exePath);

        foreach (var prefix in LegacyRuleNamePrefixes)
        {
            yield return prefix + fileName;
            yield return $"{prefix}{fileName} [{token}]";
        }

        // The current prefix also had an un-scoped form before rules were path-hashed.
        yield return RuleNamePrefix + fileName;
    }

    private static string PathToken(string exePath)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(exePath).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = exePath.ToLowerInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 4);
    }

    public async Task<FirewallResult> ApplyBlockAsync(string exePath, CancellationToken ct = default)
    {
        if (!TryValidate(exePath, out var error))
        {
            return new FirewallResult(false, error);
        }

        var rule = RuleNameFor(exePath);

        // Remove first so repeated applies don't stack duplicate rules.
        await RunNetshAsync($"advfirewall firewall delete rule name=\"{rule}\"", ct).ConfigureAwait(false);

        var outbound = await RunNetshAsync(
            $"advfirewall firewall add rule name=\"{rule}\" dir=out action=block program=\"{exePath}\" enable=yes profile=any",
            ct).ConfigureAwait(false);

        var inbound = await RunNetshAsync(
            $"advfirewall firewall add rule name=\"{rule}\" dir=in action=block program=\"{exePath}\" enable=yes profile=any",
            ct).ConfigureAwait(false);

        if (outbound.ExitCode != 0 || inbound.ExitCode != 0)
        {
            // Partial application is worse than none: roll back so state stays coherent.
            await RunNetshAsync($"advfirewall firewall delete rule name=\"{rule}\"", ct).ConfigureAwait(false);
            Untrack(exePath);
            return new FirewallResult(
                false,
                $"netsh add failed (out={outbound.ExitCode}, in={inbound.ExitCode}): {outbound.Output} {inbound.Output}".Trim());
        }

        Track(exePath);
        return FirewallResult.Ok;
    }

    public async Task<FirewallResult> RemoveBlockAsync(string exePath, CancellationToken ct = default)
    {
        if (!TryValidate(exePath, out var error))
        {
            return new FirewallResult(false, error);
        }

        var rule = RuleNameFor(exePath);
        var result = await RunNetshAsync($"advfirewall firewall delete rule name=\"{rule}\"", ct).ConfigureAwait(false);

        // Exit code 1 means "no rules match", which is the desired end state for a remove, so
        // confirm against the firewall rather than trusting the delete's exit code.
        if (await IsBlockedAsync(exePath, ct).ConfigureAwait(false))
        {
            return new FirewallResult(false, $"netsh delete failed ({result.ExitCode}): {result.Output}");
        }

        Untrack(exePath);
        return FirewallResult.Ok;
    }

    public async Task<bool> IsBlockedAsync(string exePath, CancellationToken ct = default)
    {
        if (!TryValidate(exePath, out _))
        {
            return false;
        }

        var result = await RunNetshAsync(
            $"advfirewall firewall show rule name=\"{RuleNameFor(exePath)}\"", ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<IReadOnlyList<string>> RemoveOrphanedRulesAsync(CancellationToken ct = default)
    {
        List<string> recorded;
        lock (_trackedGate)
        {
            recorded = _stateStore.Load().Union(_tracked, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var removed = new List<string>();

        foreach (var path in recorded)
        {
            // Rules written by an earlier version carry an earlier name — from before rules were
            // path-scoped, and from before the app was renamed. Sweep every historical form,
            // otherwise an upgrade would strand an app blocked forever.
            foreach (var legacyName in LegacyRuleNamesFor(path))
            {
                await RunNetshAsync(
                    $"advfirewall firewall delete rule name=\"{legacyName}\"", ct).ConfigureAwait(false);
            }

            if (await IsBlockedAsync(path, ct).ConfigureAwait(false))
            {
                var result = await RemoveBlockAsync(path, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    removed.Add(path);
                }
            }
        }

        lock (_trackedGate)
        {
            _tracked.Clear();
            _stateStore.Clear();
        }

        return removed;
    }

    private void Track(string exePath)
    {
        lock (_trackedGate)
        {
            _tracked.Add(exePath);
            _stateStore.Save(_tracked);
        }
    }

    private void Untrack(string exePath)
    {
        lock (_trackedGate)
        {
            _tracked.Remove(exePath);
            _stateStore.Save(_tracked);
        }
    }

    /// <summary>
    /// netsh takes quoted values, so a path containing a double quote would break out of the
    /// argument. Windows disallows quotes in file names, making rejection safe rather than limiting.
    /// </summary>
    private static bool TryValidate(string exePath, out string error)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            error = "No executable specified.";
            return false;
        }

        if (exePath.Contains('"'))
        {
            error = "Executable path contains a double quote, which cannot be passed to netsh.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task<(int ExitCode, string Output)> RunNetshAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            // Absolute path so PATH cannot be used to substitute a different netsh.
            FileName = NetshPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, "Failed to start netsh.exe.");
            }

            // Start both reads before waiting, otherwise a full pipe buffer deadlocks the child.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = ((await stdoutTask.ConfigureAwait(false)) + " " + (await stderrTask.ConfigureAwait(false))).Trim();
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
