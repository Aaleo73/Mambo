using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

/// <summary>页面边界的恢复界面；不接收异常、路由参数或账户信息。</summary>
public sealed partial class ErrorPage : UserControl, IDisposable
{
    private Action? retry;
    private Action? home;
    private bool dispatching;

    public ErrorPage(Action retry, Action home)
    {
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(home);
        this.retry = retry;
        this.home = home;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        retry = null;
        home = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RetryButton.Focus(FocusState.Programmatic);
    private void OnRetryClick(object sender, RoutedEventArgs e) => Dispatch(retry);
    private void OnHomeClick(object sender, RoutedEventArgs e) => Dispatch(home);

    private void Dispatch(Action? action)
    {
        if (dispatching || action is null) return;
        dispatching = true;
        try { action(); }
        finally { dispatching = false; }
    }
}
