using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;
using Mambo.Core.Subtitles;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class LocalSubtitleTargetResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static PlaybackEntry Context => new("episode-1", "合成剧 S01E01")
    { SeriesId = "series", SeriesName = "合成剧", SeasonId = "season", SeasonNumber = 1, EpisodeNumber = 1 };

    [Fact]
    public async Task ReadsCompleteServerCappedPagesForEpisodes801And1001()
    {
        var offsets = new List<int>();
        using var fixture = new Fixture(request =>
        {
            var query = Query(request);
            Assert.Equal("season", query["ParentId"]);
            var start = int.Parse(query["StartIndex"], CultureInfo.InvariantCulture);
            offsets.Add(start);
            return Page(Enumerable.Range(start + 1, Math.Min(200, 1001 - start)).Select(number => Episode(number)), 1001);
        });
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context,
            [new(0, "801.srt", 2), new(1, "第1001集.ass", 2)], Token);
        Assert.Equal("episode-801", result[0]);
        Assert.Equal("episode-1001", result[1]);
        Assert.Equal([0, 200, 400, 600, 800, 1000], offsets);
    }

    [Fact]
    public async Task UnknownTotalContinuesPastShortPagesToTheEmptyPage()
    {
        var offsets = new List<int>();
        using var fixture = new Fixture(request =>
        {
            var start = int.Parse(Query(request)["StartIndex"], CultureInfo.InvariantCulture);
            offsets.Add(start);
            return Page(start < 4 ? [Episode(start + 1), Episode(start + 2)] : [], null);
        });
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context, [new(0, "04.srt", 1)], Token);
        Assert.Equal("episode-4", result[0]);
        Assert.Equal([0, 2, 4], offsets);
    }

    [Fact]
    public async Task ExplicitOtherSeasonAndSpecialsUseSeriesMetadata()
    {
        using var fixture = new Fixture(request =>
        {
            Assert.Equal("series", Query(request)["ParentId"]);
            return Page([Episode(1), Episode(1, season: 2), Episode(1, season: 0)], 3);
        });
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context,
            [new(0, "S02E01.ass", 2), new(1, "S00E01.ass", 2)], Token);
        Assert.Equal("episode-2-1", result[0]);
        Assert.Equal("episode-0-1", result[1]);
    }

    [Fact]
    public async Task ExactOtherSeriesCanBeMatchedButMissingSeasonDoesNotBorrowCurrentSeason()
    {
        var searchCount = 0;
        using var fixture = new Fixture(request =>
        {
            var query = Query(request);
            if (query["IncludeItemTypes"] == "Series")
            {
                searchCount++;
                Assert.Equal("Other Show", query["SearchTerm"]);
                return Page([new() { Id = "other-series", Name = "Other_Show", Type = "Series" }], 1);
            }
            Assert.Equal("other-series", query["ParentId"]);
            return Page([Episode(3, series: "other-series", season: 2)], 1);
        });
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context,
            [new(0, "Other.Show.S02E03.chs.ass", 2), new(1, "Other.Show.第03集.ass", 2)], Token);
        Assert.Equal("other-series-2-3", Assert.Single(result).Value);
        Assert.Equal(1, searchCount);
    }

    [Fact]
    public async Task SearchRequiresUniquenessAcrossAllPagesAndNeverFallsBackToCurrentSeries()
    {
        var offsets = new List<int>();
        using var fixture = new Fixture(request =>
        {
            var query = Query(request);
            Assert.Equal("Series", query["IncludeItemTypes"]);
            var start = int.Parse(query["StartIndex"], CultureInfo.InvariantCulture);
            offsets.Add(start);
            return Page([new() { Id = "remake-" + start, Name = "Other Show", Type = "Series" }], 2);
        });
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, Context, [new(0, "Other.Show.S01E01.ass", 1)], Token));
        Assert.Equal([0, 1], offsets);
    }

    [Fact]
    public async Task TranslatedOrUnknownNamesAreNotFuzzyMatches()
    {
        using var fixture = new Fixture(_ => Page([new() { Id = "series", Name = "合成剧", Type = "Series" }], 1));
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, Context, [new(0, "Synthetic.Show.S01E01.ass", 1)], Token));
    }

    [Fact]
    public async Task SameCoordinateWithDifferentIdsIsAmbiguousButRepeatedSameIdIsNot()
    {
        using var fixture = new Fixture(_ => Page([Episode(1), Episode(1), Episode(2), Episode(2) with { Id = "alternate-2" }], 4));
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context,
            [new(0, "01.srt", 2), new(1, "02.srt", 2)], Token);
        Assert.Equal("episode-1", Assert.Single(result).Value);
    }

    [Fact]
    public async Task IncompletePaginationAndWrongSeriesCannotCreateAssociations()
    {
        using var incomplete = new Fixture(_ => Page([Episode(1)], 1001));
        Assert.Empty(await incomplete.Resolver.ResolveAsync(incomplete.Account, Context, [new(0, "01.srt", 1)], Token));
        using var wrong = new Fixture(_ => Page([Episode(1, series: "wrong"), Episode(1) with { IndexNumber = null }], 2));
        Assert.Empty(await wrong.Resolver.ResolveAsync(wrong.Account, Context, [new(0, "01.srt", 1)], Token));
    }

    [Fact]
    public async Task SingleUnnumberedFileUsesCapturedItemButBatchAndExplicitNumbersDoNot()
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("此用例不应请求服务器。"));
        var movie = new PlaybackEntry("movie", "合成电影");
        Assert.Equal("movie", (await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "字幕.srt", 1)], Token))[0]);
        Assert.Equal("movie", (await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "合成电影.ass", 1)], Token))[0]);
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "字幕.srt", 2)], Token));
        Assert.Equal("movie", (await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "test.srt", 1)], Token))[0]);
        Assert.Equal("movie", (await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "My Captions.ass", 1)], Token))[0]);
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "01.srt", 1)], Token));
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "S02.ass", 1)], Token));
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, movie, [new(0, "S01E02E03.ass", 1)], Token));
    }

    [Theory]
    [InlineData("01+03.ass")]
    [InlineData("01&03.chs.ass")]
    [InlineData("01,03.ass")]
    [InlineData("01、03.chs.ass")]
    [InlineData("[ABCDEF12].chs.ass")]
    [InlineData("1080p.chs.ass")]
    [InlineData("4k.WEB-DL.zh-Hant.srt")]
    public async Task AmbiguousNamesCannotUseSingleFileCurrentItemFallback(string fileName)
    {
        var calls = 0;
        using var fixture = new Fixture(_ => { calls++; return Page([Episode(1)], 1); });
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, Context, [new(0, fileName, 1)], Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("A+B", "AB")]
    [InlineData("AB", "A+B")]
    [InlineData("A-B", "AB")]
    [InlineData("A(B)", "AB")]
    public async Task DifferentMeaningfulSymbolsRequireExactOtherSeriesSearch(string currentTitle, string fileTitle)
    {
        var searches = 0;
        using var fixture = new Fixture(request =>
        {
            var query = Query(request);
            if (query["IncludeItemTypes"] == "Series")
            {
                searches++;
                Assert.Equal(fileTitle, query["SearchTerm"]);
                // 只有当前剧的结果，不能把不等价标题认作另一前缀的精确结果。
                return Page([new() { Id = "series", Name = currentTitle, Type = "Series" }], 1);
            }
            return Page([Episode(2)], 1);
        });
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context with { SeriesName = currentTitle },
            [new(0, fileTitle + ".S01E02.ass", 1)], Token);
        Assert.Empty(result);
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task MatchingSymbolicTitleStillUsesCurrentSeriesWithoutSearch()
    {
        using var fixture = new Fixture(request =>
        {
            var query = Query(request);
            Assert.Equal("Episode", query["IncludeItemTypes"]);
            Assert.Equal("season", query["ParentId"]);
            return Page([Episode(2)], 1);
        });
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context with { SeriesName = "A+B" },
            [new(0, "A+B.S01E02.ass", 1)], Token);
        Assert.Equal("episode-2", Assert.Single(result).Value);
    }

    [Theory]
    [InlineData("任意其它标题.ass")]
    [InlineData("A+B.srt")]
    public async Task UnknownUnnumberedSingleFileStillUsesCurrentButBatchDoesNot(string fileName)
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("无编号名称不应搜索猜测归属。"));
        Assert.Equal(Context.ItemId, (await fixture.Resolver.ResolveAsync(fixture.Account, Context, [new(0, fileName, 1)], Token))[0]);
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, Context, [new(0, fileName, 2)], Token));
    }

    [Fact]
    public async Task MissingSeasonContextDoesNotAssumeSeasonOne()
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("没有季信息时不猜测。"));
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, Context with { SeasonId = null, SeasonNumber = null },
            [new(0, "03.srt", 1)], Token));
    }

    [Fact]
    public async Task NetworkFailureSkipsOnlyTargetsThatNeedTheRequest()
    {
        using var fixture = new Fixture(_ => new(HttpStatusCode.ServiceUnavailable));
        var result = await fixture.Resolver.ResolveAsync(fixture.Account, Context,
            [new(0, "字幕.srt", 1), new(1, "02.srt", 2)], Token);
        Assert.Equal("episode-1", Assert.Single(result).Value);
    }

    [Fact]
    public async Task FailedMetadataLookupIsNotRepeatedForEveryFileInTheBatch()
    {
        var calls = 0;
        using var fixture = new Fixture(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable); });
        Assert.Empty(await fixture.Resolver.ResolveAsync(fixture.Account, Context,
            [new(0, "01.srt", 3), new(1, "02.srt", 3), new(2, "03.srt", 3)], Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledAccountStopsResolution()
    {
        using var fixture = new Fixture(_ => Page([], 0));
        fixture.Account.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(fixture.Account, Context,
            [new(0, "字幕.srt", 1)], Token));
    }

    private static EmbyItem Episode(int number, string series = "series", int season = 1) => new()
    {
        Id = series == "series" ? season == 1 ? "episode-" + number : "episode-" + season + "-" + number : series + "-" + season + "-" + number,
        Name = "合成单集", Type = "Episode", SeriesId = series, SeasonId = season == 1 ? "season" : "season-" + season,
        IndexNumber = number, ParentIndexNumber = season,
    };
    private static HttpResponseMessage Page(IEnumerable<EmbyItem> rows, int? count) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new EmbyItems { Items = rows.ToArray(), TotalRecordCount = count }, EmbyJsonContext.Default.EmbyItems)),
    };
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "", StringComparer.Ordinal);

    private sealed class Fixture : IDisposable
    {
        private readonly EmbyApi api;
        public AccountSession Account { get; } = new(new("https://" + Guid.NewGuid().ToString("N") + ".invalid", Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), "合成用户", Guid.NewGuid().ToString("N")));
        public LocalSubtitleTargetResolver Resolver { get; }
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> response)
        { api = new(Guid.NewGuid(), new Handler(response)); Resolver = new(api); }
        public void Dispose() { api.Dispose(); Account.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
}
