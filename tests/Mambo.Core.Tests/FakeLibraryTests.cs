using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class FakeLibraryTests
{
    [Fact]
    public void CatalogIsDeterministicAndContainsPerformanceLibraryAndTwelveEpisodeSeasons()
    {
        var first = new DemoCatalog();
        var second = new DemoCatalog();
        Assert.Equal(3, first.Libraries.Length);
        Assert.Equal(5000, first.AllItems.Count(item => item.LibraryId == DemoCatalog.MoviesLibraryId));
        Assert.Equal(first.AllItems.Select(item => (item.Id, item.Name, item.RunTimeTicks, item.UserData)),
            second.AllItems.Select(item => (item.Id, item.Name, item.RunTimeTicks, item.UserData)));
        var episodes = first.AllItems.Where(item => item.SeasonId == "demo-season-001-01").ToArray();
        Assert.Equal(12, episodes.Length);
        Assert.Equal(Enumerable.Range(1, 12), episodes.Select(item => item.IndexNumber!.Value));
        Assert.All(episodes, episode => Assert.Equal("demo-series-001", episode.SeriesId));
        Assert.NotEmpty(first.Find("demo-movie-0001")!.People);
        Assert.Null(first.Find("unknown"));
    }

    [Fact]
    public async Task WholePerformanceLibraryLoadsWithoutDuplicatesAndRefreshReplacesPages()
    {
        var service = Create();
        using var query = service.ObserveLibrary(DemoCatalog.MoviesLibraryId,
            new LibraryQuery { Sort = LibrarySort.Name, Direction = SortDirection.Ascending }, 500,
            TestContext.Current.CancellationToken);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        while (query.HasMore) await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5000, query.Items.Length);
        Assert.Equal(5000, query.TotalCount);
        Assert.Equal(5000, query.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("demo-movie-0001", query.Items[0].Id);
        Assert.Equal("demo-movie-5000", query.Items[^1].Id);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(500, query.Items.Length);
        Assert.True(query.HasMore);
    }

    [Fact]
    public async Task FilterGroupsUseOrWithinGroupAndAndAcrossGroups()
    {
        var catalog = new DemoCatalog();
        var service = Create(catalog);
        var filter = new LibraryQuery
        {
            Genres = ["剧情", "科幻"], Years = [2001, 2002], OfficialRatings = ["PG-13"],
            Sort = LibrarySort.Name, Direction = SortDirection.Ascending,
        };
        using var query = service.ObserveLibrary(DemoCatalog.MoviesLibraryId, filter, 500,
            TestContext.Current.CancellationToken);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        var expected = catalog.AllItems.Where(item => item.Kind == MediaKind.Movie &&
            item.Genres.Any(genre => genre is "剧情" or "科幻") && item.ProductionYear is 2001 or 2002 &&
            item.OfficialRating == "PG-13").Select(item => item.Id).Order(StringComparer.Ordinal);
        Assert.NotEmpty(query.Items);
        Assert.Equal(expected, query.Items.Select(item => item.Id));
        using var options = service.ObserveFilters(DemoCatalog.MoviesLibraryId, TestContext.Current.CancellationToken);
        await options.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(26, options.Current!.Years.Length);
        Assert.Contains("科幻", options.Current.Genres);
    }

    [Fact]
    public async Task NextUpSeasonsAndEpisodesAgreeOnSharedIds()
    {
        var service = Create();
        using var next = service.ObserveNextUp("demo-series-001", TestContext.Current.CancellationToken);
        using var seasons = service.ObserveSeasons("demo-series-001", TestContext.Current.CancellationToken);
        await Task.WhenAll(next.RefreshAsync(TestContext.Current.CancellationToken),
            seasons.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal("demo-episode-001-01-03", next.Current!.Id);
        Assert.Equal(3, seasons.Current.Length);
        using var episodes = service.ObserveEpisodes(seasons.Current[0].Id, 5, TestContext.Current.CancellationToken);
        await episodes.LoadMoreAsync(TestContext.Current.CancellationToken);
        while (episodes.HasMore) await episodes.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(12, episodes.TotalCount);
        Assert.Contains(next.Current.Id, episodes.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task ContinueWatchingDeduplicatesEpisodesOfSameSeries()
    {
        var service = Create();
        using var query = service.ObserveContinueWatching(TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        var episodes = query.Current.Where(item => item.Kind == MediaKind.Episode).ToArray();
        Assert.Equal(3, episodes.Length);
        Assert.Equal(3, episodes.Select(item => item.SeriesId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(episodes, item => Assert.Equal(4, item.IndexNumber));
    }

    [Fact]
    public async Task SearchNormalizesFullWidthTextAndFoldsEpisodeMatchesIntoSeries()
    {
        var service = Create();
        using var movie = service.ObserveSearch(DemoCatalog.MoviesLibraryId, " 电影０００１！ ",
            scopeToken: TestContext.Current.CancellationToken);
        await movie.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal("demo-movie-0001", Assert.Single(movie.Items).Id);
        using var episodes = service.ObserveSearch(DemoCatalog.ShowsLibraryId, "第 ０１ 集",
            scopeToken: TestContext.Current.CancellationToken);
        await episodes.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, episodes.Items.Length);
        Assert.All(episodes.Items, item => Assert.Equal(MediaKind.Series, item.Kind));
        using var empty = service.ObserveSearch(DemoCatalog.ShowsLibraryId, "!@#$",
            scopeToken: TestContext.Current.CancellationToken);
        await empty.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.True(empty.IsInitialized);
        Assert.Empty(empty.Items);
        Assert.False(empty.HasMore);
    }

    [Fact]
    public async Task InvalidReadSetsContractErrorAndConfiguredFailuresStayIndependent()
    {
        var service = Create();
        using var missing = service.ObserveDetail("unknown", TestContext.Current.CancellationToken);
        await missing.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(missing.IsInitialized);
        Assert.Equal(AppErrorKind.Contract, missing.Error?.Kind);
        using var good = service.ObserveDetail("demo-movie-0001", TestContext.Current.CancellationToken);
        await good.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(good.IsInitialized);
        Assert.Null(good.Error);

        var failing = Create(options: new FakeOptions { Delay = TimeSpan.Zero, FailureRate = 1 });
        using var query = failing.ObserveLibraries(TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AppErrorKind.Network, query.Error?.Kind);
        Assert.False(query.IsInitialized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void InvalidPageSizesAreRejected(int pageSize)
    {
        var service = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => service.ObserveLibrary(DemoCatalog.MoviesLibraryId, new(), pageSize,
            TestContext.Current.CancellationToken));
    }

    private static FakeLibraryService Create(DemoCatalog? catalog = null, FakeOptions? options = null) =>
        new(catalog ?? new DemoCatalog(), new FakeReadScheduler(),
            new FakeOperation(options ?? new FakeOptions { Delay = TimeSpan.Zero }));
}
