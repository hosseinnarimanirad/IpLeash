using System.Windows;

namespace Leash.Views;

/// <summary>
/// Lets a dialog's ViewModel close its window by setting a bindable property, so OK/Cancel can
/// be commands and the window's code-behind stays empty.
/// </summary>
public static class DialogCloser
{
    public static readonly DependencyProperty DialogResultProperty =
        DependencyProperty.RegisterAttached(
            "DialogResult",
            typeof(bool?),
            typeof(DialogCloser),
            new PropertyMetadata(OnDialogResultChanged));

    public static void SetDialogResult(DependencyObject target, bool? value) =>
        target.SetValue(DialogResultProperty, value);

    public static bool? GetDialogResult(DependencyObject target) =>
        (bool?)target.GetValue(DialogResultProperty);

    private static void OnDialogResultChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && e.NewValue is bool result)
        {
            // Setting DialogResult on a window that was not shown modally throws.
            try
            {
                window.DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                window.Close();
            }
        }
    }
}
