using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.App.Views.Controls;
using Mambo.Core.Contracts;
using Mambo.Core.Data;
using Mambo.Core.Fakes;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace Mambo.App.Debug;

/// <summary>
/// 真正的停止通知 → LibraryService 失效/刷新 → 三个实际页面的 XAML 进度绑定。
/// 账号只驻留 RAM，HTTP 只接受合成白名单请求；不手动刷新查询或补发停止消息。
/// 固定假目录不能保存播放进度，因此只替换独立页面的数据来源，不改全局服务。
/// </summary>
internal static class PlaybackRefreshSmoke
{
    private const string ItemId = "demo-movie-0007";
    private static readonly long InitialPosition = TimeSpan.FromSeconds(100).Ticks;
    private static readonly long SeekPosition = TimeSpan.FromMinutes(10).Ticks;

    public static async Task<PlaybackRefreshReport> RunAsync(MainWindow window, string fixtureDirectory, CancellationToken token)
    {
        var report = new PlaybackRefreshReport();
        var services = window.Services;
        if (!window.DispatcherQueue.HasThreadAccess ||
            services.GetRequiredService<ILibraryService>() is not FakeLibraryService ||
            services.GetRequiredService<IPlaybackService>() is not FakePlaybackService playback ||
            services.GetRequiredService<ISessionService>() is not FakeSessionService ||
            services.GetRequiredService<ISettingsService>() is not FakeSettingsService ||
            services.GetRequiredService<ILibraryPreferences>() is not FakeLibraryPreferences ||
            services.GetRequiredService<IImageService>() is not FakeImageService)
        {
            Check(report, "OwnedFakeUiRequired", false);
            return report;
        }
        if (playback.Current is not null || playback.IsStarting || !window.Shell.IsLoaded ||
            window.Shell.FindName("Root") is not Grid root || root.XamlRoot is null ||
            window.Shell.FindName("PlayerSlot") is not UIElement playerSlot)
        {
            Check(report, "IdleShellRequired", false);
            return report;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var probeToken = deadline.Token;
        var navigation = services.GetRequiredService<Navigator>();
        var entryBefore = navigation.Current;
        var pageBefore = window.Shell.PageHost.CurrentPage;
        var scheduler = services.GetRequiredService<IUiScheduler>();
        var messenger = services.GetRequiredService<IMessenger>();
        var presentation = services.GetRequiredService<WindowContext>();
        var source = services.GetRequiredService<DemoCatalog>().Find(ItemId)!;
        var duration = source.RunTimeTicks ?? 0;
        Grid? mount = null;
        DetailViewModel? detailModel = null;
        RecentViewModel? recentModel = null;
        LibraryViewModel? libraryModel = null;
        DetailPage? detail = null;
        RecentPage? recent = null;
        LibraryPage? libraryPage = null;
        IPlaybackSession? ownedSession = null;
        AccountContext? accounts = null;
        QueryCache? cache = null;
        RequestScheduler? requests = null;
        EmbyApi? api = null;
        FixtureHttp? http = null;
        FakeLibraryPreferences? preferences = null;
        LibraryService? library = null;
        EventHandler? onSnapshot = null;
        var cleanupSucceeded = true;
        try
        {
            report.Stage = "CreateIsolatedLibrary";
            Require(report, "PlayableFixtureDuration", duration > SeekPosition);
            // 仅新建本轮空目录供生产 QueryPersistence 查询；写入委托完全在内存中完成。
            // 不读取默认 AppPaths、不加载真实设置/凭据、不复用任何旧缓存。
            if (Directory.Exists(fixtureDirectory)) throw new InvalidOperationException("FixtureDirectoryReused");
            var paths = new AppPaths(fixtureDirectory);
            accounts = new AccountContext();
            var userId = Guid.NewGuid().ToString("N");
            accounts.Set(new AccountSession(new SessionSecret("https://playback-refresh.invalid/",
                Guid.NewGuid().ToString("N"), userId, "界面诊断", Guid.NewGuid().ToString("N"))));
            http = new FixtureHttp(userId, duration);
            api = new EmbyApi(Guid.NewGuid(), http);
            requests = new RequestScheduler();
            cache = new QueryCache(scheduler, new QueryPersistence(paths), write: static (_, _, _) => Task.CompletedTask);
            library = new LibraryService(accounts, api, requests, cache, scheduler, messenger);
            messenger.Register<FixtureHttp, PlaybackStopped>(http, static (fixture, message) =>
            {
                if (message.ItemId == ItemId) Interlocked.Increment(ref fixture.StoppedCount);
            });
            preferences = new FakeLibraryPreferences(scheduler);
            detailModel = new DetailViewModel(library, services.GetRequiredService<PlaybackLauncher>(), playback, ItemId);
            detail = new DetailPage(detailModel, presentation);
            recentModel = new RecentViewModel(library);
            recent = new RecentPage(recentModel);
            libraryModel = new LibraryViewModel(library, preferences,
                new MediaLibrary(DemoCatalog.MoviesLibraryId, "进度诊断", LibraryKind.Movies));
            libraryPage = new LibraryPage(libraryModel);
            mount = new Grid { IsHitTestVisible = false };
            for (var index = 0; index < 3; index++) mount.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Grid.SetColumn(recent, 1);
            Grid.SetColumn(libraryPage, 2);
            mount.Children.Add(detail);
            mount.Children.Add(recent);
            mount.Children.Add(libraryPage);
            Grid.SetRow(mount, 1);
            Grid.SetColumn(mount, 1);
            // 播放器仍位于这些独立诊断页面之上；不替换 PageHost/DI/静态 CardActions。
            var playerIndex = root.Children.IndexOf(playerSlot);
            Require(report, "PlayerSlotOwnedByShell", playerIndex >= 0);
            root.Children.Insert(playerIndex, mount);
            recent.OnNavigatedTo(new NavEntry(Route.Recent), NavigationMode.New, created: true);
            libraryPage.OnNavigatedTo(new NavEntry(Route.Library(DemoCatalog.MoviesLibraryId)), NavigationMode.New, created: true);
            detail.OnNavigatedTo(new NavEntry(Route.Detail(ItemId)), NavigationMode.New, created: true);

            report.Stage = "ObserveInitialXaml";
            var initialFraction = (double)InitialPosition / duration;
            await WaitAsync(() => PagesAt(detail, recent, libraryPage, initialFraction, InitialPosition), probeToken);
            Require(report, "InitialDetailRingAndHint", DetailAt(detail, initialFraction));
            Require(report, "InitialRecentCardBinding", CardAt(recent, initialFraction, InitialPosition));
            Require(report, "InitialLibraryCardBinding", CardAt(libraryPage, initialFraction, InitialPosition));
            var hintBefore = detailModel.PlayHint;
            var recentItemBefore = recentModel.Cards.Items.Single().Item;
            var libraryItemBefore = libraryModel.Cards.Items.Single().Item;
            var detailRequestsBefore = Volatile.Read(ref http.DetailRequests);
            var recentRequestsBefore = Volatile.Read(ref http.RecentRequests);
            var libraryRequestsBefore = Volatile.Read(ref http.LibraryRequests);

            report.Stage = "SeekAndCloseActualPlayer";
            var session = await playback.PlayAsync(new PlayRequest(ItemId, InitialPosition), probeToken);
            ownedSession = session;
            await WaitAsync(() => window.Shell.ActivePlayer is { IsLoaded: true } player &&
                ReferenceEquals(player.Session, session) && session.Snapshot.Phase == PlayerPhase.Playing, probeToken);
            if (!session.Snapshot.IsPaused) await session.TogglePauseAsync(probeToken);
            await session.SeekAsync(TimeSpan.FromTicks(SeekPosition), probeToken);
            await WaitAsync(() => session.Snapshot.IsPaused && session.Snapshot.PositionTicks == SeekPosition, probeToken);
            // 合成服务器在实际 Closed 快照通知中保存最终位置；该通知先于真实 PlaybackStopped。
            // 若关闭未发生，fixture 不更新；若生产失效链断开，三个页面会保持旧数值并超时失败。
            var fixture = http;
            onSnapshot = (_, _) =>
            {
                var snapshot = session.Snapshot;
                if (snapshot.Phase == PlayerPhase.Closed && snapshot.Entry?.ItemId == ItemId)
                {
                    Interlocked.Exchange(ref fixture.Position, snapshot.PositionTicks);
                    Interlocked.Increment(ref fixture.ClosedSnapshots);
                }
            };
            session.SnapshotChanged += onSnapshot;
            Require(report, "SeekDoesNotArtificiallyRefreshPages", http.Position == InitialPosition &&
                PagesAt(detail, recent, libraryPage, initialFraction, InitialPosition));
            var activePlayer = window.Shell.ActivePlayer!;
            var close = activePlayer.FindName("CloseButton").As<Button>();
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(close) ?? new ButtonAutomationPeer(close);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            Require(report, "ActualCloseButtonInvokeProvider", invoke is not null);
            invoke!.Invoke();
            await WaitAsync(() => playback.Current is null && window.Shell.ActivePlayer is null, probeToken);

            report.Stage = "ObserveStoppedRefreshXaml";
            var finalPosition = session.Snapshot.PositionTicks;
            Require(report, "FixtureSavedRealClosedPosition", finalPosition == SeekPosition && http.Position == finalPosition &&
                http.ClosedSnapshots == 1 && http.StoppedCount == 1);
            var fraction = (double)finalPosition / duration;
            await WaitAsync(() => PagesAt(detail, recent, libraryPage, fraction, finalPosition), probeToken);
            Require(report, "StoppedAutomaticallyRefetchesDetail", http.DetailRequests > detailRequestsBefore);
            Require(report, "StoppedAutomaticallyRefetchesRecent", http.RecentRequests > recentRequestsBefore);
            Require(report, "StoppedAutomaticallyRefetchesLibrary", http.LibraryRequests > libraryRequestsBefore);
            Require(report, "DetailRingAndContinueHintUpdated", DetailAt(detail, fraction) && hintBefore != detailModel.PlayHint);
            Require(report, "RecentProgressUpdatedByNewItem", CardAt(recent, fraction, finalPosition) &&
                !ReferenceEquals(recentItemBefore, recentModel.Cards.Items.Single().Item));
            Require(report, "LibraryProgressAndSubtitleUpdatedByNewItem", CardAt(libraryPage, fraction, finalPosition) &&
                !ReferenceEquals(libraryItemBefore, libraryModel.Cards.Items.Single().Item));
            Require(report, "SyntheticHttpOnly", http.RejectedRequests == 0);
        }
        catch (Exception error)
        {
            report.FailureKind = error is OperationCanceledException ? "DeadlineExceeded" : error.GetType().Name;
            Check(report, "ScenarioCompleted", false);
        }
        finally
        {
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception) { cleanupSucceeded = false; }
            }
            if (ownedSession is not null && onSnapshot is not null) Cleanup(() => ownedSession.SnapshotChanged -= onSnapshot);
            if (ownedSession is not null && ReferenceEquals(playback.Current, ownedSession))
            {
                try { await ownedSession.CloseAsync(CancellationToken.None); }
                catch (Exception) { cleanupSucceeded = false; }
            }
            if (mount is not null) Cleanup(() => { root.Children.Remove(mount); mount.Children.Clear(); });
            Cleanup(() => { if (detail is not null) detail.Dispose(); else detailModel?.Dispose(); });
            Cleanup(() => { if (recent is not null) recent.Dispose(); else recentModel?.Dispose(); });
            Cleanup(() => { if (libraryPage is not null) libraryPage.Dispose(); else libraryModel?.Dispose(); });
            if (http is not null) Cleanup(() => messenger.UnregisterAll(http));
            if (library is not null) Cleanup(library.Dispose);
            if (cache is not null)
            {
                try { await cache.DisposeAsync(); }
                catch (Exception) { cleanupSucceeded = false; }
            }
            if (preferences is not null) Cleanup(preferences.Dispose);
            if (requests is not null) Cleanup(requests.Dispose);
            if (api is not null) Cleanup(api.Dispose);
            if (accounts is not null) Cleanup(accounts.Dispose);
            Check(report, "GlobalPageAndHistoryUnchanged", ReferenceEquals(entryBefore, navigation.Current) &&
                ReferenceEquals(pageBefore, window.Shell.PageHost.CurrentPage));
            report.CleanupPassed = cleanupSucceeded && playback.Current is null && window.Shell.ActivePlayer is null &&
                !navigation.ForwardBlocked && (mount is null || !root.Children.Contains(mount)) &&
                (library is null || !messenger.IsRegistered<PlaybackStopped>(library)) &&
                (http is null || !messenger.IsRegistered<PlaybackStopped>(http));
            Check(report, "OwnedResourcesCleaned", report.CleanupPassed);
        }
        report.Passed = report.CleanupPassed && report.Checks.Count > 0 && report.Checks.Values.All(passed => passed);
        if (report.Passed) report.Stage = "Complete";
        return report;
    }

    private static bool PagesAt(DetailPage detail, RecentPage recent, LibraryPage library, double fraction, long position) =>
        detail.IsLoaded && recent.IsLoaded && library.IsLoaded && DetailAt(detail, fraction) &&
        CardAt(recent, fraction, position) && CardAt(library, fraction, position);

    private static bool DetailAt(DetailPage page, double fraction)
    {
        if (!page.ViewModel.HasContent || Math.Abs(page.ViewModel.ProgressFraction - fraction) > 0.000001) return false;
        // 进度环是播放钮里自绘的一段圆弧（原版样式），页面公开它实际画出的比例。
        return page.FindName("PlayButton").As<Button>().IsLoaded && page.PlayProgressVisible &&
            Math.Abs(page.ShownPlayProgress - fraction) <= 0.000001 &&
            Descendants(page).OfType<TextBlock>().Any(text => text.IsLoaded && text.Text == page.ViewModel.PlayHint);
    }

    private static bool CardAt(FrameworkElement page, double fraction, long position)
    {
        var card = Descendants(page).OfType<CardBase>().FirstOrDefault(item => item.Item?.Id == ItemId);
        if (card?.Item is not { } item || !card.IsLoaded || card.ActualWidth <= 0 ||
            item.Item.UserData.PlaybackPositionTicks != position || Math.Abs(item.ProgressFraction - fraction) > 0.000001)
            return false;
        var art = card.FindName("Art").As<Grid>();
        foreach (var child in art.Children)
        {
            Grid progress;
            try { progress = child.As<Grid>(); }
            catch (InvalidCastException) { continue; }
            if (progress.ColumnDefinitions.Count != 2 || progress.Height != 4) continue;
            var done = progress.ColumnDefinitions[0].Width;
            var rest = progress.ColumnDefinitions[1].Width;
            var subtitleMatches = page is not LibraryPage || Descendants(card).OfType<TextBlock>()
                .Any(text => text.IsLoaded && text.Text == item.Subtitle && item.Subtitle.Length > 0);
            return progress.IsLoaded && progress.Visibility == Visibility.Visible && progress.ActualWidth > 0 &&
                done.IsStar && rest.IsStar && Math.Abs(done.Value - fraction) <= 0.000001 &&
                Math.Abs(rest.Value - (1 - fraction)) <= 0.000001 && subtitleMatches;
        }
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(10, token);
        token.ThrowIfCancellationRequested();
    }
    private static void Check(PlaybackRefreshReport report, string name, bool passed) => report.Checks[name] = passed;
    private static void Require(PlaybackRefreshReport report, string name, bool passed)
    {
        Check(report, name, passed);
        if (!passed) throw new InvalidOperationException("PlaybackRefreshCheckFailed");
    }

    private sealed class FixtureHttp(string userId, long duration) : HttpMessageHandler
    {
        internal long Position = InitialPosition;
        internal int ClosedSnapshots, StoppedCount, DetailRequests, RecentRequests, LibraryRequests, RejectedRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri is not { } uri ||
                uri.Host != "playback-refresh.invalid" || uri.Scheme != Uri.UriSchemeHttps)
                return Reject();
            var prefix = "/Users/" + userId;
            var item = new EmbyItem
            {
                Id = ItemId, Name = "进度诊断电影", Type = "Movie", RunTimeTicks = duration, ProductionYear = 2025,
                UserData = new EmbyUserData { PlaybackPositionTicks = Volatile.Read(ref Position), Played = false,
                    LastPlayedDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            };
            if (uri.AbsolutePath == prefix + "/Items/" + ItemId)
            {
                Interlocked.Increment(ref DetailRequests);
                return Respond(item, EmbyJsonContext.Default.EmbyItem);
            }
            if (uri.AbsolutePath == prefix + "/Views")
                return Respond(new EmbyItems { Items = [new EmbyItem { Id = DemoCatalog.MoviesLibraryId,
                    Name = "进度诊断", Type = "CollectionFolder", CollectionType = "movies" }], TotalRecordCount = 1 },
                    EmbyJsonContext.Default.EmbyItems);
            if (uri.AbsolutePath == "/Items/Filters") return Respond(new EmbyFilters(), EmbyJsonContext.Default.EmbyFilters);
            if (uri.AbsolutePath == prefix + "/Items" && uri.Query.Contains("Filters=IsResumable", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref RecentRequests);
                return Respond(new EmbyItems { Items = [item], TotalRecordCount = 1 }, EmbyJsonContext.Default.EmbyItems);
            }
            if (uri.AbsolutePath == prefix + "/Items" &&
                uri.Query.Contains("ParentId=" + DemoCatalog.MoviesLibraryId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref LibraryRequests);
                return Respond(new EmbyItems { Items = [item], TotalRecordCount = 1 }, EmbyJsonContext.Default.EmbyItems);
            }
            return Reject();
        }
        private Task<HttpResponseMessage> Reject()
        {
            Interlocked.Increment(ref RejectedRequests);
            throw new InvalidOperationException("UnexpectedSyntheticRequest");
        }
        private static Task<HttpResponseMessage> Respond<T>(T value, JsonTypeInfo<T> type) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type)) });
    }
}

internal sealed class PlaybackRefreshReport
{
    public bool Passed { get; set; }
    public bool CleanupPassed { get; set; }
    public string Stage { get; set; } = "NotStarted";
    public string FailureKind { get; set; } = "";
    public Dictionary<string, bool> Checks { get; set; } = [];
}
