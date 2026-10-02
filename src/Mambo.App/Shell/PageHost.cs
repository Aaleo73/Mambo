using System.Numerics;
using Mambo.App.Themes;
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
    private FrameworkElement? current;

    public FrameworkElement? CurrentPage => current;

    public void Initialize(Func<Route, FrameworkElement> pageFactory) => factory = pageFactory;

    public void Show(NavigatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (factory is null) throw new InvalidOperationException("PageHost 尚未初始化。");
        if (current is INavigablePage leaving && args.From is not null) leaving.OnNavigatedFrom(args.From);
        var key = args.To.Route.Key;
        var index = pages.FindIndex(p => p.Key == key);
        var created = index < 0;
        FrameworkElement page;
        if (created)
        {
            page = factory(args.To.Route);
            Children.Add(page);
        }
        else
        {
            page = pages[index].Page;
            pages.RemoveAt(index);
        }
        pages.Add((key, page));
        foreach (var (_, other) in pages) other.Visibility = ReferenceEquals(other, page) ? Visibility.Visible : Visibility.Collapsed;
        current = page;
        Evict();
        (page as INavigablePage)?.OnNavigatedTo(args.To, args.Mode, created);
        PlayEnter(page);
    }

    public void RefreshCurrent() => (current as INavigablePage)?.Refresh();

    /// <summary>账号变化时丢弃全部页面，避免旧账号的观察继续存在。</summary>
    public void Clear()
    {
        foreach (var (_, page) in pages) (page as IDisposable)?.Dispose();
        pages.Clear();
        Children.Clear();
        current = null;
    }

    private void Evict()
    {
        while (pages.Count > Capacity)
        {
            var victim = pages.FindIndex(p => !ReferenceEquals(p.Page, current) && p.Key != Route.Home.Key);
            if (victim < 0) return;
            var page = pages[victim].Page;
            pages.RemoveAt(victim);
            Children.Remove(page);
            (page as IDisposable)?.Dispose();
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
