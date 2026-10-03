using System.Net;
using System.Text.Json;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Session;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class PlaybackPreparationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MovieResumeUsesEveryPositivePositionAndExplicitZeroWins()
    {
        using var account = Account();
        var id = Id();
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ => Json(new EmbyItem
        { Id = id, Type = "Movie", UserData = new() { PlaybackPositionTicks = 1 } })));
        var resolver = new PlaybackTargetResolver(api);
        Assert.Equal(1, (await resolver.ResolveAsync(account, new(id), Token)).StartTicks);
        Assert.Equal(0, (await resolver.ResolveAsync(account, new(id, 0), Token)).StartTicks);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<AppException>(() =>
            resolver.ResolveAsync(account, new(id, -1), Token))).Error.Code);
    }

    [Fact]
    public async Task SeriesNextUpWinsWithoutLoadingAllEpisodes()
    {
        using var account = Account();
        var series = Id(); var next = Id();
        var calls = new List<string>();
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return calls.Count == 1 ? Json(new EmbyItem { Id = series, Type = "Series" }) :
                Json(new EmbyItems { Items = [Episode(next, 3, position: 70 * TimeSpan.TicksPerSecond)] });
        }));
        var target = await new PlaybackTargetResolver(api).ResolveAsync(account, new(series), Token);
        Assert.Equal(next, target.Item.Id);
        Assert.Equal(70 * TimeSpan.TicksPerSecond, target.StartTicks);
        Assert.Equal(2, calls.Count); Assert.EndsWith("/Shows/NextUp", calls[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Series", 500)]
    [InlineData("Season", 300)]
    public async Task TargetFallbackPrefersThirtySecondResumeThenUnplayedThenFirst(string type, int limit)
    {
        using var account = Account();
        var parent = Id(); var first = Id(); var resumed = Id(); var unplayed = Id();
        var episodes = new[] { Episode(unplayed, 3), Episode(resumed, 2, true, 30 * TimeSpan.TicksPerSecond), Episode(first, 1, true) };
        var mode = 0;
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/" + parent, StringComparison.Ordinal)) return Json(new EmbyItem { Id = parent, Type = type });
            if (request.RequestUri.AbsolutePath.EndsWith("/NextUp", StringComparison.Ordinal)) return Json(new EmbyItems { Items = [] });
            Assert.Contains("Limit=" + limit, request.RequestUri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("MediaSources", Uri.UnescapeDataString(request.RequestUri.Query), StringComparison.Ordinal);
            return Json(new EmbyItems { Items = episodes.Select(item => item with { UserData = mode == 0 ? item.UserData :
                new() { PlaybackPositionTicks = 0, Played = mode == 2 || item.Id != unplayed } }).ToArray() });
        }));
        var resolver = new PlaybackTargetResolver(api);
        Assert.Equal(resumed, (await resolver.ResolveAsync(account, new(parent), Token)).Item.Id);
        mode = 1; Assert.Equal(unplayed, (await resolver.ResolveAsync(account, new(parent), Token)).Item.Id);
        mode = 2; Assert.Equal(first, (await resolver.ResolveAsync(account, new(parent), Token)).Item.Id);
    }

    [Fact]
    public async Task SeasonPlanSortsFiltersAndAlwaysKeepsSelectedEpisode()
    {
        using var account = Account();
        var season = Id(); var selected = Episode(Id(), 2) with { SeasonId = season, Name = "第二集", SeriesName = "剧名" };
        var first = Episode(Id(), 1) with { SeasonId = season };
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ => Json(new EmbyItems
        {
            Items = [selected, Episode(Id(), 4) with { SeasonId = Id() }, first,
                new() { Id = Id(), Type = "Movie", SeasonId = season }, Episode(Id(), 3)],
        })));
        var plan = await new SeasonPlan(new(api)).BuildAsync(account, selected, 25, Token);
        Assert.Equal(3, plan.Entries.Length); Assert.Equal(1, plan.SelectedIndex); Assert.Equal(25, plan.StartTicks);
        Assert.Equal(first.Id, plan.Entries[0].ItemId); Assert.Equal("剧名 S01E02 - 第二集", plan.Entries[1].Title);
        Assert.Equal("第二集", plan.Entries[1].EpisodeName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeasonPlanNetworkFailureOrMissingSelectedDowngradesToSingleEpisode(bool failing)
    {
        using var account = Account(); var selected = Episode(Id(), 1) with { SeasonId = Id() };
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ => failing ? new(HttpStatusCode.ServiceUnavailable) : Json(new EmbyItems { Items = [] })));
        var plan = await new SeasonPlan(new(api)).BuildAsync(account, selected, 17, Token);
        Assert.Equal(selected.Id, Assert.Single(plan.Entries).ItemId); Assert.Equal(0, plan.SelectedIndex);
    }

    [Fact]
    public void MediaSourceEligibilityAndRankingIncludeRequiredHeadersAndTranscoding()
    {
        using var account = Account();
        var direct = Source(Id()) with { DirectStreamUrl = "Videos/" + Id(), RequiredHttpHeaders = new() { ["Referer"] = account.Address.Uri.AbsoluteUri } };
        var http = Source(Id()) with { Path = "https://" + Id() + ".invalid/play" };
        var high = Source(Id()) with { SupportsDirectPlay = true, MediaStreams = [new() { Type = "Video", Height = 2160 }] };
        var highBitrate = high with { Id = Id(), Bitrate = 999 };
        var transcode = Source(Id()) with { TranscodingUrl = "Videos/" + Id() + "/master.m3u8" };
        var iso = Source(Id()) with { SupportsDirectPlay = true, Container = "iso" };
        var audio = Source(Id()) with { SupportsDirectPlay = true, MediaStreams = [new() { Type = "Audio" }] };
        var result = MediaSourceSelector.Select([transcode, audio, high, iso, http, highBitrate, direct]);
        Assert.Equal([direct.Id, http.Id, highBitrate.Id, high.Id, transcode.Id], result.Select(value => value.Id));
    }

    [Fact]
    public void CandidateRankingIsCategoryThenSourceAndUrlsEncodeIdsDeduplicateAndStripCredentials()
    {
        using var account = Account();
        var item = Id() + "/part"; var session = Id(); var device = Guid.NewGuid();
        var url = "Videos/" + Id() + "/stream?api_key=" + account.Secret.AccessToken;
        var first = Source(Id()) with { DirectStreamUrl = url, SupportsDirectPlay = true, Container = "mkv" };
        var second = Source(Id()) with { DirectStreamUrl = url, TranscodingUrl = "Videos/" + Id() + "/master.m3u8" };
        var result = StreamCandidateBuilder.Build(account, item, device, new() { PlaySessionId = session, MediaSources = [first, second] });
        Assert.Equal(4, result.Length);
        Assert.Equal("DirectStream", result[0].PlayMethod); Assert.Equal("Transcode", result[^1].PlayMethod);
        Assert.Contains("PlaySessionId=" + session, result[0].Address.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(account.Secret.AccessToken, result[0].Address.Query, StringComparison.Ordinal);
        Assert.Contains("%2F", result[1].Address.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("stream.mkv", result[1].Address.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("DeviceId=" + device.ToString("D"), result[1].Address.Query, StringComparison.Ordinal);
        Assert.Empty(StreamCandidateBuilder.Build(account, item, device, new() { MediaSources = [Source(Id()) with { SupportsDirectPlay = true, Container = "iso" }] }));
        Assert.Throws<AppException>(() => StreamCandidateBuilder.Build(account, "..", device, new()));
    }

    [Fact]
    public void ExplicitlyDisabledDirectFlagsPrioritizeTranscodeEvenWithExplicitDirectAndHttpUrls()
    {
        using var account = Account();
        var source = Source(Id()) with
        {
            SupportsDirectPlay = false, SupportsDirectStream = false,
            DirectStreamUrl = "Videos/" + Id() + "/direct",
            Path = "https://" + Id() + ".invalid/video",
            TranscodingUrl = "Videos/" + Id() + "/master.m3u8",
        };
        var result = StreamCandidateBuilder.Build(account, Id(), Guid.NewGuid(), new() { MediaSources = [source] });
        Assert.Equal(["Transcode", "DirectStream", "DirectPlay"], result.Select(candidate => candidate.PlayMethod));
        Assert.DoesNotContain(result, candidate => candidate.Address.Query.Contains("Static=true", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredTranscodeCategoryPrecedesOtherSourcesWhileUnknownFlagsKeepNormalCategoryOrder()
    {
        using var account = Account();
        var unknown = Source(Id()) with { DirectStreamUrl = "Videos/" + Id() + "/direct", TranscodingUrl = "Videos/" + Id() + "/master.m3u8" };
        var required = unknown with
        {
            Id = Id(), SupportsDirectPlay = false, SupportsDirectStream = false,
            DirectStreamUrl = "Videos/" + Id() + "/direct", TranscodingUrl = "Videos/" + Id() + "/master.m3u8",
        };
        var result = StreamCandidateBuilder.Build(account, Id(), Guid.NewGuid(), new() { MediaSources = [unknown, required] });
        Assert.Same(required, result[0].MediaSource); Assert.Equal("Transcode", result[0].PlayMethod);
        var fallback = result.Where(candidate => ReferenceEquals(candidate.MediaSource, unknown)).ToArray();
        Assert.Equal(["DirectStream", "Transcode"], fallback.Select(candidate => candidate.PlayMethod));
    }

    [Fact]
    public async Task EntryPreparationAlwaysFetchesPlaybackInfoButOnlyProbesSelectedCandidate()
    {
        using var account = Account(); using var directory = new TemporaryDirectory();
        var id = Id(); var source = Source(Id()) with { DirectStreamUrl = "Videos/" + id + "/stream", SupportsDirectPlay = true, RequiredHttpHeaders = new() { ["Referer"] = "x,y\\z" } };
        var registrations = 0; var probes = 0;
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(request =>
        {
            registrations++;
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(Token).GetAwaiter().GetResult());
            Assert.True(body.RootElement.GetProperty("IsPlayback").GetBoolean());
            Assert.Equal(12500000, body.RootElement.GetProperty("StartTimeTicks").GetInt64());
            return Json(new EmbyPlaybackInfo { PlaySessionId = Id(), MediaSources = [source] });
        }));
        using var client = new HttpClient(new Stub(_ => { probes++; return new(HttpStatusCode.PartialContent); }));
        var preparer = new EntryPreparer(api, new(client), Guid.NewGuid(), new(directory.Path));
        var prepared = await preparer.PrepareAsync(account, new(id, "片名"), 12500000, Token);
        await preparer.PrepareAsync(account, new(id, "片名"), 12500000, Token);
        Assert.Equal(2, registrations); Assert.Equal(0, probes);
        var resolved = await preparer.ResolveCandidateAsync(account, prepared, 0, Token);
        Assert.Equal(1, probes);
        Assert.Equal("1.250", resolved.FileOptions.Single(option => option.Key == "start").Value);
        Assert.Equal("片名", resolved.FileOptions.Single(option => option.Key == "force-media-title").Value);
        Assert.Contains("Referer: x\\,y\\\\z", resolved.FileOptions.Single(option => option.Key == "http-header-fields").Value, StringComparison.Ordinal);
        Assert.DoesNotContain(account.Secret.AccessToken, prepared.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossSourceCandidateGetsExplicitEmptyFileHeadersAndNoStartForZero()
    {
        using var account = Account(); using var directory = new TemporaryDirectory();
        var source = Source(Id()) with { Path = "https://" + Id() + ".invalid/video", RequiredHttpHeaders = new() { ["Authorization"] = Id() } };
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ => Json(new EmbyPlaybackInfo { MediaSources = [source] })));
        using var client = new HttpClient(new Stub(_ => throw new InvalidOperationException("不应探测跨源 URL")));
        var preparer = new EntryPreparer(api, new(client), Guid.NewGuid(), new(directory.Path));
        var entry = await preparer.PrepareAsync(account, new(Id(), "片名"), 0, Token);
        var resolved = await preparer.ResolveCandidateAsync(account, entry, 0, Token);
        Assert.Empty(resolved.Url.Headers);
        Assert.Equal("", resolved.FileOptions.Single(option => option.Key == "http-header-fields").Value);
        Assert.DoesNotContain(resolved.FileOptions, option => option.Key == "start");
    }

    [Fact]
    public async Task OnlySelectedSourceSubtitlesDownloadLocallyAndRedirectCannotInheritVideoHeaders()
    {
        using var account = Account(); using var directory = new TemporaryDirectory();
        var source = Source(Id()) with
        {
            DirectStreamUrl = "Videos/" + Id(),
            RequiredHttpHeaders = new() { ["Referer"] = account.Address.Uri.AbsoluteUri },
            MediaStreams = [new() { Type = "Video" }, new() { Type = "Subtitle", IsExternal = true, Index = 2, Codec = "srt", DeliveryUrl = "Subtitles/source", DisplayTitle = "中文" }],
        };
        var another = Source(Id()) with { TranscodingUrl = "Videos/" + Id(), MediaStreams = [new() { Type = "Subtitle", IsExternal = true, Index = 8, Codec = "srt" }] };
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ => Json(new EmbyPlaybackInfo { MediaSources = [source, another] })));
        var cdn = new Uri("https://" + Id() + ".invalid/subtitle?token=" + account.Secret.AccessToken);
        var calls = 0;
        using var client = new HttpClient(new Stub(request =>
        {
            calls++;
            if (request.RequestUri!.Host == cdn.Host)
            {
                Assert.False(request.Headers.Contains("X-Emby-Token")); Assert.False(request.Headers.Contains("Referer"));
                Assert.Equal(cdn.Query, request.RequestUri.Query); // 保留登录服务器签发的完整下载链接。
                return new(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\n字幕\n") };
            }
            Assert.Equal(account.Secret.AccessToken, request.Headers.GetValues("X-Emby-Token").Single());
            return request.RequestUri.AbsolutePath.EndsWith("/Subtitles/source", StringComparison.Ordinal) ?
                new(HttpStatusCode.Found) { Headers = { Location = cdn } } : new(HttpStatusCode.PartialContent);
        }));
        var preparer = new EntryPreparer(api, new(client), Guid.NewGuid(), new(directory.Path));
        var entry = await preparer.PrepareAsync(account, new(Id(), "片名"), 0, Token);
        var candidate = await preparer.ResolveCandidateAsync(account, entry, 0, Token);
        var subtitle = Assert.Single(await preparer.ResolveSubtitlesAsync(account, entry, candidate, Token));
        Assert.Equal(3, calls); Assert.Equal("中文", subtitle.Subtitle.Title);
        Assert.Contains("字幕", await File.ReadAllTextAsync(subtitle.LocalPath, Token), StringComparison.Ordinal);
        Assert.StartsWith(directory.Path, subtitle.LocalPath, StringComparison.Ordinal);
        Assert.DoesNotContain(account.Secret.AccessToken, subtitle.ToString(), StringComparison.Ordinal);
        await preparer.ReleaseSubtitlesAsync([subtitle]); Assert.False(File.Exists(subtitle.LocalPath));
    }

    [Fact]
    public void PlaybackReportIncludesMandatoryDefaultsAndOptionalTrackIndexes()
    {
        var report = new PlaybackReport { ItemId = Id(), PlaylistIndex = 2, PlaylistLength = 4, PlaybackRate = 1.5, AudioStreamIndex = 3 };
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(report, EmbyJsonContext.Default.PlaybackReport));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("PlaylistIndex").GetInt32()); Assert.Equal(4, root.GetProperty("PlaylistLength").GetInt32());
        Assert.Equal(0, root.GetProperty("NowPlayingQueue").GetArrayLength()); Assert.Equal(int.MaxValue, root.GetProperty("MaxStreamingBitrate").GetInt32());
        Assert.Equal("RepeatNone", root.GetProperty("RepeatMode").GetString()); Assert.Equal(0, root.GetProperty("SubtitleOffset").GetInt64());
        Assert.False(root.GetProperty("Shuffle").GetBoolean()); Assert.Equal(1.5, root.GetProperty("PlaybackRate").GetDouble());
        Assert.Equal(3, root.GetProperty("AudioStreamIndex").GetInt32()); Assert.False(root.TryGetProperty("EventName", out _));
    }

    [Fact]
    public void DeviceProfileEmbedsEveryFormatAndOnlyFiveTextFormatsAreExternal()
    {
        var profile = DeviceProfileFactory.Create();
        Assert.Equal(5, profile.SubtitleProfiles.Count(value => value.Method == "External"));
        Assert.All(profile.SubtitleProfiles.GroupBy(value => value.Format), group => Assert.Contains(group, value => value.Method == "Embed"));
        Assert.Contains(profile.TranscodingProfiles, value => value.Protocol == "hls");
        Assert.Contains(profile.TranscodingProfiles, value => value.Protocol == "http");
    }

    [Fact]
    public async Task SeasonPlanDeduplicatesIdsAndHandlesNullRowsWithoutAnEmptyPlan()
    {
        using var account = Account();
        var season = Id(); var selected = Episode(Id(), 2) with { SeasonId = season };
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ => Json(new EmbyItems
        { Items = [null!, selected with { Name = "重复元数据", IndexNumber = 1 }, selected, Episode("..", 4)] })));
        var plan = await new SeasonPlan(new(api)).BuildAsync(account, selected, 0, Token);
        Assert.Equal(selected.Id, Assert.Single(plan.Entries).ItemId); Assert.Equal("单集", plan.Entries[0].EpisodeName);
        Assert.Equal(0, plan.SelectedIndex);
    }

    [Fact]
    public async Task SubtitleFailureSkipsOptionalTrackWithoutLeavingFiles()
    {
        using var account = Account(); using var directory = new TemporaryDirectory();
        var source = Source(Id()) with { DirectStreamUrl = "Videos/" + Id(), MediaStreams = [new() { Type = "Video" },
            new() { Type = "Subtitle", Index = 3, IsExternal = true, Codec = "srt", DeliveryUrl = "Subtitles/failed" }] };
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ => Json(new EmbyPlaybackInfo { MediaSources = [source] })));
        using var client = new HttpClient(new Stub(request => request.RequestUri!.AbsolutePath.EndsWith("/failed", StringComparison.Ordinal) ?
            new(HttpStatusCode.NotFound) : new(HttpStatusCode.PartialContent)));
        var preparer = new EntryPreparer(api, new(client), Guid.NewGuid(), new(directory.Path));
        var entry = await preparer.PrepareAsync(account, new(Id(), "片名"), 0, Token);
        var candidate = await preparer.ResolveCandidateAsync(account, entry, 0, Token);
        Assert.Empty(await preparer.ResolveSubtitlesAsync(account, entry, candidate, Token));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static AccountSession Account() => new(new SessionSecret("https://" + Id() + ".invalid/emby", Id(), Id(), "测试用户", Id()));
    private static EmbyItem Episode(string id, int number, bool played = false, long position = 0) => new()
    { Id = id, Name = "单集", Type = "Episode", ParentIndexNumber = 1, IndexNumber = number, UserData = new() { Played = played, PlaybackPositionTicks = position } };
    private static EmbyMediaSource Source(string id) => new() { Id = id, Container = "mkv", MediaStreams = [new() { Type = "Video" }] };
    private static HttpResponseMessage Json(EmbyItem value) => Response(JsonSerializer.SerializeToUtf8Bytes(value, EmbyJsonContext.Default.EmbyItem));
    private static HttpResponseMessage Json(EmbyItems value) => Response(JsonSerializer.SerializeToUtf8Bytes(value, EmbyJsonContext.Default.EmbyItems));
    private static HttpResponseMessage Json(EmbyPlaybackInfo value) => Response(JsonSerializer.SerializeToUtf8Bytes(value, EmbyJsonContext.Default.EmbyPlaybackInfo));
    private static HttpResponseMessage Response(byte[] json) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(json) };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "p3-tests", Id());
        public TemporaryDirectory() { Directory.CreateDirectory(Path); }
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
