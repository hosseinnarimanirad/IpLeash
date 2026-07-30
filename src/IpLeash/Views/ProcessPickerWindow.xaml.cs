using System.Windows;

namespace IpLeash.Views;

/// <summary>
/// Intentionally free of logic. OK/Cancel are commands, and the window closes through the
/// <see cref="DialogCloser"/> attached property rather than a code-behind handler.
/// </summary>
public partial class ProcessPickerWindow : Window
{
    public ProcessPickerWindow() => InitializeComponent();
}
