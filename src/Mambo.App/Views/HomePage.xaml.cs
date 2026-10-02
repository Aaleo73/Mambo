using System.ComponentModel;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

/// <summary>
/// 首页状态依次判断：恢复中空白 → 恢复失败 → 未登录引导 → 内容。
/// </summary>
public sealed partial class HomePage : UserControl, INavigablePage, IDisposable
{
    private readonly ShellViewModel shell;
    private readonly ISessionService session;
    private readonly Navigator navigator;

    public HomePage(ShellViewModel shell, ISessionService session, Navigator navigator)
    {
        this.shell = shell;
        this.session = session;
        this.navigator = navigator;
        InitializeComponent();
        shell.PropertyChanged += OnShellPropertyChanged;
        ApplyState();
    }

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (created) ScrollState.Restore(Scroller, entry.VerticalOffset);
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.VerticalOffset = Scroller.VerticalOffset;
    }

    public void Refresh()
    {
    }

    public void Dispose() => shell.PropertyChanged -= OnShellPropertyChanged;

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.State) or nameof(ShellViewModel.IsLoggedIn)) ApplyState();
    }

    private void ApplyState()
    {
        var state = shell.State;
        Onboarding.Visibility = state == SessionState.LoggedOut ? Visibility.Visible : Visibility.Collapsed;
        Unreachable.Visibility = state == SessionState.Unreachable ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = shell.IsLoggedIn && state != SessionState.Unreachable ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>引导标题字号随内容区宽度变化：12% 宽度，限制在 64–150 之间。</summary>
    private void OnOnboardingSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var size = Math.Clamp((e.NewSize.Width - 128) * 0.12, 64, 150);
        foreach (var line in new[] { OnboardingLine1, OnboardingLine2 })
        {
            line.FontSize = size;
            line.LineHeight = Math.Round(size * 1.04);
        }
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => navigator.Navigate(Route.Settings);

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        try { await session.RestoreAsync(); }
        catch (AppException) { }
        catch (OperationCanceledException) { }
    }
}
