using IpLeash.Models;

namespace IpLeash.Views.Services;

/// <summary>
/// The ViewModel's only route to modal UI. Keeping dialogs behind this interface is what lets
/// MainViewModel stay free of any reference to WPF types.
/// </summary>
public interface IDialogService
{
    /// <summary>Prompts for an executable. Returns null if the user cancelled.</summary>
    string? BrowseForExecutable(string? initialPath);

    /// <summary>
    /// Shows the running-process picker. The candidate list is supplied by the caller so the
    /// dialog itself stays free of service dependencies. Returns the chosen executable paths,
    /// empty if cancelled.
    /// </summary>
    IReadOnlyList<string> PickRunningExecutables(IReadOnlyList<RunningExecutable> candidates);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);
}
