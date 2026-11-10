using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using IpLeash.Models;
using IpLeash.ViewModels;

namespace IpLeash.Views.Services;

/// <inheritdoc cref="IDialogService"/>
/// <remarks>
/// <see cref="IDialogService"/> is synchronous because it is shared with the WPF build, where
/// modal dialogs block. Avalonia's are asynchronous, so each one here runs a nested dispatcher
/// frame until the dialog closes: the caller blocks, the UI keeps pumping, nothing deadlocks.
/// </remarks>
public sealed class DialogService : IDialogService
{
    public string? BrowseForExecutable(string? initialPath)
    {
        if (MainWindow is not { } owner)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = "Select the application to monitor",
            AllowMultiple = false,
        };

        // An .app bundle is a file in the open panel unless it is told otherwise, so a single
        // file picker covers both bundles and command-line executables.
        var picked = RunToCompletion(owner.StorageProvider.OpenFilePickerAsync(options));
        return picked is { Count: > 0 } ? picked[0].TryGetLocalPath() : null;
    }

    public IReadOnlyList<string> PickRunningExecutables(IReadOnlyList<RunningExecutable> candidates)
    {
        var viewModel = new ProcessPickerViewModel(candidates);
        var window = new ProcessPickerWindow { DataContext = viewModel };

        return ShowModal<bool>(window) ? viewModel.SelectedPaths : Array.Empty<string>();
    }

    public bool Confirm(string title, string message) =>
        ShowModal<bool>(BuildMessageWindow(title, message, isQuestion: true, isError: false));

    public void ShowError(string title, string message) =>
        ShowModal<bool>(BuildMessageWindow(title, message, isQuestion: false, isError: true));

    public void ShowInfo(string title, string message) =>
        ShowModal<bool>(BuildMessageWindow(title, message, isQuestion: false, isError: false));

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>
    /// Shows <paramref name="dialog"/> modally over the main window, or on its own when that
    /// window is hidden in the menu bar (Exit is reachable from there), and blocks until it closes.
    /// </summary>
    private static TResult? ShowModal<TResult>(Window dialog)
    {
        var owner = MainWindow;

        if (owner is { IsVisible: true })
        {
            return RunToCompletion(dialog.ShowDialog<TResult>(owner));
        }

        var closed = new TaskCompletionSource<TResult?>();
        dialog.Closed += (_, _) => closed.TrySetResult(default);
        dialog.Closing += (_, e) =>
        {
            // Capture the result Close(result) was called with, before the default overwrites it.
            if (dialog.Tag is TResult result)
            {
                closed.TrySetResult(result);
            }
        };

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Topmost = true;
        dialog.Show();
        dialog.Activate();

        return RunToCompletion(closed.Task);
    }

    /// <summary>Pumps the UI thread until <paramref name="task"/> finishes, then returns its result.</summary>
    private static T RunToCompletion<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.UIThread.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }

    private static Window BuildMessageWindow(string title, string message, bool isQuestion, bool isError)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#F4F5F7"),
        };

        void CloseWith(bool result)
        {
            window.Tag = result;
            window.Close(result);
        }

        var ok = new Button
        {
            Content = isQuestion ? "Continue" : "OK",
            IsDefault = true,
            Margin = new Thickness(0),
        };
        ok.Classes.Add("primary");
        ok.Click += (_, _) => CloseWith(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        if (isQuestion)
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Classes.Add("secondary");
            cancel.Click += (_, _) => CloseWith(false);
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(ok);

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse(isError ? "#B42318" : "#111827"),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var body = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Brush.Parse("#374151"),
        };

        window.Content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 18),
            Children = { heading, body, buttons },
        };

        return window;
    }
}
