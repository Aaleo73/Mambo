using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Mambo.App.Views;
using Mambo.App.Views.Controls;
using Mambo.Core;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mambo.App.Debug;

/// <summary>从进程创建时刻量起；不记录服务器或用户数据。</summary>
internal static class StartupTimeline
{
    public static long WindowLoadedMilliseconds { get; private set; }
    public static long HomeContentMilliseconds { get; private set; }
    public static int RestoredSnapshotsAtHomeContent { get; private set; }
    public static bool HomeIsLoadedAtFirstContent { get; private set; }
    public static int RealizedHomeCardsAtFirstContent { get; private set; }
    public static int VisibleHomeCardsAtFirstContent { get; private set; }
    public static int RestoredHomeCardsVisibleAtFirstContent { get; private set; }
    private static StartupHomeGeometry? homeGeometryAtFirstContent;
    internal static Dictionary<string, long> Phases { get; } = [];
    internal static void Mark(string phase) => Phases[phase] = Program.UptimeMilliseconds;

    public static async Task ObserveAsync(MainWindow window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadedRecorded = false;

        void OnLoaded(object sender, RoutedEventArgs args) => RecordLoaded();
        void RecordLoaded()
        {
            if (loadedRecorded || timeout.IsCancellationRequested) return;
            loadedRecorded = true;
            // 在真实 Loaded 回调中量时，不包含稍后排队的其他 UI 工作。
            WindowLoadedMilliseconds = Program.UptimeMilliseconds;
            Mark("WindowLoaded");
            loaded.TrySetResult();
        }
        void OnClosed(object sender, WindowEventArgs args) => timeout.Cancel();
        void OnRendering(object? sender, object args)
        {
            if (timeout.IsCancellationRequested || rendered.Task.IsCompleted ||
                window.Shell.PageHost.CurrentPage is not HomePage { HasFirstContent: true } home) return;
            try
            {
                var renderedAtMilliseconds = Program.UptimeMilliseconds;
                var cache = window.Services.GetService<BackendRuntime>()?.QueryCache;
                RestoredSnapshotsAtHomeContent = cache?.RestoredSnapshotCount ?? 0;
                HomeIsLoadedAtFirstContent = home.IsLoaded;
                RealizedHomeCardsAtFirstContent = CountRealizedHomeCards(home);
                var visible = VisibleHomeItems(home).ToArray();
                VisibleHomeCardsAtFirstContent = visible.Length;
                RestoredHomeCardsVisibleAtFirstContent = cache?.CountRestoredHomeItems(visible) ?? 0;
                // 常规启动不额外遍历或采集布局；诊断只包含固定状态与数字。
                if (Program.Arguments.Contains("--startup-smoke", StringComparer.Ordinal))
                    homeGeometryAtFirstContent = CaptureHomeGeometry(home);
                Mark("HomeContentRendered");
                // 完整采集成功后再公布时间，报告不能读取到半完成的缓存来源判定。
                HomeContentMilliseconds = renderedAtMilliseconds;
                rendered.TrySetResult();
            }
            catch (Exception error)
            {
                rendered.TrySetException(error);
            }
        }

        try
        {
            window.Shell.Loaded += OnLoaded;
            window.Closed += OnClosed;
            if (window.Shell.IsLoaded) RecordLoaded();
            await loaded.Task.WaitAsync(timeout.Token);
            while (window.Shell.PageHost.CurrentPage is not HomePage { HasFirstContent: true })
                await Task.Delay(10, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            CompositionTarget.Rendering += OnRendering;
            await rendered.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally
        {
            window.Shell.Loaded -= OnLoaded;
            window.Closed -= OnClosed;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    internal static async Task RunProbeAsync(MainWindow window, string reportPath)
    {
        var report = new StartupReport();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(32));
            while (HomeContentMilliseconds == 0)
            {
                var session = window.Services.GetRequiredService<ISessionService>();
                if (WindowLoadedMilliseconds > 0 && session.State == SessionState.LoggedOut)
                {
                    report.Status = "Unsupported";
                    report.Reason = "NoRestorableSession";
                    break;
                }
                await Task.Delay(10, timeout.Token);
            }
            if (HomeContentMilliseconds > 0)
            {
                report.Status = "Measured";
                report.WindowLoadedMilliseconds = WindowLoadedMilliseconds;
                report.HomeContentMilliseconds = HomeContentMilliseconds;
                report.RestoredSnapshotsAtHomeContent = RestoredSnapshotsAtHomeContent;
                report.HomeIsLoadedAtFirstContent = HomeIsLoadedAtFirstContent;
                report.RealizedHomeCardsAtFirstContent = RealizedHomeCardsAtFirstContent;
                report.VisibleHomeCardsAtFirstContent = VisibleHomeCardsAtFirstContent;
                report.RestoredHomeCardsVisibleAtFirstContent = RestoredHomeCardsVisibleAtFirstContent;
                report.HomeGeometryAtFirstContent = homeGeometryAtFirstContent;
                report.WindowTargetMet = WindowLoadedMilliseconds <= 600;
                report.CachedHomeTargetMet = RestoredHomeCardsVisibleAtFirstContent > 0 && HomeContentMilliseconds <= 800;
            }
        }
        catch (OperationCanceledException) { report.Status = "Unsupported"; report.Reason = "HomeContentTimedOut"; }
        catch (Exception error) { report.Status = "Failed"; report.Reason = error.GetType().Name; }
        finally
        {
            report.WindowLoadedMilliseconds = WindowLoadedMilliseconds;
            report.HomeContentMilliseconds = HomeContentMilliseconds;
            report.Phases = new(Phases);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, StartupJsonContext.Default.StartupReport));
            await window.CloseForSmokeAsync();
            window.Close();
        }
    }

