using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IpLeash.Models;

namespace IpLeash.ViewModels;

/// <summary>One selectable executable in the process picker.</summary>
public sealed partial class ProcessCandidateViewModel : ObservableObject
{
    public ProcessCandidateViewModel(RunningExecutable executable, bool canBlock)
    {
        Executable = executable;
        CanBlock = canBlock;
    }

    public RunningExecutable Executable { get; }

    /// <summary>
    /// False for processes whose real path could not be read (protected or system processes).
    /// They are still listed, because hiding them looks like a bug, but they cannot be selected —
    /// a firewall rule needs a path.
    /// </summary>
    public bool CanBlock { get; }

    public string ImageName => Executable.ImageName;

    public string Path => Executable.Path;

    public string PidText => Executable.PidText;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Backs the "add from running process" dialog. Lists distinct executables rather than raw
/// processes, so an app running as four processes is one selectable row.
///
/// Filtering is done by rebuilding the visible collection rather than with an ICollectionView,
/// because ICollectionView lives in WindowsBase and would put a WPF type in the ViewModel layer.
/// </summary>
public sealed partial class ProcessPickerViewModel : ObservableObject
{
    private readonly List<ProcessCandidateViewModel> _all;

    public ProcessPickerViewModel(IReadOnlyList<RunningExecutable> candidates)
    {
        _all = candidates
            .Select(c => new ProcessCandidateViewModel(c, canBlock: c.Path.Contains(Path.DirectorySeparatorChar)))
            .OrderByDescending(c => c.CanBlock)
            .ThenBy(c => c.ImageName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
    }

    /// <summary>The filtered view bound by the dialog.</summary>
    public ObservableCollection<ProcessCandidateViewModel> Candidates { get; } = [];

    /// <summary>Set by the ViewModel to close the dialog; bound to the window via DialogCloser.</summary>
    [ObservableProperty]
    private bool? _dialogResult;

    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>Selections survive filtering because they live on the candidates, not the view.</summary>
    public IReadOnlyList<string> SelectedPaths =>
        _all.Where(c => c.IsSelected && c.CanBlock).Select(c => c.Path).ToList();

    /// <summary>Default name offered for the new entry: the image name of the first selection.</summary>
    public string SuggestedName =>
        _all.FirstOrDefault(c => c.IsSelected && c.CanBlock)?.ImageName ?? "New app";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void Accept() => DialogResult = true;

    [RelayCommand]
    private void Cancel() => DialogResult = false;

    private void ApplyFilter()
    {
        Candidates.Clear();

        foreach (var candidate in _all)
        {
            if (Matches(candidate))
            {
                Candidates.Add(candidate);
            }
        }
    }

    private bool Matches(ProcessCandidateViewModel candidate)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        return candidate.ImageName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
               || candidate.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }
}
