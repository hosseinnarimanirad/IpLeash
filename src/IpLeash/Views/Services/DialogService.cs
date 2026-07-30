using System.IO;
using System.Windows;
using Microsoft.Win32;
using IpLeash.Models;
using IpLeash.ViewModels;

namespace IpLeash.Views.Services;

/// <inheritdoc cref="IDialogService"/>
public sealed class DialogService : IDialogService
{
    public string? BrowseForExecutable(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the application to monitor",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(initialPath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                    dialog.FileName = Path.GetFileName(initialPath);
                }
            }
            catch (ArgumentException)
            {
                // Unusable path; fall back to the dialog's default location.
            }
        }

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickRunningExecutables(IReadOnlyList<RunningExecutable> candidates)
    {
        var viewModel = new ProcessPickerViewModel(candidates);
        var window = new ProcessPickerWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = viewModel,
        };

        return window.ShowDialog() == true ? viewModel.SelectedPaths : Array.Empty<string>();
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
}
