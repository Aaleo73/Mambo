using System.Numerics;
using Mambo.App.Themes;
using Mambo.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Shell;

public interface INavigablePage
{
    /// <summary>created 为 true 表示页面实例刚创建，需要从 entry 恢复视图状态。</summary>
    void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created);
    void OnNavigatedFrom(NavEntry entry);
    void Refresh();
}

/// <summary>
/// 按路由缓存最多 4 个活页面（首页常驻），不活跃的页面折叠隐藏，返回时滚动、图片和布局都还在。
/// 被淘汰的页面释放，再次进入时重建并从历史记录恢复滚动位置。
/// </summary>
public sealed partial class PageHost : Grid
{
    private const int Capacity = 4;
    private readonly List<(string Key, FrameworkElement Page)> pages = [];
    private Func<Route, FrameworkElement>? factory;
    private Navigator? navigator;
    private Action<Exception>? reportFailure;
    private FrameworkElement? current;
    private bool showingError;

    public FrameworkElement? CurrentPage => current;
    internal event Action<Exception>? DiagnosticFailure;

    public void Initialize(Func<Route, FrameworkElement> pageFactory, Navigator navigation, Action<Exception>? failureReporter = null)
    {
        ArgumentNullException.ThrowIfNull(pageFactory);
        ArgumentNullException.ThrowIfNull(navigation);
        factory = pageFactory;
        navigator = navigation;
        reportFailure = failureReporter;
    }

    public void Show(NavigatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (factory is null) throw new InvalidOperationException("PageHost 尚未初始化。");
        FrameworkElement? candidate = null;
        var leaving = true;
        try
        {
            // 兜底页从不缓存，重试也不复用任何已失败的页面实例。
            if (showingError) Clear();
            if (current is INavigablePage previous && args.From is not null) previous.OnNavigatedFrom(args.From);
            leaving = false;
            var key = args.To.Route.Key;
            var index = pages.FindIndex(p => p.Key == key);
            var created = index < 0;
            if (created)
            {
                candidate = factory(args.To.Route);
                Children.Add(candidate);
            }
            else
            {
                candidate = pages[index].Page;
                pages.RemoveAt(index);
            }
            pages.Add((key, candidate));
            foreach (var (_, other) in pages) other.Visibility = ReferenceEquals(other, candidate) ? Visibility.Visible : Visibility.Collapsed;
            current = candidate;
            (candidate as INavigablePage)?.OnNavigatedTo(args.To, args.Mode, created);
            Evict();
            PlayEnter(candidate);
        }
        catch (Exception error)
        {
            ReportFailure(error);
            if (candidate is not null && !pages.Any(page => ReferenceEquals(page.Page, candidate))) Release(candidate);
            if (leaving && args.From is not null) ResetViewState(args.From);
            ShowError(args.To);
        }
    }

    public void RefreshCurrent()
    {
        if (showingError) { navigator?.RetryCurrent(); return; }
        try { (current as INavigablePage)?.Refresh(); }
        catch (Exception error) { ReportFailure(error); if (navigator is not null) ShowError(navigator.Current); }
    }

    /// <summary>账号变化时丢弃全部页面，避免旧账号的观察继续存在。</summary>
    public void Clear()
    {
        var discarded = pages.Select(page => page.Page).ToList();
        if (current is not null && !discarded.Any(page => ReferenceEquals(page, current))) discarded.Add(current);
        pages.Clear();
        current = null;
        showingError = false;
        // 单个失败页的清理异常不能阻止其余页面取消查询与退订。
        foreach (var page in discarded) Release(page);
    }

    private void ShowError(NavEntry entry)
    {
        // 页面边界发生异常时，丢弃所有活页及观察，避免旧账号/旧作用域或坏缓存被重试复用。
        Clear();
        ResetViewState(entry);
        showingError = true;
        Action retry = () => navigator?.RetryCurrent();
        Action home = () =>
        {
            if (navigator is null) return;
            if (navigator.Current.Route.Equals(Route.Home)) navigator.RetryCurrent();
            else navigator.Navigate(Route.Home);
        };
        FrameworkElement recovery;
        try { recovery = new ErrorPage(retry, home); }
        catch (Exception error)
        {
            ReportFailure(error);
            // 自定义资源本身失败时，仍保留使用系统样式的恢复操作；不再次调用页面工厂。
            var homeButton = new Button { Content = "返回首页" };
            var retryButton = new Button { Content = "重试" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(homeButton, "返回首页");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(retryButton, "重试页面");
            homeButton.Click += (_, _) => home();
            retryButton.Click += (_, _) => retry();
            recovery = new UserControl
            {
                Content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "出了点问题", FontSize = 20 },
                        new TextBlock { Text = "页面暂时无法显示，请重试或返回首页。", TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
                        homeButton,
                        retryButton,
                    },
                },
            };
        }
        current = recovery;
        Children.Add(recovery);
    }

    private static void ResetViewState(NavEntry entry)
    {
        entry.ViewState = null;
        entry.VerticalOffset = 0;
    }

    private void Release(FrameworkElement page)
    {
        try { page.Visibility = Visibility.Collapsed; }
        catch (Exception error) { ReportFailure(error); }
        try { Children.Remove(page); }
        catch (Exception error) { ReportFailure(error); }
        try { (page as IDisposable)?.Dispose(); }
        catch (Exception error) { ReportFailure(error); }
    }

    private void ReportFailure(Exception error)
    {
        try { DiagnosticFailure?.Invoke(error); }
        catch (Exception) { }
        // 诊断接收方失败不能阻止错误页或其余作用域释放。
        try { reportFailure?.Invoke(error); }
        catch (Exception) { }
    }

    private void Evict()
    {
        while (pages.Count > Capacity)
        {
            var victim = pages.FindIndex(p => !ReferenceEquals(p.Page, current) && p.Key != Route.Home.Key);
            if (victim < 0) return;
            var page = pages[victim].Page;
            pages.RemoveAt(victim);
            Release(page);
        }
    }

    private static void PlayEnter(FrameworkElement page)
    {
        if (!Motion.AnimationsEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        var visual = ElementCompositionPreview.GetElementVisual(page);
        var compositor = visual.Compositor;
        var easing = Motion.CreateEasing(compositor, Motion.Settle);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.Duration = Motion.Route;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, new Vector3(0, 8, 0));
        offset.InsertKeyFrame(1, Vector3.Zero, easing);
        offset.Duration = Motion.Route;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", offset);
    }
}
