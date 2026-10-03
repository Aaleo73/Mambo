using System.Diagnostics;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Debug;

/// <summary>
/// 在真实假数据页面上验证淘汰后的滚动恢复。恢复阶段只观察 ScrollViewer，
/// 不替页面补页或调用 ChangeView；报告不保存路由参数、搜索文本或媒体元数据。
/// </summary>
internal static class NavigationSmoke
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(10);
    private const double OffsetTolerance = 2;

    public static async Task<NavigationReport> RunAsync(MainWindow window, CancellationToken token, Func<NavigationReport, Task>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        var report = new NavigationReport();
        var watch = Stopwatch.StartNew();
        if (!window.DispatcherQueue.HasThreadAccess)
            return new() { Status = "Unsupported", Reason = "UiThreadRequired" };
        // 此检查先于读取任何当前页面、导航记录或业务数据。
        if (window.Services.GetRequiredService<ILibraryService>() is not FakeLibraryService ||
            window.Services.GetRequiredService<IPlaybackService>() is not FakePlaybackService ||
            window.Services.GetRequiredService<ISessionService>() is not FakeSessionService ||
            window.Services.GetRequiredService<ISettingsService>() is not FakeSettingsService ||
            window.Services.GetRequiredService<ILibraryPreferences>() is not FakeLibraryPreferences ||
            window.Services.GetRequiredService<IImageService>() is not FakeImageService)
            return new() { Status = "Unsupported", Reason = "RealBackendRejected" };

        var navigation = window.Services.GetRequiredService<Navigator>();
        var playback = window.Services.GetRequiredService<IPlaybackService>();
        if (!window.Shell.IsLoaded || window.Shell.XamlRoot is null || playback.Current is not null || playback.IsStarting || navigation.ForwardBlocked)
            return new() { Status = "Unsupported", Reason = "BrowsingWindowRequired" };
        var originalRoute = navigation.Current.Route;
        var debugSettings = Application.Current.DebugSettings;
        var previousTracing = debugSettings.LayoutCycleTracingLevel;
        // 只在以上全部假服务、当前窗口和 UI 线程守卫通过后开启；不触发调试断点。
        debugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High;
        report.LayoutCycleTracingEnabled = true;
        async Task CheckpointAsync(string stage, ScrollViewer? scroller = null, int loadedItems = 0, int stepIndex = 0)
        {
            report.Stage = stage;
            report.ActivePageKind = (int)navigation.Current.Route.Kind;
            report.StepIndex = stepIndex;
            report.LoadedItems = loadedItems;
            report.CurrentOffset = scroller?.VerticalOffset;
            report.CurrentScrollableHeight = scroller?.ScrollableHeight;
            report.CurrentViewportHeight = scroller?.ViewportHeight;
            report.DurationMilliseconds = watch.Elapsed.TotalMilliseconds;
            if (checkpoint is not null) await checkpoint(report);
        }
        NavigatedEventArgs? lastNavigation = null;
        void OnNavigated(object? sender, NavigatedEventArgs args)
        {
            lastNavigation = args;
            report.NavigationEvents++;
        }
        navigation.Navigated += OnNavigated;
        try
        {
            await CheckpointAsync("Started");
            await ScenarioAsync(report, "LibraryBack", async scenario =>
            {
                // 先淘汰可能已被性能探针加载了全部元数据的同路由旧页。
                await VisitAsync(window, navigation, Fillers(), token,
                    index => CheckpointAsync("LibraryInitialEviction", stepIndex: index));
                await CheckpointAsync("LibraryNavigate");
                var page = await NavigateAsync<LibraryPage>(window, navigation, Route.Library(DemoCatalog.MoviesLibraryId), token);
                var entry = navigation.Current;
                var scroller = Scroller(page);
                await PrepareGridAsync(page.ViewModel.Cards, scroller, scenario,
                    (stage, count) => CheckpointAsync(stage, scroller, count), token);
                await VisitAsync(window, navigation, Fillers(), token,
                    index => CheckpointAsync("LibrarySavedPageEviction", stepIndex: index));
                await AssertEvictedAsync(window, page, report, scenario, token);
                await TraverseAsync(window, navigation, forward: false, 5, token,
                    index => CheckpointAsync("LibraryBackTraversal", stepIndex: index));
                Mark(report, "LibraryBackEntryIdentity", ReferenceEquals(navigation.Current, entry));
                var restored = await CurrentAsync<LibraryPage>(window, token);
                Mark(report, "LibraryBackNewPage", !ReferenceEquals(page, restored));
                await CheckpointAsync("LibraryRestoreObserve", Scroller(restored), restored.ViewModel.Cards.Items.Count);
                await ObserveRestoreAsync(Scroller(restored), () => Ready(restored.ViewModel.Cards), scenario, token);
                Mark(report, "LibraryBackOffsetWithinTolerance", scenario.OffsetDifference <= OffsetTolerance);
                await CheckpointAsync("LibraryCompleted", Scroller(restored), restored.ViewModel.Cards.Items.Count);
            }, token);

            await ScenarioAsync(report, "RecentForward", async scenario =>
            {
                // 5 个前置真实页面，让后退途中最近播放页被淘汰，再前进回来。
                await VisitAsync(window, navigation,
                    [Route.Home, Route.Library(DemoCatalog.ShowsLibraryId), Route.Detail("demo-movie-0001"),
                        Route.Detail("demo-movie-0002"), Route.Settings], token,
                    index => CheckpointAsync("RecentInitialHistory", stepIndex: index));
                await CheckpointAsync("RecentNavigate");
                var page = await NavigateAsync<RecentPage>(window, navigation, Route.Recent, token);
                var entry = navigation.Current;
                var scroller = Scroller(page);
                await PrepareGridAsync(page.ViewModel.Cards, scroller, scenario,
                    (stage, count) => CheckpointAsync(stage, scroller, count), token);
                await TraverseAsync(window, navigation, forward: false, 5, token,
                    index => CheckpointAsync("RecentBackEviction", stepIndex: index));
                await AssertEvictedAsync(window, page, report, scenario, token);
                await TraverseAsync(window, navigation, forward: true, 5, token,
                    index => CheckpointAsync("RecentForwardTraversal", stepIndex: index));
                Mark(report, "RecentForwardEntryIdentity", ReferenceEquals(navigation.Current, entry));
                var restored = await CurrentAsync<RecentPage>(window, token);
                Mark(report, "RecentForwardNewPage", !ReferenceEquals(page, restored));
                await CheckpointAsync("RecentRestoreObserve", Scroller(restored), restored.ViewModel.Cards.Items.Count);
                await ObserveRestoreAsync(Scroller(restored), () => Ready(restored.ViewModel.Cards), scenario, token);
                Mark(report, "RecentForwardOffsetWithinTolerance", scenario.OffsetDifference <= OffsetTolerance);
                await CheckpointAsync("RecentCompleted", Scroller(restored), restored.ViewModel.Cards.Items.Count);
            }, token);

            await ScenarioAsync(report, "SearchReplaceBackForward", async scenario =>
            {
                await CheckpointAsync("SearchHomeAnchor");
                await NavigateAsync<HomePage>(window, navigation, Route.Home, token);
                var anchor = navigation.Current;
                await CheckpointAsync("SearchInitialNavigate");
                var oldSearch = await NavigateAsync<SearchPage>(window, navigation, Route.Search("电影"), token);
                await WaitAsync(() => SearchReady(oldSearch), "SearchInitialData", token);
                var replacedEntry = navigation.Current;
                var eventsBefore = report.NavigationEvents;
                await CheckpointAsync("SearchReplaceNavigate");
                var page = await NavigateAsync<SearchPage>(window, navigation, Route.Search("电影 0"), token);
                var entry = navigation.Current;
                Mark(report, "SearchUsesOneReplaceEvent", report.NavigationEvents == eventsBefore + 1 &&
                    lastNavigation?.Mode == NavigationMode.Replace && ReferenceEquals(lastNavigation.From, replacedEntry) &&
                    ReferenceEquals(lastNavigation.To, entry));
                Mark(report, "SearchReplaceCreatesNewEntry", !ReferenceEquals(entry, replacedEntry));
                await WaitAsync(() => SearchReady(page), "SearchReplacementData", token);
                Mark(report, "SearchReplacementStartsAtTop", Scroller(page).VerticalOffset <= OffsetTolerance);
                await PrepareSearchAsync(page, scenario,
                    (stage, count) => CheckpointAsync(stage, Scroller(page), count), token);
                var savedCounts = page.ViewModel.Groups.ToDictionary(group => group.LibraryId, group => group.Cards.Items.Count, StringComparer.Ordinal);
                await VisitAsync(window, navigation, Fillers(), token,
                    index => CheckpointAsync("SearchSavedPageEviction", stepIndex: index));
                await AssertEvictedAsync(window, page, report, scenario, token);
                await TraverseAsync(window, navigation, forward: false, 5, token,
                    index => CheckpointAsync("SearchBackTraversal", stepIndex: index));
                Mark(report, "SearchBackReplacedEntryIdentity", ReferenceEquals(navigation.Current, entry));
                var restored = await CurrentAsync<SearchPage>(window, token);
                Mark(report, "SearchBackNewPage", !ReferenceEquals(page, restored));
                bool CountsRestored() => SearchReady(restored) && savedCounts.All(pair =>
                    restored.ViewModel.Groups.Any(group => group.LibraryId == pair.Key && group.Cards.Items.Count >= pair.Value));
                await CheckpointAsync("SearchRestoreObserve", Scroller(restored), restored.ViewModel.Groups.Sum(group => group.Cards.Items.Count));
                await ObserveRestoreAsync(Scroller(restored), CountsRestored, scenario, token);
                Mark(report, "SearchBackOffsetWithinTolerance", scenario.OffsetDifference <= OffsetTolerance);
                Mark(report, "SearchGroupPagesRestored", CountsRestored());
                await CheckpointAsync("SearchBackToAnchor");
                Mark(report, "SearchReplaceBackSkipsOldQuery", navigation.GoBack() && ReferenceEquals(navigation.Current, anchor));
                await CurrentAsync<HomePage>(window, token);
                await CheckpointAsync("SearchForwardToReplacement");
                Mark(report, "SearchReplaceForwardUsesNewEntry", navigation.GoForward() && ReferenceEquals(navigation.Current, entry));
                var forwardPage = await CurrentAsync<SearchPage>(window, token);
                var forwardScroller = Scroller(forwardPage);
                await WaitAsync(() => SearchReady(forwardPage) &&
                    Math.Abs(forwardScroller.VerticalOffset - scenario.ExpectedOffset) <= OffsetTolerance,
                    "SearchForwardOffset", token, stable: true);
                scenario.ForwardOffset = forwardScroller.VerticalOffset;
                Mark(report, "SearchForwardOffsetWithinTolerance", Math.Abs(scenario.ForwardOffset.Value - scenario.ExpectedOffset) <= OffsetTolerance);
                await CheckpointAsync("SearchCompleted", forwardScroller, forwardPage.ViewModel.Groups.Sum(group => group.Cards.Items.Count));
            }, token);
            await CheckpointAsync("RestoreStartingRoute");
        }
        catch (OperationCanceledException)
        {
            report.Status = "Failed";
            report.Reason = "Cancelled";
        }
        catch (Exception error)
        {
            report.Status = "Failed";
            report.Reason = "UnexpectedProbeFailure";
            report.ErrorKind = error.GetType().Name;
        }
        finally
        {
            debugSettings.LayoutCycleTracingLevel = previousTracing;
            navigation.Navigated -= OnNavigated;
            // 将后续播放烟测留在原来的假页面；不尝试重建探针之前的整段历史。
            if (window.Shell.IsLoaded && playback.Current is null && !navigation.ForwardBlocked)
            {
                try
                {
                    navigation.Navigate(originalRoute);
                    await Task.Yield();
                    report.ReturnedToStartingRoute = navigation.Current.Route.Equals(originalRoute);
                }
                catch (Exception error)
                {
                    report.Status = "Failed";
                    report.Reason = "RestoreStartingRouteFailed";
                    report.ErrorKind = error.GetType().Name;
                }
            }
            report.DurationMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
        if (report.Status == "NotRun")
        {
            report.Status = report.Scenarios.Any(scenario => scenario.Status == "Failed") || report.Checks.Any(check => !check.Passed)
                ? "Failed" : report.Scenarios.Any(scenario => scenario.Status != "Passed") ? "Unsupported" : "Passed";
            if (report.Status != "Passed") report.Reason = "ScenarioChecksIncomplete";
            if (report.Scenarios.Count != 3 || !report.ReturnedToStartingRoute)
            {
                report.Status = "Failed";
                report.Reason = "IncompleteProbe";
            }
        }
        report.Passed = report.Status == "Passed";
        report.Stage = report.Status == "Passed" ? "Completed" : report.Status;
        return report;
    }

    private static Route[] Fillers() =>
        [Route.Settings, Route.Library(DemoCatalog.ShowsLibraryId), Route.Detail("demo-movie-0001"), Route.Detail("demo-movie-0002"), Route.Home];

    private static async Task ScenarioAsync(NavigationReport report, string name, Func<NavigationScrollScenario, Task> action, CancellationToken token)
    {
        var scenario = new NavigationScrollScenario { Name = name };
        report.Scenarios.Add(scenario);
        try { await action(scenario); scenario.Status = "Passed"; }
        catch (ProbeFailure error) { scenario.Status = error.Status; scenario.Reason = error.Reason; }
        catch (OperationCanceledException) { scenario.Status = "Failed"; scenario.Reason = "Cancelled"; throw; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            scenario.Status = "Failed";
            scenario.Reason = "UnexpectedScenarioFailure";
            scenario.ErrorKind = error.GetType().Name;
            scenario.ErrorHResult = error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            scenario.FailureStage = report.Stage;
        }
        token.ThrowIfCancellationRequested();
    }

    private static async Task VisitAsync(MainWindow window, Navigator navigation, Route[] routes, CancellationToken token,
        Func<int, Task>? checkpoint = null)
    {
        for (var index = 0; index < routes.Length; index++)
        {
            if (checkpoint is not null) await checkpoint(index);
            var route = routes[index];
            navigation.Navigate(route);
            await WaitAsync(() => PageMatches(window.Shell.PageHost.CurrentPage, route.Kind), "VisitPageLayout", token);
        }
    }

    private static async Task<T> NavigateAsync<T>(MainWindow window, Navigator navigation, Route route, CancellationToken token) where T : FrameworkElement
    {
        navigation.Navigate(route);
        return await CurrentAsync<T>(window, token);
    }

    private static async Task<T> CurrentAsync<T>(MainWindow window, CancellationToken token) where T : FrameworkElement
    {
        await WaitAsync(() => window.Shell.PageHost.CurrentPage is T page && Visible(page), "CurrentPageLayout", token);
        return (T)window.Shell.PageHost.CurrentPage!;
    }

    private static bool Visible(FrameworkElement page) =>
        page.IsLoaded && page.XamlRoot is not null && page.Visibility == Visibility.Visible && page.ActualWidth > 0 && page.ActualHeight > 0;

    private static bool PageMatches(FrameworkElement? page, PageKind kind) => page is not null && Visible(page) && (kind switch
    {
        PageKind.Home => page is HomePage,
        PageKind.Settings => page is SettingsPage,
        PageKind.Library => page is LibraryPage,
        PageKind.Detail => page is DetailPage,
        PageKind.Recent => page is RecentPage,
        PageKind.Search => page is SearchPage,
        _ => false,
    });

    private static ScrollViewer Scroller(FrameworkElement page) => page.FindName("Scroller") as ScrollViewer
        ?? throw new ProbeFailure("ScrollerNotFound", "Unsupported");

    private static bool Ready(PagedCards cards) => cards.IsInitialized && !cards.IsLoadingFirst && !cards.IsLoadingMore && !cards.HasError && !cards.HasMoreError;
    private static bool SearchReady(SearchPage page) => !page.ViewModel.IsLoading && !page.ViewModel.HasError &&
        page.ViewModel.Groups.Count > 0 && page.ViewModel.Groups.All(group => Ready(group.Cards));

    private static async Task PrepareGridAsync(PagedCards cards, ScrollViewer scroller, NavigationScrollScenario scenario,
        Func<string, int, Task> checkpoint, CancellationToken token)
    {
        await checkpoint(scenario.Name + "FixtureInitialLayout", cards.Items.Count);
        await WaitAsync(() => Ready(cards), "GridInitialData", token);
        await LayoutAsync(scroller, token);
        scenario.InitialLoadedItems = cards.Items.Count;
        scenario.InitialScrollableHeight = scroller.ScrollableHeight;
        var target = Math.Max(scroller.ViewportHeight * 2.5, scroller.ScrollableHeight + scroller.ViewportHeight * 1.25);
        var watch = Stopwatch.StartNew();
        while (scroller.ScrollableHeight < target || cards.Items.Count <= scenario.InitialLoadedItems)
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= StepBudget) throw new ProbeFailure("FixturePagingTimedOut");
            if (!cards.HasMore) throw new ProbeFailure("FixtureDepthUnavailable", "Unsupported");
            await checkpoint(scenario.Name + "FixtureLoadMore", cards.Items.Count);
            await cards.LoadMoreAsync().WaitAsync(StepBudget, token);
            await WaitAsync(() => Ready(cards), "GridAdditionalPage", token);
            await LayoutAsync(scroller, token);
        }
        scenario.LoadedItemsAtSave = cards.Items.Count;
        await checkpoint(scenario.Name + "FixtureSetOffset", cards.Items.Count);
        await SetFixtureOffsetAsync(scroller, target, scenario, token);
        await checkpoint(scenario.Name + "FixtureOffsetSaved", cards.Items.Count);
    }

    private static async Task PrepareSearchAsync(SearchPage page, NavigationScrollScenario scenario,
        Func<string, int, Task> checkpoint, CancellationToken token)
    {
        await checkpoint("SearchFixtureInitialLayout", page.ViewModel.Groups.Sum(group => group.Cards.Items.Count));
        await WaitAsync(() => SearchReady(page), "SearchInitialData", token);
        var scroller = Scroller(page);
        await LayoutAsync(scroller, token);
        scenario.InitialLoadedItems = page.ViewModel.Groups.Sum(group => group.Cards.Items.Count);
        scenario.InitialScrollableHeight = scroller.ScrollableHeight;
        var target = Math.Max(scroller.ViewportHeight * 2.5, scroller.ScrollableHeight + scroller.ViewportHeight * 1.25);
        var watch = Stopwatch.StartNew();
        while (scroller.ScrollableHeight < target || page.ViewModel.Groups.Sum(group => group.Cards.Items.Count) <= scenario.InitialLoadedItems)
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= StepBudget) throw new ProbeFailure("SearchFixturePagingTimedOut");
            var group = page.ViewModel.Groups.FirstOrDefault(group => group.Cards.HasMore);
            if (group is null) throw new ProbeFailure("SearchFixtureDepthUnavailable", "Unsupported");
            // 使用当前真实页面的分页命令，与“加载更多”按钮调用相同观察。
            await checkpoint("SearchFixtureLoadMore", page.ViewModel.Groups.Sum(group => group.Cards.Items.Count));
            await group.Cards.LoadMoreAsync().WaitAsync(StepBudget, token);
            await WaitAsync(() => SearchReady(page), "SearchAdditionalPage", token);
            await LayoutAsync(scroller, token);
        }
        scenario.LoadedItemsAtSave = page.ViewModel.Groups.Sum(group => group.Cards.Items.Count);
        await checkpoint("SearchFixtureSetOffset", scenario.LoadedItemsAtSave);
        await SetFixtureOffsetAsync(scroller, target, scenario, token);
        await checkpoint("SearchFixtureOffsetSaved", page.ViewModel.Groups.Sum(group => group.Cards.Items.Count));
    }

    private static async Task LayoutAsync(ScrollViewer scroller, CancellationToken token)
    {
        double? previous = null;
        await WaitAsync(() =>
        {
            var height = scroller.ScrollableHeight;
            var unchanged = previous.HasValue && Math.Abs(previous.Value - height) <= OffsetTolerance;
            previous = height;
            return scroller.IsLoaded && scroller.ViewportHeight > 0 && double.IsFinite(height) && unchanged;
        }, "StableScrollerLayout", token, stable: true);
    }

    private static async Task SetFixtureOffsetAsync(ScrollViewer scroller, double target, NavigationScrollScenario scenario, CancellationToken token)
    {
        scenario.ViewportHeight = scroller.ViewportHeight;
        scenario.ExpectedOffset = target;
        scroller.ChangeView(null, target, null, true);
        await WaitAsync(() => Math.Abs(scroller.VerticalOffset - target) <= OffsetTolerance, "SetDeepFixtureOffset", token, stable: true);
        scenario.OffsetAtSave = scroller.VerticalOffset;
        if (scenario.OffsetAtSave <= scenario.InitialScrollableHeight + OffsetTolerance || scenario.OffsetAtSave < scenario.ViewportHeight * 2)
            throw new ProbeFailure("FixtureWasNotDeep");
    }

    private static async Task AssertEvictedAsync(MainWindow window, FrameworkElement oldPage, NavigationReport report, NavigationScrollScenario scenario, CancellationToken token)
    {
        await WaitAsync(() => !window.Shell.PageHost.Children.Contains(oldPage) && !oldPage.IsLoaded, "PageEviction", token);
        scenario.OldPageRemoved = true;
        Mark(report, scenario.Name + "Evicted", window.Shell.PageHost.Children.Count <= 4);
    }

    private static async Task TraverseAsync(MainWindow window, Navigator navigation, bool forward, int count, CancellationToken token,
        Func<int, Task>? checkpoint = null)
    {
        for (var index = 0; index < count; index++)
        {
            if (checkpoint is not null) await checkpoint(index);
            if (!(forward ? navigation.GoForward() : navigation.GoBack())) throw new ProbeFailure("HistoryTraversalUnavailable");
            var kind = navigation.Current.Route.Kind;
            await WaitAsync(() => PageMatches(window.Shell.PageHost.CurrentPage, kind), "HistoryPageLayout", token);
        }
    }

    private static async Task ObserveRestoreAsync(ScrollViewer scroller, Func<bool> dataReady, NavigationScrollScenario scenario, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await WaitAsync(() => dataReady() && scroller.IsLoaded && scroller.ViewportHeight > 0 &&
                scroller.ScrollableHeight + OffsetTolerance >= scenario.ExpectedOffset &&
                Math.Abs(scroller.VerticalOffset - scenario.ExpectedOffset) <= OffsetTolerance,
                "RestoredOffset", token, stable: true);
        }
        finally
        {
            scenario.RestoredOffset = scroller.VerticalOffset;
            scenario.RestoredScrollableHeight = scroller.ScrollableHeight;
            scenario.RestoreMilliseconds = watch.Elapsed.TotalMilliseconds;
            scenario.OffsetDifference = Math.Abs(scenario.RestoredOffset - scenario.ExpectedOffset);
        }
    }

    private static async Task WaitAsync(Func<bool> ready, string reason, CancellationToken token, bool stable = false)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan? matchedAt = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (ready())
            {
                matchedAt ??= watch.Elapsed;
                if (!stable || watch.Elapsed - matchedAt.Value >= TimeSpan.FromMilliseconds(200)) return;
            }
            else matchedAt = null;
            if (watch.Elapsed >= StepBudget) throw new ProbeFailure(reason + "TimedOut");
            await Task.Delay(25, token);
        }
    }

    private static void Mark(NavigationReport report, string name, bool passed)
    {
        report.Checks.Add(new() { Name = name, Passed = passed });
        if (!passed) throw new ProbeFailure(name);
    }

    private sealed class ProbeFailure(string reason, string status = "Failed") : Exception("NavigationProbeCheckFailed")
    {
        public string Reason { get; } = reason;
        public string Status { get; } = status;
    }
}

