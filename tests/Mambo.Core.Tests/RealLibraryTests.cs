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
            if (request.RequestUri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            var values = Parameters(request);
            Assert.Equal("500", values["Limit"]);
            Assert.Contains("Genres", values["Fields"], StringComparison.Ordinal);
            Assert.Contains("ProductionYear", values["Fields"], StringComparison.Ordinal);
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

    [Theory]
    [InlineData("genres")]
    [InlineData("years")]
    [InlineData("ratings")]
    [InlineData("combined")]
    public async Task IgnoredLibraryFiltersMatchMetadataWithOrWithinGroupsAndAndBetweenGroups(string selection)
    {
        EmbyItem[] rows =
        [
            Movie("first") with { Genres = ["Drama"], ProductionYear = 1990, OfficialRating = "PG" },
            Movie("second") with { Genres = ["Family", "COMEDY"], ProductionYear = 1999, OfficialRating = "r" },
            Movie("other-genre") with { Genres = ["Action"], ProductionYear = 1990, OfficialRating = "PG" },
            Movie("other-year") with { Genres = ["Drama"], ProductionYear = 2000, OfficialRating = "PG" },
            Movie("other-rating") with { Genres = ["Drama"], ProductionYear = 1990, OfficialRating = "NC-17" },
            Movie("missing-metadata"),
        ];
        using var harness = new LibraryHarness((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)
                ? Items([View("movies", "movies")]) : Items(rows, rows.Length)));
        var filters = new LibraryQuery
        {
            Genres = selection is "genres" or "combined" ? ["drama", "comedy"] : [],
            Years = selection is "years" or "combined" ? [1990, 1999] : [],
            OfficialRatings = selection is "ratings" or "combined" ? ["pg", "R"] : [],
        };
        using var query = harness.Library.ObserveLibrary("movies", filters, scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        string[] expected = selection switch
        {
            "genres" => ["first", "second", "other-year", "other-rating"],
            "years" => ["first", "second", "other-genre", "other-rating"],
            "ratings" => ["first", "second", "other-genre", "other-year"],
            _ => ["first", "second"],
        };
        Assert.Null(query.Error);
        Assert.Equal(expected, query.Items.Select(item => item.Id));
        Assert.False(query.HasMore);
        Assert.Null(query.TotalCount);
    }

    [Fact]
    public async Task FilteredPagingSkipsEmptyBatchesUsesRawCappedOffsetsAndRefreshesFromStart()
    {
        var offsets = new ConcurrentQueue<int>();
        var refreshed = false;
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            var parameters = Parameters(request);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            offsets.Enqueue(offset);
            var rows = Enumerable.Range(offset, Math.Min(2, 9 - offset)).Select(index => Movie("movie-" + index) with
            {
                Genres = [(Volatile.Read(ref refreshed) ? index == 3 : index is 0 or 1 or 7) ? "动画" : "剧情"],
            }).ToArray();
            return Task.FromResult(Items(rows, 9));
        });
        using var query = harness.Library.ObserveLibrary("movies", new() { Genres = ["动画"] }, 3,
            TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.Equal(["movie-0", "movie-1"], query.Items.Select(item => item.Id));
        Assert.Equal([0], offsets.ToArray());
        Assert.True(query.HasMore);
        Assert.Null(query.TotalCount);

        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Null(query.Error);
        Assert.Equal(["movie-0", "movie-1", "movie-7"], query.Items.Select(item => item.Id));
        // Unsupported filters restart an unfiltered cursor; server-capped batches still
        // advance by their actual size, and earlier matches are not duplicated.
        Assert.Equal([0, 2, 0, 2, 4, 6], offsets.ToArray());
        Assert.True(query.HasMore);
        Assert.Null(query.TotalCount);

        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Null(query.Error);
        Assert.Equal(["movie-0", "movie-1", "movie-7"], query.Items.Select(item => item.Id));
        Assert.Equal([0, 2, 0, 2, 4, 6, 8], offsets.ToArray());
        Assert.False(query.HasMore);
        Assert.Null(query.TotalCount);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, offsets.Count);

        Volatile.Write(ref refreshed, true);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Null(query.Error);
        Assert.Equal("movie-3", Assert.Single(query.Items).Id);
        Assert.Equal([0, 2, 0, 2, 4, 6, 8, 0, 2], offsets.ToArray());
        Assert.True(query.HasMore);
        Assert.Null(query.TotalCount);
    }

    [Fact]
    public async Task FilteredPagingFindsMatchOnlyOnFinalRawPage()
    {
        var offsets = new ConcurrentQueue<int>();
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            var offset = int.Parse(Parameters(request)["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            offsets.Enqueue(offset);
            return Task.FromResult(Items(Enumerable.Range(offset, Math.Min(2, 5 - offset)).Select(index =>
                Movie("movie-" + index) with { ProductionYear = index == 4 ? 1999 : 2000 }).ToArray(), 5));
        });
        using var query = harness.Library.ObserveLibrary("movies", new() { Years = [1999] }, 2,
            TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.Equal("movie-4", Assert.Single(query.Items).Id);
        Assert.Equal([0, 0, 2, 4], offsets.ToArray());
        Assert.False(query.HasMore);
        Assert.Null(query.TotalCount);
    }

    [Fact]
    public async Task FilteredPagingWithNoMatchesInitializesAnEmptyTerminalPage()
    {
        using var harness = new LibraryHarness((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)
                ? Items([View("movies", "movies")]) : Items([Movie("unrated")], 1)));
        using var query = harness.Library.ObserveLibrary("movies", new() { OfficialRatings = ["PG"] },
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.True(query.IsInitialized);
        Assert.Empty(query.Items);
        Assert.False(query.HasMore);
        Assert.Null(query.TotalCount);
    }

    [Fact]
    public async Task SparseFiltersScanInLargeBatchesAndReusePagesAcrossCombinations()
    {
        var calls = new ConcurrentQueue<(int Offset, int Limit)>();
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            var parameters = Parameters(request);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            var limit = int.Parse(parameters["Limit"], System.Globalization.CultureInfo.InvariantCulture);
            calls.Enqueue((offset, limit));
            return Task.FromResult(Items(Enumerable.Range(offset, Math.Min(limit, 5000 - offset)).Select(index =>
                Movie("movie-" + index) with
                {
                    Genres = [index == 4999 ? "稀有类型" : "剧情"], ProductionYear = 2024,
                    OfficialRating = index == 4999 ? "PG" : "R",
                }).ToArray(), 5000));
        });
        using var first = harness.Library.ObserveLibrary("movies", new() { Genres = ["稀有类型"] },
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(first);
        Assert.Null(first.Error);
        Assert.Equal("movie-4999", Assert.Single(first.Items).Id);
        Assert.False(first.HasMore);
        Assert.Equal(11, calls.Count); // One native request plus ten shared 500-row batches.
        Assert.All(calls.Skip(1), call => Assert.Equal(500, call.Limit));

        using var combined = harness.Library.ObserveLibrary("movies", new() { Years = [2024], OfficialRatings = ["pg"] },
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(combined);
        Assert.Null(combined.Error);
        Assert.Equal("movie-4999", Assert.Single(combined.Items).Id);
        using var empty = harness.Library.ObserveLibrary("movies", new() { Years = [1990] },
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(empty);
        Assert.Null(empty.Error);
        Assert.Empty(empty.Items);
        Assert.False(empty.HasMore);
        Assert.Equal(11, calls.Count);
    }

    [Fact]
    public async Task NativeFilteringKeepsTheFastPathWithoutScanningUnrelatedItems()
    {
        var calls = 0;
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            Assert.Equal("动画", Parameters(request)["Genres"]);
            Interlocked.Increment(ref calls);
            return Task.FromResult(Items([Movie("match") with { Genres = ["动画"] }], 1));
        });
        using var query = harness.Library.ObserveLibrary("movies", new() { Genres = ["动画"] },
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.Equal("match", Assert.Single(query.Items).Id);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SharedScanDoesNotDropDenseMatchesBeyondOneDisplayPage()
    {
        var calls = 0;
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            var parameters = Parameters(request);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            var limit = int.Parse(parameters["Limit"], System.Globalization.CultureInfo.InvariantCulture);
            Interlocked.Increment(ref calls);
            return Task.FromResult(Items(Enumerable.Range(offset, Math.Min(limit, 155 - offset)).Select(index =>
                Movie("movie-" + index) with { Genres = [index == 0 ? "剧情" : "动画"] }).ToArray(), 155));
        });
        using var query = harness.Library.ObserveLibrary("movies", new() { Genres = ["动画"] }, 60,
            TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Equal(60, query.Items.Length);
        while (query.HasMore)
        {
            await query.LoadMoreAsync(TestContext.Current.CancellationToken);
            Assert.Null(query.Error);
        }
        Assert.Equal(Enumerable.Range(1, 154).Select(index => "movie-" + index), query.Items.Select(item => item.Id));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FilterOptionsPublishTheFirstBatchBeforeFallbackFinishesAndShareTheScan()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        using var harness = new LibraryHarness(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Items/Filters", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.RequestUri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)) return Items([View("movies", "movies")]);
            var parameters = Parameters(request);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            var limit = int.Parse(parameters["Limit"], System.Globalization.CultureInfo.InvariantCulture);
            if (!parameters.ContainsKey("Genres")) Interlocked.Increment(ref scans);
            if (offset == 500) { blocked.TrySetResult(); await release.Task.WaitAsync(token); }
            return Items(Enumerable.Range(offset, Math.Min(limit, 501 - offset)).Select(index =>
                Movie("movie-" + index) with { Genres = [index == 500 ? "尾页类型" : "剧情"] }).ToArray(), 501);
        });
        using var options = harness.Library.ObserveFilters("movies", TestContext.Current.CancellationToken);
        var updated = 0;
        options.Updated += (_, _) => updated++;
        await blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Scheduler.Drain();
        Assert.Contains("剧情", options.Current!.Genres);
        Assert.True(options.IsRefreshing);
        Assert.True(updated > 0);
        release.SetResult();
        await UntilAsync(() => options.IsInitialized && !options.IsRefreshing);
        Assert.Contains("尾页类型", options.Current.Genres);
        using var query = harness.Library.ObserveLibrary("movies", new() { Genres = ["尾页类型"] },
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.Equal("movie-500", Assert.Single(query.Items).Id);
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task CancellingSparseScanRetainsCompletedPagesForTheNextSelection()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var harness = new LibraryHarness(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)) return Items([View("movies", "movies")]);
            var parameters = Parameters(request);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            var limit = int.Parse(parameters["Limit"], System.Globalization.CultureInfo.InvariantCulture);
            Interlocked.Increment(ref calls);
            if (offset == 500) { blocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Items(Enumerable.Range(offset, limit).Select(index => Movie("movie-" + index) with { Genres = ["剧情"] }).ToArray(), 1000);
        });
        using var first = harness.Library.ObserveLibrary("movies", new() { Genres = ["动画"] }, scopeToken: scope.Token);
        await blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        scope.Cancel();
        using var next = harness.Library.ObserveLibrary("movies", new() { Genres = ["剧情"] }, scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(next);
        Assert.Null(next.Error);
        Assert.Equal(60, next.Items.Length);
        Assert.Equal("movie-0", next.Items[0].Id);
        Assert.Equal(3, calls);
        Assert.False(first.IsInitialized);
        Assert.Empty(first.Items);
    }

    [Fact]
    public async Task PartiallySupportedCombinationRestartsUnfilteredCursorWithoutMissingMatches()
    {
        EmbyItem[] all =
        [
            Movie("first") with { Genres = ["Drama"], ProductionYear = 2024 },
            Movie("other-genre") with { Genres = ["Comedy"], ProductionYear = 2024 },
            Movie("wrong-year") with { Genres = ["Drama"], ProductionYear = 1990 },
            Movie("last") with { Genres = ["Drama"], ProductionYear = 2024 },
        ];
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)) return Task.FromResult(Items([View("movies", "movies")]));
            var parameters = Parameters(request);
            var offset = int.Parse(parameters["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            var limit = int.Parse(parameters["Limit"], System.Globalization.CultureInfo.InvariantCulture);
            var rows = parameters.ContainsKey("Genres") ? all.Where(item => item.Genres!.Contains("Drama")).ToArray() : all;
            return Task.FromResult(Items(rows.Skip(offset).Take(limit).ToArray(), rows.Length));
        });
        using var query = harness.Library.ObserveLibrary("movies", new() { Genres = ["Drama"], Years = [2024] }, 1,
            TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        while (query.HasMore)
        {
            await query.LoadMoreAsync(TestContext.Current.CancellationToken);
            Assert.Null(query.Error);
        }
        Assert.Equal(["first", "last"], query.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task SharedScansAreIsolatedBySortAndAccount()
    {
        var scans = 0;
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)) return Task.FromResult(Items([View("movies", "movies")]));
            var parameters = Parameters(request);
            if (!parameters.ContainsKey("Genres")) Interlocked.Increment(ref scans);
            var account = request.RequestUri.AbsolutePath.Contains("/new-user/", StringComparison.Ordinal) ? "new" : "old";
            EmbyItem[] rows = [Movie(account + "-b") with { Genres = ["Drama"] }, Movie(account + "-a") with { Genres = ["Drama"] }, Movie("other")];
            if (parameters["SortBy"] == "SortName") Array.Reverse(rows);
            return Task.FromResult(Items(rows, rows.Length));
        });
        using var first = harness.Library.ObserveLibrary("movies", new() { Genres = ["Drama"] }, scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(first);
        Assert.Equal(["old-b", "old-a"], first.Items.Select(item => item.Id));
        using var sorted = harness.Library.ObserveLibrary("movies", new() { Genres = ["Drama"], Sort = LibrarySort.Name },
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(sorted);
        Assert.Equal(["old-a", "old-b"], sorted.Items.Select(item => item.Id));
        harness.Accounts.Set(LibraryHarness.NewAccount("new-user"));
        using var changed = harness.Library.ObserveLibrary("movies", new() { Genres = ["Drama"] }, scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(changed);
        Assert.Equal(["new-b", "new-a"], changed.Items.Select(item => item.Id));
        Assert.Equal(3, scans);
    }

    [Fact]
    public async Task RefreshingUnfilteredLibraryInvalidatesPreviousFilterScan()
    {
        var revised = false;
        using var harness = new LibraryHarness((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)
                ? Items([View("movies", "movies")])
                : Items([Movie("changing") with { Genres = [Volatile.Read(ref revised) ? "Comedy" : "Drama"] }, Movie("other")], 2)));
        using var filtered = harness.Library.ObserveLibrary("movies", new() { Genres = ["Drama"] }, scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(filtered);
        Assert.Single(filtered.Items);
        using var all = harness.Library.ObserveLibrary("movies", new(), scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(all);
        Volatile.Write(ref revised, true);
        await all.RefreshAsync(TestContext.Current.CancellationToken);
        using var changed = harness.Library.ObserveLibrary("movies", new() { Genres = ["Drama"] }, scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(changed);
        Assert.Null(changed.Error);
        Assert.Empty(changed.Items);
        Assert.False(changed.HasMore);
    }

    [Fact]
    public async Task CancelledFilterObservationDoesNotReceiveProgressFromOtherReaders()
    {
        using var harness = new LibraryHarness((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/Items/Filters", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
                : request.RequestUri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)
                    ? Items([View("movies", "movies")]) : Items([Movie("one") with { Genres = ["new-option"] }], 1)));
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var filters = harness.Library.ObserveFilters("movies", scope.Token);
        await filters.RefreshAsync(TestContext.Current.CancellationToken);
        harness.Scheduler.Drain();
        var callbacks = 0;
        filters.Updated += (_, _) => callbacks++;
        scope.Cancel();
        using var library = harness.Library.ObserveLibrary("movies", new(), scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(library);
        harness.Scheduler.Drain();
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task CancellingFilteredPagingStopsScanningAndDoesNotPublishLateMatches()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offsets = new ConcurrentQueue<int>();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var harness = new LibraryHarness((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Items([View("movies", "movies")]));
            var offset = int.Parse(Parameters(request)["StartIndex"], System.Globalization.CultureInfo.InvariantCulture);
            offsets.Enqueue(offset);
            if (offset == 1) { started.TrySetResult(); return response.Task; }
            return Task.FromResult(Items([Movie("movie-" + offset) with { Genres = ["动画"] }], 3));
        });
        using var query = harness.Library.ObserveLibrary("movies", new() { Genres = ["动画"] }, 1, scope.Token);
        await InitializedAsync(query);
        Assert.Equal("movie-0", Assert.Single(query.Items).Id);
        var loading = query.LoadMoreAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        scope.Cancel();
        response.SetResult(Items([Movie("late-match") with { Genres = ["动画"] }], 3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Equal("movie-0", Assert.Single(query.Items).Id);
        Assert.Equal([0, 1], offsets.ToArray());
        Assert.Null(query.Error);
    }

    [Fact]
    public async Task PhraseSearchDoesNotPublishBroadTokenMatches()
    {
        using var harness = new LibraryHarness((request, _) =>
        {
            var phrase = Parameters(request)["SearchTerm"] == "\"斗破苍穹\"";
            EmbyItem[] rows = phrase ? [Movie("exact") with { Name = "斗破苍穹" }] :
                [Movie("unrelated") with { Name = "苍天有泪" }, Movie("exact") with { Name = "斗破苍穹" }];
            return Task.FromResult(Items(rows, rows.Length));
        });
        using var query = harness.Library.ObserveSearch("shows", "  “斗破苍穹”！  ",
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.Equal("exact", Assert.Single(query.Items).Id);
        Assert.False(query.HasMore);
    }

    [Fact]
    public async Task PunctuationOnlySearchInitializesEmptyWithoutHttp()
    {
        using var harness = new LibraryHarness((_, _) => throw new InvalidOperationException("空搜索不应启动 HTTP。"));
        using var query = harness.Library.ObserveSearch("shows", "  “！？……”  ",
            scopeToken: TestContext.Current.CancellationToken);
        await InitializedAsync(query);
        Assert.Null(query.Error);
        Assert.True(query.IsInitialized);
        Assert.Empty(query.Items);
        Assert.False(query.HasMore);
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