    // 只传对象引用到缓存诊断，不读取或写出媒体名称、ID、图片或账号。
    private static int CountRealizedHomeCards(DependencyObject node)
    {
        if (node is CardBase) return 1;
        var count = 0;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            count += CountRealizedHomeCards(VisualTreeHelper.GetChild(node, index));
        return count;
    }

    private static StartupHomeGeometry CaptureHomeGeometry(HomePage page)
    {
        var geometry = new StartupHomeGeometry
        {
            HomeWidth = Finite(page.ActualWidth),
            HomeHeight = Finite(page.ActualHeight),
        };
        try
        {
            var viewport = new Windows.Foundation.Rect(0, 0, page.ActualWidth, page.ActualHeight);
            Visit(page, viewport, "None", 0, 0, 0, 0, 0, 0, 0);
            geometry.Status = "Captured";
        }
        catch (Exception)
        {
            geometry.Status = "GeometryUnavailable";
        }
        return geometry;

        void Visit(DependencyObject node, Windows.Foundation.Rect clip, string firstExclusion, int firstExclusionDepth,
            int depth, int collapsedAncestors, int transparentAncestors, int unloadedAncestors,
            int scrollViewerAncestors, int scrollViewAncestors)
        {
            // 与可见性扫描不同，这里穿过被排除的祖先，只定位最多八张已生成卡片为何被排除。
            if (geometry.Cards.Count >= 8) return;
            if (node is UIElement ui)
            {
                if (ui.Visibility == Visibility.Collapsed)
                {
                    collapsedAncestors++;
                    Exclude("Collapsed");
                }
                if (ui.Opacity <= 0)
                {
                    transparentAncestors++;
                    Exclude("OpacityZero");
                }
            }
            if (node is FrameworkElement element)
            {
                if (!element.IsLoaded)
                {
                    unloadedAncestors++;
                    Exclude("NotLoaded");
                }
                var isScrollViewer = element is Microsoft.UI.Xaml.Controls.ScrollViewer;
                var isScrollView = element is Microsoft.UI.Xaml.Controls.ScrollView;
                if (isScrollViewer)
                {
                    scrollViewerAncestors++;
                    if (!geometry.HasOuterScrollViewer)
                    {
                        var scroller = (Microsoft.UI.Xaml.Controls.ScrollViewer)element;
                        geometry.HasOuterScrollViewer = true;
                        geometry.ScrollViewerWidth = Finite(scroller.ActualWidth);
                        geometry.ScrollViewerHeight = Finite(scroller.ActualHeight);
                        geometry.ViewportWidth = Finite(scroller.ViewportWidth);
                        geometry.ViewportHeight = Finite(scroller.ViewportHeight);
                        geometry.HorizontalOffset = Finite(scroller.HorizontalOffset);
                        geometry.VerticalOffset = Finite(scroller.VerticalOffset);
                    }
                }
                if (isScrollView) scrollViewAncestors++;
                Windows.Foundation.Rect bounds = default;
                var boundsAvailable = false;
                if (isScrollViewer || element is CardBase)
                {
                    try
                    {
                        bounds = element.TransformToVisual(page).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                        boundsAvailable = true;
                        clip.Intersect(bounds);
                        if (clip.IsEmpty || clip.Width <= 0 || clip.Height <= 0)
                            Exclude(isScrollViewer ? "EmptyScrollViewerClip" : "EmptyCardClip");
                    }
                    catch (Exception)
                    {
                        Exclude("TransformUnavailable");
                    }
                }
                if (element is CardBase card)
                {
                    var hasItem = card.Item?.Item is not null;
                    if (!hasItem) Exclude("MissingItem");
                    geometry.Cards.Add(new StartupCardGeometry
                    {
                        IsLoaded = card.IsLoaded,
                        HasItem = hasItem,
                        Width = Finite(card.ActualWidth),
                        Height = Finite(card.ActualHeight),
                        BoundsAvailable = boundsAvailable,
                        X = boundsAvailable ? Finite(bounds.X) : 0,
                        Y = boundsAvailable ? Finite(bounds.Y) : 0,
                        ClipX = Finite(clip.X),
                        ClipY = Finite(clip.Y),
                        ClipWidth = clip.IsEmpty ? 0 : Math.Max(0, Finite(clip.Width)),
                        ClipHeight = clip.IsEmpty ? 0 : Math.Max(0, Finite(clip.Height)),
                        FirstExclusion = firstExclusion,
                        FirstExclusionDepth = firstExclusionDepth,
                        Depth = depth,
                        CollapsedAncestors = collapsedAncestors,
                        TransparentAncestors = transparentAncestors,
                        UnloadedAncestors = unloadedAncestors,
                        ScrollViewerAncestors = scrollViewerAncestors,
                        ScrollViewAncestors = scrollViewAncestors,
                    });
                    return;
                }
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node) && geometry.Cards.Count < 8; index++)
                Visit(VisualTreeHelper.GetChild(node, index), clip, firstExclusion, firstExclusionDepth, depth + 1,
                    collapsedAncestors, transparentAncestors, unloadedAncestors, scrollViewerAncestors, scrollViewAncestors);

            void Exclude(string reason)
            {
                if (firstExclusion != "None") return;
                firstExclusion = reason;
                firstExclusionDepth = depth;
            }
        }
    }

    private static double Finite(double number) => double.IsFinite(number) ? number : 0;

    // 排除虚拟化预取区：必须与首页及每层 ScrollViewer 的实际视口相交。
    private static IEnumerable<MediaItem> VisibleHomeItems(HomePage page)
    {
        var viewport = new Windows.Foundation.Rect(0, 0, page.ActualWidth, page.ActualHeight);
        return Visit(page, viewport);

        IEnumerable<MediaItem> Visit(DependencyObject node, Windows.Foundation.Rect clip)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: <= 0 }) yield break;
            if (node is FrameworkElement element)
            {
                if (!element.IsLoaded) yield break;
                if (element is Microsoft.UI.Xaml.Controls.ScrollViewer or CardBase)
                {
                    var bounds = element.TransformToVisual(page).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    clip.Intersect(bounds);
                    if (clip.IsEmpty || clip.Width <= 0 || clip.Height <= 0) yield break;
                }
                if (element is CardBase { Item.Item: { } item })
                {
                    yield return item;
                    yield break;
                }
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                foreach (var item in Visit(VisualTreeHelper.GetChild(node, index), clip)) yield return item;
        }
    }
}