internal sealed class NavigationReport
{
    public bool Passed { get; set; }
    public string Stage { get; set; } = "NotStarted";
    public string Status { get; set; } = "NotRun";
    public string Reason { get; set; } = "";
    public string ErrorKind { get; set; } = "";
    public int NavigationEvents { get; set; }
    public bool LayoutCycleTracingEnabled { get; set; }
    public int ActivePageKind { get; set; }
    public int StepIndex { get; set; }
    public int LoadedItems { get; set; }
    public double? CurrentOffset { get; set; }
    public double? CurrentScrollableHeight { get; set; }
    public double? CurrentViewportHeight { get; set; }
    public bool ReturnedToStartingRoute { get; set; }
    public double DurationMilliseconds { get; set; }
    public List<NavigationCheck> Checks { get; set; } = [];
    public List<NavigationScrollScenario> Scenarios { get; set; } = [];
}

internal sealed class NavigationCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
}

internal sealed class NavigationScrollScenario
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "NotRun";
    public string Reason { get; set; } = "";
    public string ErrorKind { get; set; } = "";
    public string ErrorHResult { get; set; } = "";
    public string FailureStage { get; set; } = "";
    public bool OldPageRemoved { get; set; }
    public int InitialLoadedItems { get; set; }
    public int LoadedItemsAtSave { get; set; }
    public double ViewportHeight { get; set; }
    public double InitialScrollableHeight { get; set; }
    public double ExpectedOffset { get; set; }
    public double OffsetAtSave { get; set; }
    public double RestoredOffset { get; set; }
    public double? ForwardOffset { get; set; }
    public double RestoredScrollableHeight { get; set; }
    public double OffsetDifference { get; set; }
    public double RestoreMilliseconds { get; set; }
}
