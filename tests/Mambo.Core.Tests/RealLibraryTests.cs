using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Data;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class RealLibraryTests
{
    [Fact]
    public void MappingKeepsOwnImagesBeforeParentAndSeriesAndNeverMapsSources()
    {
        var series = new MediaItem("series", "剧集", MediaKind.Series)
        { Images = [new("series", ImageKind.Logo, "series-logo"), new("series", ImageKind.Backdrop, "series-backdrop")] };
        var mapped = EmbyMapper.Item(new EmbyItem
        {
            Id = "episode", Name = "单集", Type = "Episode", SeriesId = "series",
            ImageTags = new() { ["Primary"] = "own-primary", ["Thumb"] = "own-thumb", ["Logo"] = "own-logo" },
            BackdropImageTags = ["own-backdrop"], ParentBackdropItemId = "parent", ParentBackdropImageTags = ["parent-backdrop"],
            ParentLogoItemId = "parent", ParentLogoImageTag = "parent-logo",
            MediaSources = [new() { Path = "https://example.invalid/stream", RequiredHttpHeaders = new() { ["X-Emby-Token"] = Guid.NewGuid().ToString("N") } }],
        }, series: series)!;
        Assert.Equal(["episode", "parent", "series"], mapped.Images.Where(image => image.Kind == ImageKind.Backdrop).Select(image => image.ItemId));
        Assert.Equal(["episode", "parent", "series"], mapped.Images.Where(image => image.Kind == ImageKind.Logo).Select(image => image.ItemId));
        Assert.Contains(mapped.Images, image => image.Kind == ImageKind.Thumb && image.ItemId == "episode");
        Assert.False(mapped.Genres.IsDefault);
        Assert.False(mapped.People.IsDefault);
        Assert.DoesNotContain("example.invalid", mapped.ToString());
    }

    [Fact]
    public async Task LibrariesExcludeNonVideoAndKeepServerOrder()
    {
        using var harness = new LibraryHarness((_, _) => Task.FromResult(Items(
            [View("music", "music"), View("shows", "tvshows"), View("photos", "photos"),
                View("movies", "movies"), View("mixed", null), View("books", "books"), View("sets", "boxsets"),
                View("channels", "channels"), View("folders", "folders"), View("live", "livetv"), View("lists", "playlists"),
                View("not-folder", "movies") with { Type = "Movie" }])));
        using var query = harness.Library.ObserveLibraries(TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["shows", "movies", "mixed"], query.Current.Select(item => item.Id));
        Assert.Null(query.Error);
        Assert.False(query.Current.IsDefault);
    }

    [Fact]
    public async Task MovieLibraryRequestsVideosAndDiscardsUnexpectedKindsWithoutChangingRawCursor()
    {
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            var parameters = Parameters(request);
            Assert.Equal("Movie,Video", parameters["IncludeItemTypes"]);
            return Task.FromResult(Items([Movie("movie"), Movie("video") with { Type = "Video" },
                Movie("season") with { Type = "Season" }], 3));
        });
        using var query = harness.Library.ObserveLibrary("movies", new(), scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.Equal(["movie", "video"], query.Items.Select(item => item.Id));
        Assert.False(query.HasMore);
        Assert.Null(query.TotalCount);
    }

    [Fact]
    public void MetadataInputLimitsRejectUtf8IdsAndOversizedSearchTerms()
    {
        Assert.Null(EmbyMapper.Identity(new string('中', 86)));
        Assert.NotNull(EmbyMapper.Identity(new string('中', 85)));
        Assert.Null(EmbyMapper.Identity("条目\n"));
        using var harness = new LibraryHarness((_, _) => throw new InvalidOperationException("输入错误不应启动 HTTP。"));
        var error = Assert.Throws<AppException>(() => { using var query = harness.Library.ObserveSearch("movies", new string('a', 257), scopeToken: TestContext.Current.CancellationToken); });
        Assert.Equal(ErrorCodes.InvalidArgument, error.Error.Code);
    }

    [Fact]
    public async Task ReturnedObservationsPublishTheirOwnSenderOnlyThroughUiScheduler()
    {
        using var harness = new LibraryHarness((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal) ? Items([View("movies", "movies")]) :
            request.RequestUri.AbsolutePath.EndsWith("/Items/movie", StringComparison.Ordinal) ? Item(Movie("movie")) : Items([Movie("movie")], 1)));
        using var detail = harness.Library.ObserveDetail("movie", TestContext.Current.CancellationToken);
        using var page = harness.Library.ObserveLibrary("movies", new(), scopeToken: TestContext.Current.CancellationToken);
        var detailSenders = new List<object?>();
        var pageSenders = new List<object?>();
        detail.Updated += (sender, _) => detailSenders.Add(sender);
        page.Updated += (sender, _) => pageSenders.Add(sender);
        await detail.RefreshAsync(TestContext.Current.CancellationToken);
        // 同步命中首屏缓存只提供初值；显式刷新确保本测试触发一次通知。
        await page.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(detailSenders);
        Assert.Empty(pageSenders);
        harness.Scheduler.Drain();
        Assert.NotEmpty(detailSenders);
        Assert.NotEmpty(pageSenders);
        Assert.All(detailSenders, sender => Assert.Same(detail, sender));
        Assert.All(pageSenders, sender => Assert.Same(page, sender));
        var notifications = detailSenders.Count + pageSenders.Count;
        await detail.RefreshAsync(TestContext.Current.CancellationToken);
        await page.RefreshAsync(TestContext.Current.CancellationToken);
        detail.Dispose(); page.Dispose();
        harness.Scheduler.Drain();
        Assert.Equal(notifications, detailSenders.Count + pageSenders.Count);
    }

    [Fact]
    public async Task FilterFallbackScansBeyondFirstFiveHundredItems()
    {
        var offsets = new ConcurrentQueue<int>();
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Items/Filters", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var values = Parameters(request);
            Assert.Equal("500", values["Limit"]);
            Assert.Equal("false", values["EnableImages"]);
            var offset = int.Parse(values["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            offsets.Enqueue(offset);
            var rows = offset == 0 ? Enumerable.Range(0, 500).Select(index => Movie("movie-" + index) with
                { Genres = ["剧情"], ProductionYear = 2000, OfficialRating = "PG" }).ToArray() :
                [Movie("tail") with { Genres = ["尾页类型"], ProductionYear = 2037, OfficialRating = "R" }];
            return Task.FromResult(Items(rows, 501));
        });
        using var query = harness.Library.ObserveFilters("movies", TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Null(query.Error);
        Assert.Contains("尾页类型", query.Current!.Genres);
        Assert.Contains(2037, query.Current.Years);
        Assert.Equal([0, 500], offsets.Distinct());
    }

    [Fact]
    public async Task NextUpMayInitializeWithNullWhenSeriesHasNoPlayableEpisodes()
    {
        using var harness = new LibraryHarness((_, _) => Task.FromResult(Items([], 0)));
        using var query = harness.Library.ObserveNextUp("empty-series", TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(query.IsInitialized);
        Assert.Null(query.Current);
        Assert.Null(query.Error);
    }

    [Fact]
    public async Task NextUpFallbackSelectsFirstEpisodeWithThirtySecondResume()
    {
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Shows/NextUp", StringComparison.Ordinal))
                return Task.FromResult(Items([], 0));
            if (request.RequestUri.AbsolutePath.EndsWith("/Items/series", StringComparison.Ordinal))
                return Task.FromResult(Item(new() { Id = "series", Name = "剧集", Type = "Series" }));
            return Task.FromResult(Items([Episode("one", "series", 1), Episode("two", "series", 2) with
                { UserData = new() { PlaybackPositionTicks = TimeSpan.FromSeconds(5).Ticks } }, Episode("three", "series", 3) with
                { UserData = new() { PlaybackPositionTicks = TimeSpan.FromSeconds(45).Ticks } }]));
        });
        using var query = harness.Library.ObserveNextUp("series", TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Null(query.Error);
        Assert.Equal("three", query.Current?.Id);
    }

    [Fact]
    public async Task PagingCachesOnlyDefaultFirstPageAndRefreshReplacesIncrementalPages()
    {
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)) return Task.FromResult(Items([View("movies", "movies")]));
            var parameters = Parameters(request);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            var count = int.Parse(parameters["Limit"], System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(Items(Enumerable.Range(offset, Math.Min(count, 130 - offset)).Select(index => Movie("movie-" + index)).ToArray(), 130));
        });
        using var query = harness.Library.ObserveLibrary("movies", new(), scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Equal(60, query.Items.Length);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(120, query.Items.Length);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(130, query.Items.Length);
        Assert.False(query.HasMore);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(60, query.Items.Length);
        await harness.Cache.FlushAsync(TestContext.Current.CancellationToken);
        var files = Directory.GetFiles(harness.Paths.QueryCache, "*.json");
        Assert.Single(files);
        var persisted = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Contains("library-first", persisted);
        Assert.DoesNotContain("movie-129", persisted);
        Assert.DoesNotContain("example.invalid", persisted);
        Assert.DoesNotContain(harness.Accounts.Current!.Secret.AccessToken, persisted);
    }

    [Fact]
    public async Task SearchFoldsEpisodesAndKeepsRawServerPaginationCursor()
    {
        var offsets = new ConcurrentQueue<int>();
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Items/series-a", StringComparison.Ordinal)) return Task.FromResult(Item(new() { Id = "series-a", Name = "剧集A", Type = "Series" }));
            if (request.RequestUri.AbsolutePath.EndsWith("/Items/series-b", StringComparison.Ordinal)) return Task.FromResult(Item(new() { Id = "series-b", Name = "剧集B", Type = "Series" }));
            var parameters = Parameters(request);
            Assert.Equal("测试", parameters["SearchTerm"]);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            offsets.Enqueue(offset);
            return Task.FromResult(Items(offset == 0 ? [Episode("a1", "series-a", 1), Episode("a2", "series-a", 2)] :
                [Episode("a3", "series-a", 3), Episode("b1", "series-b", 1)], 4));
        });
        using var query = harness.Library.ObserveSearch("shows", " 测试！ ", 2, TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Equal("series-a", Assert.Single(query.Items).Id);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Null(query.Error);
        Assert.Equal(["series-a", "series-b"], query.Items.Select(item => item.Id));
        Assert.Equal([0, 2], offsets.Distinct());
        Assert.False(query.HasMore);
    }

    [Fact]
    public async Task PlaybackStoppedAutomaticallyRefreshesActiveDetail()
    {
        var version = 1;
        using var harness = new LibraryHarness((_, _) => Task.FromResult(Item(Movie("movie") with { Name = "版本" + Volatile.Read(ref version) })));
        using var query = harness.Library.ObserveDetail("movie", TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("版本1", query.Current?.Name);
        Volatile.Write(ref version, 2);
        harness.Messenger.Send(new PlaybackStopped("movie"));
        await UntilAsync(() => query.Current?.Name == "版本2", harness.Scheduler);
        Assert.Equal("版本2", query.Current?.Name);
    }

    [Fact]
    public async Task AccountSwitchTerminatesOldObservationWithoutPublishingNewAccountData()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/Users/old-user/", StringComparison.Ordinal))
            { started.TrySetResult(); return oldResponse.Task; }
            return Task.FromResult(Item(Movie("movie") with { Name = "新账号" }));
        });
        using var old = harness.Library.ObserveDetail("movie", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Accounts.Set(LibraryHarness.NewAccount("new-user"));
        using var current = harness.Library.ObserveDetail("movie", TestContext.Current.CancellationToken);
        await current.RefreshAsync(TestContext.Current.CancellationToken);
        oldResponse.SetResult(Item(Movie("movie") with { Name = "旧账号迟到数据" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal("新账号", current.Current?.Name);
        Assert.False(old.IsInitialized);
    }

    [Fact]
    public async Task PrefetchHoldsObservationUntilRequestFinishesAndWarmsDetails()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var harness = new LibraryHarness((_, _) => { Interlocked.Increment(ref calls); started.TrySetResult(); return response.Task; });
        harness.Library.PrefetchDetail("movie");
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        response.SetResult(Item(Movie("movie")));
        using var query = harness.Library.ObserveDetail("movie", TestContext.Current.CancellationToken);
        await UntilAsync(() => query.IsInitialized, harness.Scheduler);
        Assert.Equal("movie", query.Current?.Id);
        Assert.Equal(1, calls);
    }

    private static EmbyItem View(string id, string? collection) => new() { Id = id, Name = id, Type = "CollectionFolder", CollectionType = collection };
    private static EmbyItem Movie(string id) => new() { Id = id, Name = id, Type = "Movie", RunTimeTicks = TimeSpan.FromMinutes(90).Ticks };
    private static EmbyItem Episode(string id, string seriesId, int index) => new() { Id = id, Name = id, Type = "Episode",
        SeriesId = seriesId, SeasonId = "season", ParentIndexNumber = 1, IndexNumber = index, RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };
    private static HttpResponseMessage Items(EmbyItem[] items, int? total = null) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new EmbyItems { Items = items, TotalRecordCount = total }, EmbyJsonContext.Default.EmbyItems)) };
    private static HttpResponseMessage Item(EmbyItem item) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(item, EmbyJsonContext.Default.EmbyItem)) };
    private static Dictionary<string, string> Parameters(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.Ordinal);
    private static Task InitializedAsync(IPagedQuery<MediaItem> query) => UntilAsync(() => query.IsInitialized || query.Error is not null);
    private static async Task UntilAsync(Func<bool> ready, LibraryTestScheduler? scheduler = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!ready()) { scheduler?.Drain(); await Task.Delay(5, timeout.Token); }
    }

    private sealed class LibraryHarness : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "Mambo-LibraryTests-" + Guid.NewGuid().ToString("N")));
        public LibraryTestScheduler Scheduler { get; } = new();
        public AccountContext Accounts { get; } = new();
        public IMessenger Messenger { get; } = new WeakReferenceMessenger();
        public QueryCache Cache { get; }
        public LibraryService Library { get; }
        private readonly EmbyApi api;
        private readonly RequestScheduler requests = new();
        public LibraryHarness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            Accounts.Set(NewAccount("old-user"));
            api = new(Guid.NewGuid(), new LibraryHandler(handler));
            Cache = new(Scheduler, new QueryPersistence(Paths));
            Library = new(Accounts, api, requests, Cache, Scheduler, Messenger);
        }
        public static AccountSession NewAccount(string userId) => new(new SessionSecret("https://example.invalid/emby", "generated-server", userId, "合成用户", Guid.NewGuid().ToString("N")));
        public void Dispose()
        {
            Library.Dispose(); Cache.Dispose(); requests.Dispose(); api.Dispose(); Accounts.Dispose();
            var path = Path.GetFullPath(Paths.Root);
            if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
    private sealed class LibraryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
    private sealed class LibraryTestScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> pending = new();
        public bool TryEnqueue(Action callback) { pending.Enqueue(callback); return true; }
        public void Drain() { while (pending.TryDequeue(out var callback)) callback(); }
    }
}
