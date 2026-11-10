using System.ComponentModel;
using Avalonia.Controls;
using IpLeash.ViewModels;

namespace IpLeash.Views;

/// <summary>
/// OK and Cancel are ViewModel commands that set <see cref="ProcessPickerViewModel.DialogResult"/>;
/// this closes the window with that result. The Avalonia stand-in for the WPF DialogCloser.
/// </summary>
public partial class ProcessPickerWindow : Window
{
    private ProcessPickerViewModel? _viewModel;

    public ProcessPickerWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ProcessPickerViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessPickerViewModel.DialogResult) && _viewModel?.DialogResult is { } result)
        {
            Close(result);
        }
    }
}