internal sealed class StartupReport
{
    public string Status { get; set; } = "NotRun";
    public string Reason { get; set; } = "";
    public string Scope { get; set; } = "Fresh process, current account, persisted query cache; OS file cache is not flushed; no identity or content is reported";
    public long WindowLoadedMilliseconds { get; set; }
    public long HomeContentMilliseconds { get; set; }
    public int RestoredSnapshotsAtHomeContent { get; set; }
    public bool HomeIsLoadedAtFirstContent { get; set; }
    public int RealizedHomeCardsAtFirstContent { get; set; }
    public int VisibleHomeCardsAtFirstContent { get; set; }
    public int RestoredHomeCardsVisibleAtFirstContent { get; set; }
    public StartupHomeGeometry? HomeGeometryAtFirstContent { get; set; }
    public bool WindowTargetMet { get; set; }
    public bool CachedHomeTargetMet { get; set; }
    public Dictionary<string, long> Phases { get; set; } = [];
}

internal sealed class StartupHomeGeometry
{
    public string Status { get; set; } = "NotCaptured";
    public double HomeWidth { get; set; }
    public double HomeHeight { get; set; }
    public bool HasOuterScrollViewer { get; set; }
    public double ScrollViewerWidth { get; set; }
    public double ScrollViewerHeight { get; set; }
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }
    public double HorizontalOffset { get; set; }
    public double VerticalOffset { get; set; }
    public List<StartupCardGeometry> Cards { get; set; } = [];
}

internal sealed class StartupCardGeometry
{
    public bool IsLoaded { get; set; }
    public bool HasItem { get; set; }
    public bool BoundsAvailable { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double ClipX { get; set; }
    public double ClipY { get; set; }
    public double ClipWidth { get; set; }
    public double ClipHeight { get; set; }
    public string FirstExclusion { get; set; } = "None";
    public int FirstExclusionDepth { get; set; }
    public int Depth { get; set; }
    public int CollapsedAncestors { get; set; }
    public int TransparentAncestors { get; set; }
    public int UnloadedAncestors { get; set; }
    public int ScrollViewerAncestors { get; set; }
    public int ScrollViewAncestors { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(StartupReport))]
internal sealed partial class StartupJsonContext : JsonSerializerContext;
