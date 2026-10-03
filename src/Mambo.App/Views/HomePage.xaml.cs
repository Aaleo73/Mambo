using System.ComponentModel;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

/// <summary>
/// 首页状态依次判断：恢复中空白 → 恢复失败 → 未登录引导 → 全部加载中骨架屏 → 媒体库失败 → 内容。
/// </summary>
public sealed partial class HomePage : UserControl, INavigablePage, IDisposable
{
    private readonly ShellViewModel shell;
    private readonly ISessionService session;
    private readonly ILibraryService library;
    private readonly Navigator navigator;
    private readonly TitleBarService titleBar;
    private HomeViewModel? content;
    private bool active;
    private bool covered;
    private bool stateQueued;
    private bool disposed;
    private IDisposable? scrollRestore;

    public HomePage(ShellViewModel shell, ISessionService session, ILibraryService library, Navigator navigator,
        TitleBarService titleBar, WindowContext window)
    {
        ArgumentNullException.ThrowIfNull(window);
        this.shell = shell;
        this.session = session;
        this.library = library;
        this.navigator = navigator;
        this.titleBar = titleBar;
        InitializeComponent();
        try
        {
            Hero.Initialize(window);
            for (var i = 0; i < 8; i++)
                SkeletonCards.Children.Add(new Border { Style = XamlResources.Style(Application.Current.Resources, "SkeletonBlockStyle"), Width = 300, Height = 169 });
            shell.PropertyChanged += OnShellPropertyChanged;
            SizeChanged += (_, _) => UpdateHeroHeight();
            ApplyState();
        }
        catch
        {
            try { Dispose(); }
            catch (Exception) { }
            throw;
        }
    }

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        active = true;
        Hero.SetPageActive(!covered);
        UpdateDots();
        if (created) scrollRestore = ScrollState.Restore(Scroller, entry.VerticalOffset);
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        active = false;
        scrollRestore?.Dispose();
        scrollRestore = null;
        entry.VerticalOffset = Scroller.VerticalOffset;
        Hero.SetPageActive(false);
        titleBar.Hide(Hero.Dots);
    }

    public void Refresh() => _ = content?.RefreshAsync();
    internal bool HasFirstContent => content is { HasContent: true } && content.Rails.Any(rail => rail.Items.Count > 0);

    /// <summary>播放器覆盖首页时暂停轮播；返回后仍按导航活动状态决定是否恢复。</summary>
    public void SetCovered(bool value)
    {
        covered = value;
        Hero.SetPageActive(active && !covered);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shell.PropertyChanged -= OnShellPropertyChanged;
        try { scrollRestore?.Dispose(); }
        finally
        {
            try { titleBar.Hide(Hero.Dots); }
            finally
            {
                try { DisposeContent(); }
                finally { Hero.Dispose(); }
            }
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ShellViewModel.State) or nameof(ShellViewModel.IsLoggedIn)) || disposed || stateQueued) return;
        // 同一账号通知随后可能清掉此页；合并到下一次调度，避免旧页先创建又立即销毁内容。
        stateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            stateQueued = false;
            if (!disposed) ApplyState();
        })) stateQueued = false;
    }

    private void ApplyState()
    {
        var state = shell.State;
        var showContent = shell.IsLoggedIn && state != SessionState.Unreachable;
        Onboarding.Visibility = state == SessionState.LoggedOut ? Visibility.Visible : Visibility.Collapsed;
        Unreachable.Visibility = state == SessionState.Unreachable ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;
        if (showContent && content is null)
        {
            content = new HomeViewModel(library, navigator);
            content.PropertyChanged += OnContentPropertyChanged;
            content.SlidesChanged += OnSlidesChanged;
            RailList.ItemsSource = content.Rails;
            OnSlidesChanged(null, EventArgs.Empty);
        }
        else if (!shell.IsLoggedIn)
        {
            DisposeContent();
        }
        ApplyContentState();
        UpdateDots();
    }

    private void DisposeContent()
    {
        if (content is null) return;
        content.PropertyChanged -= OnContentPropertyChanged;
        content.SlidesChanged -= OnSlidesChanged;
        RailList.ItemsSource = null;
        content.Dispose();
        content = null;
        Hero.SetSlides([]);
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e) => ApplyContentState();

    private void ApplyContentState()
    {
        var loading = content?.IsLoading ?? false;
        var failed = content?.HasError ?? false;
        SkeletonPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadFailed.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        ContentPanel.Visibility = content?.HasContent == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSlidesChanged(object? sender, EventArgs e)
    {
        Hero.SetSlides(content?.Slides ?? []);
        Hero.Visibility = Hero.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RailList.Margin = new Thickness(0, Hero.Count > 0 ? -36 : 24, 0, 0);
        UpdateDots();
    }

    private void UpdateDots()
    {
        if (active && content is not null && Hero.Count >= 2 && Scroller.Visibility == Visibility.Visible) titleBar.Show(Hero.Dots);
        else titleBar.Hide(Hero.Dots);
    }

    /// <summary>hero 高度为窗口高度的 62%，限制在 500–600 之间。</summary>
    private void UpdateHeroHeight()
    {
        var windowHeight = XamlRoot?.Size.Height ?? ActualHeight;
        var height = Math.Clamp(Math.Round(windowHeight * (double)Application.Current.Resources["HeroHeightRatio"]),
            (double)Application.Current.Resources["HeroMinHeight"], (double)Application.Current.Resources["HeroMaxHeight"]);
        Hero.Height = height;
        HeroSkeleton.Height = height;
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (Hero.Height > 0) Hero.SetVisibleFraction(Math.Max(0, (Hero.Height - Scroller.VerticalOffset) / Hero.Height));
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
    private void OnReloadClick(object sender, RoutedEventArgs e) => content?.Retry();

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        try { await session.RestoreAsync(); }
        catch (AppException) { }
        catch (OperationCanceledException) { }
    }
}
