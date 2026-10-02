using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Mambo.App.Shell;

public sealed partial class ToastHost : UserControl
{
    public ToastHost(ToastService toasts)
    {
        Toasts = toasts;
        InitializeComponent();
    }

    public ToastService Toasts { get; }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToastItem item) ToastService.Pause(item);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToastItem item) ToastService.Resume(item);
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToastItem item) Toasts.Invoke(item);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToastItem item) Toasts.Dismiss(item);
    }
}
