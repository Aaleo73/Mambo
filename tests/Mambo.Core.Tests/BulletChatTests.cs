using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using Mambo.Core.BulletChat;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class BulletChatTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 解析 ----

    [Fact]
    public void ParserMapsModesMasksColorAndSortsStably()
    {
        var parsed = BulletChatParser.Parse(
        [
            new() { P = "12.50,1,16777215,a", M = "滚动" },
            new() { P = "3.00,5,4278190335,b", M = "顶部" },
            new() { P = "3.00,4,255,c", M = "底部" },
            new() { P = "3.00,2,0,d", M = "同时刻保持原顺序" },
        ]);
        Assert.Equal(["顶部", "底部", "同时刻保持原顺序", "滚动"], parsed.Select(comment => comment.Text));
        Assert.Equal([BulletChatMode.Top, BulletChatMode.Bottom, BulletChatMode.Scroll, BulletChatMode.Scroll], parsed.Select(comment => comment.Mode));
        Assert.Equal(0x0000FFu, parsed[0].Rgb);
        Assert.Equal(12.5, parsed[3].TimeSeconds);
    }

    [Fact]
    public void ParserDropsInvalidRowsAndCleansText()
    {
        var parsed = BulletChatParser.Parse(
        [
            new() { P = "1,1", M = "字段不够" }, new() { P = "-1,1,0,a", M = "负时间" }, new() { P = "x,1,0,a", M = "非数字" },
            new() { P = "1,7,0,a", M = "高级弹幕" }, new() { P = "1,1,0,a", M = "   " }, new() { P = null, M = "空" }, new() { P = "1,1,0,a", M = null },
            new() { P = "2,1,0,a", M = " 第一行\n第二行\t" }, new() { P = "3,1,0,a", M = new string('长', 500) },
        ]);
        Assert.Equal(2, parsed.Length);
        Assert.Equal("第一行 第二行", parsed[0].Text);
        Assert.Equal(120, parsed[1].Text.Length);
    }

    [Fact]
    public void ParserSamplesEvenlyWhenOverLimit()
    {
        var comments = Enumerable.Range(0, BulletChatParser.MaximumComments * 2)
            .Select(index => new DandanComment { P = index.ToString(CultureInfo.InvariantCulture) + ",1,0,a", M = "x" }).ToList();
        var parsed = BulletChatParser.Parse(comments);
        Assert.Equal(BulletChatParser.MaximumComments, parsed.Length);
        Assert.Equal(0, parsed[0].TimeSeconds);
        Assert.True(parsed[^1].TimeSeconds >= comments.Count - 2, "抽样应覆盖到结尾");
    }

    // ---- 匹配 ----

    [Theory]
    [InlineData("葬送的芙莉莲", "葬送的芙莉莲", null)]
    [InlineData("葬送的芙莉莲 第二季", "葬送的芙莉莲", 2)]
    [InlineData("某作品 第十二期", "某作品", 12)]
    [InlineData("Some Show Season 3", "Some Show", 3)]
    [InlineData("Some Show 2nd Season", "Some Show", 2)]
    [InlineData("Some Show S2", "Some Show", 2)]
    [InlineData("某作品 Ⅲ", "某作品", 3)]
    [InlineData("一人之下5", "一人之下", 5)]
    [InlineData("86", "86", null)]
    [InlineData("第二季", "第二季", null)]
    public void SplitSeasonRecognisesMarkers(string title, string expectedTitle, int? expectedSeason)
    {
        var (rest, season) = BulletChatMatcher.SplitSeason(title);
        Assert.Equal(expectedTitle, rest);
        Assert.Equal(expectedSeason, season);
    }

    [Fact]
    public void RankPrefersUnmarkedEntryForFirstSeasonAndMarkedEntryForSecond()
    {
        DandanAnime[] candidates = [Anime(18886, "葬送的芙莉莲 第二季", start: "2026-01-16"), Anime(17617, "葬送的芙莉莲", start: "2023-09-29")];
        Assert.Equal(17617, BulletChatMatcher.Rank(new("葬送的芙莉莲", 1, 5, 2023), candidates)[0].AnimeId);
        var second = BulletChatMatcher.Rank(new("葬送的芙莉莲", 2, 3, 2026), candidates);
        Assert.Equal(18886, second[0].AnimeId);
        // 年份对不上的无标记条目不可能是第二季。
        Assert.Single(second);
    }

    [Fact]
    public void RankUsesYearWhenLaterSeasonHasSubtitleInsteadOfNumber()
    {
        DandanAnime[] candidates = [Anime(1, "某某战记", start: "2023-04-01"), Anime(2, "某某战记 远征篇", start: "2024-04-01")];
        Assert.Equal(2, BulletChatMatcher.Rank(new("某某战记", 2, 1, 2024), candidates)[0].AnimeId);
        Assert.Equal(1, BulletChatMatcher.Rank(new("某某战记", 1, 1, 2023), candidates)[0].AnimeId);
    }

    [Fact]
    public void RankSeparatesMoviesFromSeriesAndRejectsUnrelatedTitles()
    {
        DandanAnime[] candidates =
        [
            Anime(16063, "孤独摇滚！", start: "2022-10-09"), Anime(18039, "孤独摇滚！ 剧场总集篇", type: "movie", start: "2024-06-07"),
            Anime(9, "完全无关的另一部作品", start: "2022-10-09"),
        ];
        var series = BulletChatMatcher.Rank(new("孤独摇滚！", 1, 3, 2022), candidates);
        Assert.Equal([16063L], series.Select(anime => anime.AnimeId));
        var movie = BulletChatMatcher.Rank(new("孤独摇滚！ 剧场总集篇", null, null, 2024), candidates);
        Assert.Equal(18039, movie[0].AnimeId);
        Assert.DoesNotContain(movie, anime => anime.AnimeId == 9);
    }

    [Fact]
    public void PickEpisodeUsesNumberSkipsSpecialsAndHandlesContinuedNumbering()
    {
        DandanEpisode[] first = [Episode(1003, "3"), Episode(1001, "1"), Episode(1901, "S1"), Episode(1002, "2"), Episode(1991, "C1")];
        var plain = BulletChatMatcher.PickEpisode(new("x", 1, 2, null), first);
        Assert.Equal(1002, plain!.Episode.EpisodeId);
        Assert.Equal(0, plain.Offset);
        Assert.Null(BulletChatMatcher.PickEpisode(new("x", 1, 9, null), first));

        // 第二季续接第一季编号（29、30、31…），Emby 的 S02E03 是其中第 3 个。
        DandanEpisode[] continued = [Episode(2001, "29"), Episode(2002, "30"), Episode(2003, "31"), Episode(2901, "C1")];
        var third = BulletChatMatcher.PickEpisode(new("x", 2, 3, null), continued);
        Assert.Equal(2003, third!.Episode.EpisodeId);
        Assert.Equal(0, third.Offset);
        // Emby 用绝对集号时按编号取。
        var absolute = BulletChatMatcher.PickEpisode(new("x", 1, 30, null), continued);
        Assert.Equal(2002, absolute!.Episode.EpisodeId);
        Assert.Equal(1 - 29, absolute.Offset);
        Assert.Equal(2003, BulletChatMatcher.PickByOffset(31, absolute.Offset, continued)!.EpisodeId);
        Assert.Null(BulletChatMatcher.PickByOffset(40, absolute.Offset, continued));

        Assert.Equal(1001, BulletChatMatcher.PickEpisode(new("电影", null, null, null), first)!.Episode.EpisodeId);
    }

    [Fact]
    public void BestMatchRequiresAClearWinner()
    {
        DandanMatch[] guesses =
        [
            new() { EpisodeId = 176170003, AnimeId = 17617, AnimeTitle = "葬送的芙莉莲", Type = "tvseries" },
            new() { EpisodeId = 188860003, AnimeId = 18886, AnimeTitle = "葬送的芙莉莲 第二季", Type = "tvseries" },
            new() { EpisodeId = 307690003, AnimeId = 30769, AnimeTitle = "关于莉莉周的一切", Type = "tvseries" },
        ];
        Assert.Equal(188860003, BulletChatMatcher.BestMatch(new("葬送的芙莉莲", 2, 3, null), guesses)!.EpisodeId);
        DandanMatch[] ambiguous = [new() { EpisodeId = 1, AnimeId = 1, AnimeTitle = "同名作品" }, new() { EpisodeId = 2, AnimeId = 2, AnimeTitle = "同名作品" }];
        Assert.Null(BulletChatMatcher.BestMatch(new("同名作品", 1, 1, null), ambiguous));
        Assert.Null(BulletChatMatcher.BestMatch(new("同名作品", 1, 1, null), []));
    }

    [Fact]
    public void TargetIgnoresSpecialsAndEpisodesWithoutNumber()
    {
        Assert.Null(BulletChatTarget.From(new PlaybackEntry("a", "t") { SeriesName = "剧", SeasonNumber = 0, EpisodeNumber = 1 }));
        Assert.Null(BulletChatTarget.From(new PlaybackEntry("a", "t") { SeriesName = "剧", SeasonNumber = 1 }));
        var movie = BulletChatTarget.From(new PlaybackEntry("a", " 电影名 ") { ProductionYear = 2020 });
        Assert.True(movie!.IsMovie);
        Assert.Equal("电影名", movie.Title);
        Assert.Equal(2020, movie.Year);
    }

    // ---- 自动匹配端到端（对假服务器） ----

    [Fact]
    public async Task AutoMatchLoadsCommentsRemembersSeasonAndSkipsSearchForNextEpisode()
    {
        using var sandbox = new Sandbox();
        using var server = FrierenServer();
        using var client = new DandanplayClient(server);
        using var history = new BulletChatHistory(sandbox.Paths);
        var provider = new DandanplayBulletChatProvider(client, history, () => "scope");

        var third = await provider.ResolveAsync(EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026), Token);
        Assert.Equal("188860003", third.Episode!.Id);
        Assert.Equal("葬送的芙莉莲 第二季", third.Episode.AnimeTitle);
        Assert.Equal(2, third.Comments.Length);
        Assert.Contains(server.Requests, request => request.StartsWith("GET /api/v2/search/anime", StringComparison.Ordinal));

        server.Requests.Clear();
        var fourth = await provider.ResolveAsync(EpisodeEntry("item-4", "葬送的芙莉莲", 2, 4, 2026), Token);
        Assert.Equal("188860004", fourth.Episode!.Id);
        Assert.DoesNotContain(server.Requests, request => request.Contains("/search/", StringComparison.Ordinal) || request.Contains("/match", StringComparison.Ordinal));

        // 记忆落盘，重启后仍然有效。
        using var reopened = new BulletChatHistory(sandbox.Paths);
        var memory = reopened.Find(BulletChatHistory.SeasonKey("scope", "series-1", 2));
        Assert.Equal("18886", memory!.AnimeId);
        Assert.False(memory.Manual);
    }

    [Fact]
    public async Task EpisodeBeyondEntryRollsOverToSequel()
    {
        using var sandbox = new Sandbox();
        using var server = new Server();
        server.Search["分割放送"] = Animes(Anime(1, "分割放送", start: "2023-01-01", count: 12), Anime(2, "分割放送 第2部分", start: "2023-07-01", count: 12));
        server.Bangumi["1"] = Bangumi("分割放送", Enumerable.Range(1, 12).Select(number => Episode(100 + number, number.ToString(CultureInfo.InvariantCulture))).ToArray());
        server.Bangumi["2"] = Bangumi("分割放送 第2部分", Enumerable.Range(1, 12).Select(number => Episode(200 + number, number.ToString(CultureInfo.InvariantCulture))).ToArray());
        server.Comments["203"] = Comments("1,1,0,a|后半第三集");
        using var client = new DandanplayClient(server);
        using var history = new BulletChatHistory(sandbox.Paths);
        var provider = new DandanplayBulletChatProvider(client, history, () => "scope");

        var resolution = await provider.ResolveAsync(EpisodeEntry("item-15", "分割放送", 1, 15, 2023), Token);
        Assert.Equal("203", resolution.Episode!.Id);
        // 记住的是后续条目与位置偏移：第 16 集直接落到它的第 4 集。
        var memory = history.Find(BulletChatHistory.SeasonKey("scope", "series-1", 1))!;
        Assert.Equal("2", memory.AnimeId);
        Assert.Equal(-12, memory.Offset);
    }

    [Fact]
    public async Task FallsBackToFileNameMatchOnlyWhenUnambiguous()
    {
        using var sandbox = new Sandbox();
        using var server = new Server();
        server.Matches = """[{"episodeId":5003,"animeId":50,"animeTitle":"罗马音片名","episodeTitle":"第3话","type":"tvseries"}]""";
        server.Bangumi["50"] = Bangumi("罗马音片名", Episode(5001, "1"), Episode(5002, "2"), Episode(5003, "3"));
        server.Comments["5003"] = Comments("1,1,0,a|兜底命中");
        using var client = new DandanplayClient(server);
        using var history = new BulletChatHistory(sandbox.Paths);
        var provider = new DandanplayBulletChatProvider(client, history, () => "scope");

        var resolution = await provider.ResolveAsync(EpisodeEntry("item", "罗马音片名", 1, 3, null), Token);
        Assert.Equal("5003", resolution.Episode!.Id);
        Assert.Contains("POST /api/v2/match", server.Requests);
        Assert.Equal(0, history.Find(BulletChatHistory.SeasonKey("scope", "series-1", 1))!.Offset);

        server.Matches = """[{"episodeId":1,"animeId":1,"animeTitle":"撞名作品"},{"episodeId":2,"animeId":2,"animeTitle":"撞名作品"}]""";
        Assert.Null((await provider.ResolveAsync(EpisodeEntry("other", "撞名作品", 1, 1, null) with { SeriesId = "series-2" }, Token)).Episode);
    }

    [Fact]
    public async Task ManualSelectionOverridesAutoMatchAndIsFollowedByLaterEpisodes()
    {
        using var sandbox = new Sandbox();
        using var server = FrierenServer();
        using var client = new DandanplayClient(server);
        using var history = new BulletChatHistory(sandbox.Paths);
        var provider = new DandanplayBulletChatProvider(client, history, () => "scope");
        var entry = EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026);
        await provider.ResolveAsync(entry, Token);

        // 用户改选第一季的第 5 话：偏移 +2，且标记为手动。
        var chosen = (await provider.GetEpisodesAsync("17617", Token)).Single(episode => episode.Id == "176170005");
        var manual = await provider.SelectAsync(entry, chosen, Token);
        Assert.Equal("176170005", manual.Episode!.Id);
        var memory = history.Find(BulletChatHistory.SeasonKey("scope", "series-1", 2))!;
        Assert.True(memory.Manual);
        Assert.Equal(2, memory.Offset);

        // 同一条目再次播放用的是手动结果；下一集沿用偏移；自动结果不会再把它盖掉。
        Assert.Equal("176170005", (await provider.ResolveAsync(entry, Token)).Episode!.Id);
        Assert.Equal("176170006", (await provider.ResolveAsync(EpisodeEntry("item-4", "葬送的芙莉莲", 2, 4, 2026), Token)).Episode!.Id);
        await history.RememberAsync(BulletChatHistory.SeasonKey("scope", "series-1", 2), new("18886", "自动", 0, false), Token);
        Assert.True(history.Find(BulletChatHistory.SeasonKey("scope", "series-1", 2))!.Manual);
    }

    [Fact]
    public async Task MovieMatchIsRememberedPerItem()
    {
        using var sandbox = new Sandbox();
        using var server = new Server();
        server.Search["某剧场版"] = Animes(Anime(7, "某剧场版", type: "movie", start: "2020-05-01", count: 1));
        server.Bangumi["7"] = Bangumi("某剧场版", Episode(70001, "1"));
        server.Comments["70001"] = Comments("1,1,0,a|电影弹幕");
        using var client = new DandanplayClient(server);
        using var history = new BulletChatHistory(sandbox.Paths);
        var provider = new DandanplayBulletChatProvider(client, history, () => "scope");
        var movie = new PlaybackEntry("movie-item", "某剧场版") { ProductionYear = 2020 };

        Assert.Equal("70001", (await provider.ResolveAsync(movie, Token)).Episode!.Id);
        server.Requests.Clear();
        Assert.Equal("70001", (await provider.ResolveAsync(movie, Token)).Episode!.Id);
        Assert.Equal(["GET /api/v2/comment/70001?withRelated=true&chConvert=0"], server.Requests);
    }

    // ---- 客户端与缓存 ----

    [Fact]
    public async Task RequestsIdentifyAsMamboAndCarryNoCredentials()
    {
        using var server = FrierenServer();
        using var client = new DandanplayClient(server, version: "9.8.7");
        await client.SearchAnimeAsync("葬送的芙莉莲", Token);
        await client.MatchAsync("葬送的芙莉莲 S02E03", Token);
        Assert.All(server.UserAgents, agent => Assert.Equal("Mambo/9.8.7", agent));
        Assert.False(server.SawCredentials);
        Assert.All(server.Hosts, host => Assert.Equal(new Uri(DandanplayClient.DefaultServer).Host, host));
        // 没有真实文件：哈希只是 32 位十六进制的占位值。
        Assert.Matches("\"fileHash\":\"[0-9a-f]{32}\"", server.Bodies.Single());
        Assert.Contains("\"matchMode\":\"hashAndFileName\"", server.Bodies.Single());
    }

    [Fact]
    public async Task HttpFailuresBecomeSafeErrors()
    {
        using var server = new Server { Status = HttpStatusCode.ServiceUnavailable };
        using var client = new DandanplayClient(server);
        var unavailable = await Assert.ThrowsAsync<AppException>(() => client.SearchAnimeAsync("任意片名", Token));
        Assert.Equal(AppErrorKind.Server, unavailable.Error.Kind);
        Assert.True(unavailable.Error.Retryable);
        Assert.DoesNotContain("任意片名", unavailable.Message);

        using var broken = new Server { Raw = "not json" };
        using var second = new DandanplayClient(broken);
        Assert.Equal(ErrorCodes.InvalidResponse, (await Assert.ThrowsAsync<AppException>(() => second.SearchAnimeAsync("x", Token))).Error.Code);

        using var offline = new Server { Before = (_, _) => throw new HttpRequestException("offline") };
        using var third = new DandanplayClient(offline);
        var network = await Assert.ThrowsAsync<AppException>(() => third.CommentsAsync("1", Token));
        Assert.Equal(ErrorCodes.NetworkUnavailable, network.Error.Code);
        Assert.Null(network.InnerException);
    }

    [Fact]
    public async Task CacheServesFreshResponsesAndRefetchesAfterExpiry()
    {
        using var sandbox = new Sandbox();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        using var server = FrierenServer();
        var cache = new BulletChatCache(sandbox.Paths, clock);
        using var client = new DandanplayClient(server, cache);

        await client.CommentsAsync("188860003", Token);
        await client.CommentsAsync("188860003", Token);
        Assert.Single(server.Requests);
        clock.Advance(TimeSpan.FromHours(7));
        await client.CommentsAsync("188860003", Token);
        Assert.Equal(2, server.Requests.Count);

        // 缓存文件名不含片名或编号；清空后重新请求。
        Assert.All(Directory.EnumerateFiles(sandbox.Paths.BulletChatCache), file => Assert.DoesNotContain("188860003", Path.GetFileName(file)));
        cache.Clear();
        Assert.Empty(Directory.EnumerateFiles(sandbox.Paths.BulletChatCache));
        await client.CommentsAsync("188860003", Token);
        Assert.Equal(3, server.Requests.Count);
    }

    // ---- 服务：跟随播放 ----

    [Fact]
    public async Task ServiceFollowsPlaybackAndPublishesLoadedState()
    {
        using var harness = new Harness(FrierenServer());
        var changes = 0;
        harness.Service.Changed += (_, _) => Interlocked.Increment(ref changes);
        Assert.Equal(BulletChatStatus.Idle, harness.Service.Current.Status);

        var session = harness.Play(EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026));
        await Until(() => harness.Service.Current.Status == BulletChatStatus.Loaded);
        Assert.Equal("item-3", harness.Service.Current.ItemId);
        Assert.Equal(2, harness.Service.Current.Comments.Length);
        Assert.True(changes >= 2);

        // 进度快照不触发重新加载。
        var requests = harness.Server.Requests.Count;
        session.Set(session.Snapshot with { PositionTicks = 12345 });
        session.Set(session.Snapshot with { PositionTicks = 23456 });
        Assert.Equal(requests, harness.Server.Requests.Count);

        harness.Playback.End(session);
        Assert.Equal(BulletChatStatus.Idle, harness.Service.Current.Status);
        Assert.True(harness.Service.Current.Comments.IsEmpty);
    }

    [Fact]
    public async Task ServiceStaysIdleAndSilentWhenDisabledOrExternal()
    {
        using var harness = new Harness(FrierenServer());
        harness.Settings.Set(value => value with { BulletChat = value.BulletChat with { Enabled = false } });
        var session = harness.Play(EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026));
        await Task.Delay(100, Token);
        Assert.Equal(BulletChatStatus.Idle, harness.Service.Current.Status);
        Assert.Empty(harness.Server.Requests);

        // 播放中打开：立即开始匹配；再关掉：回到 Idle。
        harness.Settings.Set(value => value with { BulletChat = value.BulletChat with { Enabled = true } });
        await Until(() => harness.Service.Current.Status == BulletChatStatus.Loaded);
        harness.Settings.Set(value => value with { BulletChat = value.BulletChat with { Enabled = false } });
        Assert.Equal(BulletChatStatus.Idle, harness.Service.Current.Status);
        harness.Playback.End(session);

        harness.Settings.Set(value => value with { BulletChat = value.BulletChat with { Enabled = true } });
        harness.Server.Requests.Clear();
        harness.Play(EpisodeEntry("item-4", "葬送的芙莉莲", 2, 4, 2026), EngineKind.External);
        await Task.Delay(100, Token);
        Assert.Equal(BulletChatStatus.Idle, harness.Service.Current.Status);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task SwitchingEntryCancelsPreviousLoad()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        var server = FrierenServer();
        server.Before = async (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/188860003", StringComparison.Ordinal)) return;
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
        };
        using var harness = new Harness(server);
        var session = harness.Play(EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026));
        await Until(() => harness.Server.Requests.Any(request => request.Contains("/comment/188860003", StringComparison.Ordinal)));
        Assert.Equal(BulletChatStatus.Loading, harness.Service.Current.Status);

        session.Set(session.Snapshot with { Entry = EpisodeEntry("item-4", "葬送的芙莉莲", 2, 4, 2026) });
        await Until(() => harness.Service.Current.Status == BulletChatStatus.Loaded);
        Assert.Equal("item-4", harness.Service.Current.ItemId);
        Assert.Equal("188860004", harness.Service.Current.Episode!.Id);
        await Until(() => Volatile.Read(ref cancelled) == 1);
        release.SetResult();
    }

    [Fact]
    public async Task FailureIsReportedAndReloadRecovers()
    {
        var server = FrierenServer();
        server.Status = HttpStatusCode.BadGateway;
        var logged = new List<AppError>();
        using var harness = new Harness(server, logged.Add);
        harness.Play(EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026));
        await Until(() => harness.Service.Current.Status == BulletChatStatus.Failed);
        Assert.Equal(AppErrorKind.Server, harness.Service.Current.Error!.Kind);
        Assert.Single(logged);
        Assert.DoesNotContain("芙莉莲", logged[0].Message);

        server.Status = HttpStatusCode.OK;
        await harness.Service.ReloadAsync(Token);
        await Until(() => harness.Service.Current.Status == BulletChatStatus.Loaded);
    }

    [Fact]
    public async Task UnmatchedEntryReportsNotMatched()
    {
        using var harness = new Harness(new Server());
        harness.Play(EpisodeEntry("item", "库里没有的作品", 1, 1, 2020));
        await Until(() => harness.Service.Current.Status == BulletChatStatus.NotMatched);
        Assert.Null(harness.Service.Current.Episode);
        Assert.True(harness.Service.Current.Comments.IsEmpty);
    }

    [Fact]
    public async Task PreparingPhaseWaitsForFinalEntry()
    {
        using var harness = new Harness(FrierenServer());
        var session = new Session();
        session.Set(new() { Phase = PlayerPhase.Preparing, Entry = new PlaybackEntry("item-3", "正在准备播放") });
        harness.Playback.Start(session);
        await Task.Delay(100, Token);
        Assert.Empty(harness.Server.Requests);
        session.Set(new() { Phase = PlayerPhase.Opening, Entry = EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026) });
        await Until(() => harness.Service.Current.Status == BulletChatStatus.Loaded);
    }

    [Fact]
    public async Task ManualSelectThroughServiceRequiresPlaybackAndUpdatesState()
    {
        using var harness = new Harness(FrierenServer());
        var episode = new BulletChatEpisode("176170005", "17617", "葬送的芙莉莲", "第5话");
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<AppException>(() => harness.Service.SelectAsync(episode, Token))).Error.Code);
        await Assert.ThrowsAsync<AppException>(() => harness.Service.SearchAsync("  ", Token));

        harness.Play(EpisodeEntry("item-3", "葬送的芙莉莲", 2, 3, 2026));
        await Until(() => harness.Service.Current.Status == BulletChatStatus.Loaded);
        Assert.Equal(2, (await harness.Service.SearchAsync(" 葬送的芙莉莲 ", Token)).Length);
        await harness.Service.SelectAsync(episode, Token);
        Assert.Equal(BulletChatStatus.Loaded, harness.Service.Current.Status);
        Assert.Equal("176170005", harness.Service.Current.Episode!.Id);
        Assert.Equal("第一季第五话", harness.Service.Current.Comments.Single().Text);
    }

    // ---- 设置、条目、演示数据 ----

    [Fact]
    public async Task SettingsDefaultWhenMissingClampWhenInvalidAndPersist()
    {
        using var sandbox = new Sandbox();
        var device = Guid.NewGuid();
        await File.WriteAllTextAsync(sandbox.Paths.Settings, $$$"""{"Version":1,"Settings":{"DeviceId":"{{{device}}}","Volume":37}}""", Token);
        using (var settings = new SettingsStore(sandbox.Paths, new InlineScheduler()))
        {
            Assert.Equal(new BulletChatSettings(), settings.Current.BulletChat);
            Assert.True(settings.Current.BulletChat.Enabled);
            await settings.UpdateAsync(value => value with { BulletChat = value.BulletChat with { Enabled = false, Opacity = 0.4, ScrollSeconds = 10 } }, Token);
            await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { BulletChat = value.BulletChat with { Opacity = 5 } }, Token));
            await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { BulletChat = null! }, Token));
        }
        using (var restored = new SettingsStore(sandbox.Paths, new InlineScheduler()))
        {
            Assert.False(restored.Current.BulletChat.Enabled);
            Assert.Equal(0.4, restored.Current.BulletChat.Opacity);
            Assert.Equal(10, restored.Current.BulletChat.ScrollSeconds);
            Assert.Equal(37, restored.Current.Volume);
        }

        await File.WriteAllTextAsync(sandbox.Paths.Settings,
            """{"Version":1,"Settings":{"DeviceId":"<device>","Volume":37,"BulletChat":{"Enabled":true,"Opacity":9,"FontScale":0,"ScrollSeconds":-1,"Area":0.5}}}"""
                .Replace("<device>", device.ToString(), StringComparison.Ordinal), Token);
        using var repaired = new SettingsStore(sandbox.Paths, new InlineScheduler());
        Assert.Equal(new BulletChatSettings { Area = 0.5 }, repaired.Current.BulletChat);
        Assert.Equal(device, repaired.Current.DeviceId);
    }

    [Fact]
    public void PlaybackEntryCarriesProductionYear()
    {
        var direct = SeasonPlan.ToEntry(new EmbyItem { Id = "1001", Name = "集", Type = "Episode", SeriesName = "剧", ParentIndexNumber = 2, IndexNumber = 3, ProductionYear = 2026 });
        Assert.Equal(2026, direct.ProductionYear);
        var premiere = SeasonPlan.ToEntry(new EmbyItem { Id = "1002", Name = "片", Type = "Movie", PremiereDate = new DateTimeOffset(2019, 7, 1, 0, 0, 0, TimeSpan.Zero) });
        Assert.Equal(2019, premiere.ProductionYear);
        Assert.Null(SeasonPlan.ToEntry(new EmbyItem { Id = "1003", Name = "片", Type = "Movie" }).ProductionYear);
    }

    [Fact]
    public async Task DemoProviderIsDeterministicAndCoversAllModes()
    {
        var provider = new FakeBulletChatProvider(new FakeOperation(new FakeOptions { Delay = TimeSpan.Zero }));
        var entry = new PlaybackEntry("demo-item", "演示") { DurationTicks = TimeSpan.FromMinutes(5).Ticks };
        var first = await provider.ResolveAsync(entry, Token);
        var second = await provider.ResolveAsync(entry, Token);
        Assert.Equal(first.Comments.AsEnumerable(), second.Comments.AsEnumerable());
        Assert.NotEqual(first.Comments.AsEnumerable(), (await provider.ResolveAsync(entry with { ItemId = "other" }, Token)).Comments.AsEnumerable());
        Assert.Equal(3, first.Comments.Select(comment => comment.Mode).Distinct().Count());
        Assert.True(first.Comments.All(comment => comment.TimeSeconds is > 0 and < 300));
        Assert.Equal(first.Comments.OrderBy(comment => comment.TimeSeconds).AsEnumerable(), first.Comments.AsEnumerable());
    }

    [Fact]
    public async Task HistoryIgnoresCorruptFileAndCapsOffsets()
    {
        using var sandbox = new Sandbox();
        await File.WriteAllTextAsync(sandbox.Paths.BulletChatHistory, "{ broken", Token);
        using (var broken = new BulletChatHistory(sandbox.Paths)) Assert.Null(broken.Find("any"));
        await File.WriteAllTextAsync(sandbox.Paths.BulletChatHistory,
            """{"Version":1,"Entries":{"ok":{"AnimeId":"1","AnimeTitle":"a","Offset":2,"Manual":true},"bad":{"AnimeId":"","AnimeTitle":"a","Offset":0,"Manual":false},"far":{"AnimeId":"1","AnimeTitle":"a","Offset":99999,"Manual":false}}}""", Token);
        using var history = new BulletChatHistory(sandbox.Paths);
        Assert.Equal(2, history.Find("ok")!.Offset);
        Assert.Null(history.Find("bad"));
        Assert.Null(history.Find("far"));
    }

    // ---- 辅助 ----

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("等待条件超时。");
            await Task.Delay(10, Token);
        }
    }

    private static PlaybackEntry EpisodeEntry(string itemId, string series, int season, int episode, int? year) =>
        new(itemId, string.Create(CultureInfo.InvariantCulture, $"{series} S{season:00}E{episode:00}"))
        {
            SeriesId = "series-1", SeriesName = series, SeasonNumber = season, EpisodeNumber = episode, ProductionYear = year,
        };

    private static DandanAnime Anime(long id, string title, string type = "tvseries", string start = "2020-01-01", int count = 12) => new()
    {
        AnimeId = id, BangumiId = id.ToString(CultureInfo.InvariantCulture), AnimeTitle = title, Type = type, StartDate = start + "T00:00:00", EpisodeCount = count,
    };

    private static DandanEpisode Episode(long id, string number) => new() { EpisodeId = id, EpisodeNumber = number, EpisodeTitle = "第" + number + "话" };

    private static string Animes(params DandanAnime[] animes) => "[" + string.Join(',', animes.Select(anime =>
        $$$"""{"animeId":{{{anime.AnimeId}}},"bangumiId":"{{{anime.BangumiId}}}","animeTitle":"{{{anime.AnimeTitle}}}","type":"{{{anime.Type}}}","typeDescription":"TV动画","startDate":"{{{anime.StartDate}}}","episodeCount":{{{anime.EpisodeCount}}}}""")) + "]";

    private static string Bangumi(string title, params DandanEpisode[] episodes) => $$"""{"animeTitle":"{{title}}","type":"tvseries","episodes":[""" + string.Join(',', episodes.Select(episode =>
        $$"""{"episodeId":{{episode.EpisodeId}},"episodeTitle":"{{episode.EpisodeTitle}}","episodeNumber":"{{episode.EpisodeNumber}}"}""")) + "]}";

    /// <summary>每项为 "p 字段|文本"。</summary>
    private static string Comments(params string[] rows) => "[" + string.Join(',', rows.Select((row, index) =>
        $$"""{"cid":{{index}},"p":"{{row.Split('|')[0]}}","m":"{{row.Split('|')[1]}}"}""")) + "]";

    // 按线上实测的形状搭建：第二季的集号续接第一季（29 起），并带有 C1 这类非正片。
    private static Server FrierenServer()
    {
        var server = new Server();
        server.Search["葬送的芙莉莲"] = Animes(Anime(17617, "葬送的芙莉莲", start: "2023-09-29", count: 28), Anime(18886, "葬送的芙莉莲 第二季", start: "2026-01-16", count: 10));
        server.Bangumi["17617"] = Bangumi("葬送的芙莉莲", [.. Enumerable.Range(1, 28).Select(number => Episode(176170000 + number, number.ToString(CultureInfo.InvariantCulture))), Episode(176179001, "S1")]);
        server.Bangumi["18886"] = Bangumi("葬送的芙莉莲 第二季", [.. Enumerable.Range(1, 10).Select(number => Episode(188860000 + number, (28 + number).ToString(CultureInfo.InvariantCulture))), Episode(188869101, "C1")]);
        server.Comments["188860003"] = Comments("5.5,1,16777215,a|第三集弹幕", "1.0,5,255,b|顶部");
        server.Comments["188860004"] = Comments("2.0,1,16777215,a|第四集弹幕");
        server.Comments["176170005"] = Comments("2.0,1,16777215,a|第一季第五话");
        server.Comments["176170006"] = Comments("2.0,1,16777215,a|第一季第六话");
        return server;
    }

    private sealed class Server : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly List<string> requests = [];
        public Dictionary<string, string> Search { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Bangumi { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Comments { get; } = new(StringComparer.Ordinal);
        public string? Matches { get; set; }
        public string? Raw { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Func<HttpRequestMessage, CancellationToken, Task>? Before { get; set; }
        public List<string> UserAgents { get; } = [];
        public List<string> Hosts { get; } = [];
        public List<string> Bodies { get; } = [];
        public bool SawCredentials { get; private set; }
        public RequestLog Requests => new(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (gate)
            {
                requests.Add(request.Method + " " + uri.AbsolutePath + Uri.UnescapeDataString(uri.Query));
                UserAgents.Add(request.Headers.UserAgent.ToString());
                Hosts.Add(uri.Host);
                if (body is not null) Bodies.Add(body);
                SawCredentials |= request.Headers.Authorization is not null || request.Headers.Contains("X-Emby-Token") ||
                    request.Headers.Contains("X-Emby-Authorization") || request.Headers.Contains("Cookie") || request.Headers.Contains("X-AppId");
            }
            if (Before is not null) await Before(request, cancellationToken);
            if (Status != HttpStatusCode.OK) return new(Status);
            var path = uri.AbsolutePath;
            var last = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
            var json = Raw ?? path switch
            {
                "/api/v2/search/anime" => """{"animes":""" + Search.GetValueOrDefault(Uri.UnescapeDataString(uri.Query["?keyword=".Length..]), "[]") + ""","success":true}""",
                "/api/v2/match" => """{"isMatched":false,"matches":""" + (Matches ?? "[]") + "}",
                _ when path.StartsWith("/api/v2/bangumi/", StringComparison.Ordinal) => """{"bangumi":""" + Bangumi.GetValueOrDefault(last, "null") + "}",
                _ when path.StartsWith("/api/v2/comment/", StringComparison.Ordinal) => """{"count":0,"comments":""" + Comments.GetValueOrDefault(last, "[]") + "}",
                _ => throw new InvalidOperationException("UnexpectedBulletChatRequest"),
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        /// <summary>线程安全的请求记录视图。</summary>
        public readonly struct RequestLog(Server owner) : IEnumerable<string>
        {
            public int Count { get { lock (owner.gate) return owner.requests.Count; } }
            public void Clear() { lock (owner.gate) owner.requests.Clear(); }
            public IEnumerator<string> GetEnumerator() { string[] copy; lock (owner.gate) copy = [.. owner.requests]; return ((IEnumerable<string>)copy).GetEnumerator(); }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly Sandbox sandbox = new();
        private readonly DandanplayClient client;
        private readonly BulletChatHistory history;
        public Server Server { get; }
        public Playback Playback { get; } = new();
        public StubSettings Settings { get; } = new();
        public BulletChatService Service { get; }

        public Harness(Server server, Action<AppError>? log = null)
        {
            Server = server;
            client = new(server);
            history = new(sandbox.Paths);
            Service = new(Playback, Settings, new DandanplayBulletChatProvider(client, history, () => "scope"), new InlineScheduler(),
                static kind => kind == EngineKind.Embedded, log);
        }

        public Session Play(PlaybackEntry entry, EngineKind engine = EngineKind.Embedded)
        {
            var session = new Session();
            session.Set(new() { Phase = PlayerPhase.Playing, EngineKind = engine, Entry = entry });
            Playback.Start(session);
            return session;
        }

        public void Dispose() { Service.Dispose(); client.Dispose(); history.Dispose(); Server.Dispose(); sandbox.Dispose(); }
    }

    private sealed class InlineScheduler : IUiScheduler
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }

    private sealed class Sandbox : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "MamboTests", Guid.NewGuid().ToString("N")));
        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MamboTests")) + Path.DirectorySeparatorChar;
            if (!Paths.Root.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录不在预期路径内。");
            if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true);
        }
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new() { DeviceId = Guid.NewGuid() };
        public ConnectionDefaults ConnectionDefaults { get; } = new();
        public ExternalPlayerStatus ExternalPlayerStatus => ExternalPlayerStatus.UsingEmbedded;
        public event EventHandler? Changed;
        public void Set(Func<AppSettings, AppSettings> update) { Current = update(Current); Changed?.Invoke(this, EventArgs.Empty); }
        public Task UpdateAsync(AppSettings settings, CancellationToken cancellationToken = default) { Set(_ => settings); return Task.CompletedTask; }
        public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) { Set(update); return Task.CompletedTask; }
        public Task SaveConnectionDefaultsAsync(ConnectionDefaults defaults, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ValidateExternalPlayerAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Playback : IPlaybackService
    {
        public IPlaybackSession? Current { get; private set; }
        public bool IsStarting => false;
#pragma warning disable CS0067 // 契约要求的事件，本桩不触发。
        public event EventHandler? Changed;
        public event EventHandler<PlaybackEntrySkippedEventArgs>? EntrySkipped;
#pragma warning restore CS0067
        public event EventHandler<PlaybackSessionEventArgs>? SessionStarted;
        public event EventHandler<PlaybackSessionEventArgs>? SessionEnded;
        public void Start(Session session) { Current = session; SessionStarted?.Invoke(this, new(session)); }
        public void End(Session session)
        {
            session.Set(session.Snapshot with { Phase = PlayerPhase.Closed });
            Current = null;
            SessionEnded?.Invoke(this, new(session, PlaybackEndReason.UserClosed));
        }
        public Task<IPlaybackSession> PlayAsync(PlayRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IPlaybackSession> PreviewAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IPlaybackSession> PreviewAsync(bool replaceCurrent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Session : IPlaybackSession
    {
        public SessionSnapshot Snapshot { get; private set; } = new();
        public event EventHandler? SnapshotChanged;
        public void Set(SessionSnapshot snapshot) { Snapshot = snapshot; SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        public Task TogglePauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectEntryAsync(string itemId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepFrameAsync(FrameStepDirection direction, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(PlaybackEndReason reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
