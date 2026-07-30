namespace Leash.Services;

/// <summary>
/// Records which executables currently have block rules applied, so that a run killed before it
/// could clean up can be detected and undone on the next start.
///
/// This exists because netsh cannot enumerate rules in a locale-independent way: its output is
/// localized, so orphan discovery cannot be done by parsing "show rule". Remembering the exact
/// paths we blocked lets cleanup be a set of exact deletes instead.
/// </summary>
public interface IBlockStateStore
{
    /// <summary>Executable paths recorded as blocked. Empty when nothing is recorded.</summary>
    IReadOnlyList<string> Load();

    /// <summary>Replaces the recorded set. Passing an empty set is equivalent to clearing it.</summary>
    void Save(IEnumerable<string> exePaths);

    void Clear();
}
