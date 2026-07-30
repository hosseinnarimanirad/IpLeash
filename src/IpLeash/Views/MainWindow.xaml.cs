using System.Windows;

namespace IpLeash.Views;

/// <summary>
/// Intentionally free of logic. Everything the window does is expressed as bindings and
/// commands against <see cref="ViewModels.MainViewModel"/>, which App wires up as the DataContext.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
