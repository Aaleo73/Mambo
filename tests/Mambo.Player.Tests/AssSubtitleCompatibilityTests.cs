using Mambo.Core.Playback;
using Mambo.Player.LibMpv;
using Xunit;
using NativeValue = Mambo.Player.LibMpv.MpvValue;
using EngineValue = Mambo.Core.Playback.MpvValue;

namespace Mambo.Player.Tests;

public sealed class AssSubtitleCompatibilityTests
{
    private static readonly string[] SeekToFourSeconds = ["seek", "4", "absolute+exact"];
    private static readonly string[] NextEpisode = ["playlist-next", "force"];
    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    [InlineData(false, "\r\r\n")]
    public void MissingEventDeclarationsAreRestoredWithoutChangingStylesOrDialogue(bool eventsSection, string newline)
    {
        var source = AssMatroskaFixture.Header + (eventsSection ? "[Events]\n" : "") + AssMatroskaFixture.Dialogue("中文在上\\N{\\fs14}English below");
        source = source.Replace("\n", newline, StringComparison.Ordinal);
        Assert.True(AssScriptCompatibility.TryRepair(source, out var repaired));
        Assert.Contains("[Events]", repaired, StringComparison.Ordinal);
        Assert.Contains(AssMatroskaFixture.EventFormat, repaired, StringComparison.Ordinal);
        Assert.Contains(AssMatroskaFixture.Dialogue("中文在上\\N{\\fs14}English below").Trim(), repaired, StringComparison.Ordinal);
        Assert.Contains(AssMatroskaFixture.Header.Replace("\n", newline, StringComparison.Ordinal), repaired, StringComparison.Ordinal);
        Assert.False(AssScriptCompatibility.TryRepair(repaired, out _));
    }

    [Theory]
    [InlineData("0,0:00:00.00,0:00:20.00,Default,,0,0,0,,Valid, comma in text", true)]
    [InlineData("0,0:00:00.00,0:00:20.00,Default,,0,0,0,,", false)]
    [InlineData("0,0:00:20.00,0:00:00.00,Default,,0,0,0,,Reversed", false)]
    [InlineData("0,0:00:00.00,0:99:00.00,Default,,0,0,0,,Invalid", false)]
    [InlineData("not an ASS record", false)]
    public void RepairRequiresAnUnambiguousTimedAssDialogue(string dialogue, bool expected) =>
        Assert.Equal(expected, AssScriptCompatibility.TryRepair(AssMatroskaFixture.Header + "Dialogue: " + dialogue, out _));

    [Fact]
    public void OrdinaryHeadersScriptsAndUnsupportedInputsAreNotRewritten()
    {
        Assert.False(AssScriptCompatibility.TryRepair(AssMatroskaFixture.Header, out _));
        Assert.False(AssScriptCompatibility.TryRepair(AssMatroskaFixture.Header + "[Events]\n" + AssMatroskaFixture.EventFormat + "\n" + AssMatroskaFixture.Dialogue("Valid"), out _));
        Assert.False(AssScriptCompatibility.TryRepair("Dialogue: " + AssMatroskaFixture.Dialogue("Header missing"), out _));
        Assert.False(AssScriptCompatibility.TryRepair(AssMatroskaFixture.Header + "\0" + AssMatroskaFixture.Dialogue("Null"), out _));
        Assert.False(AssScriptCompatibility.TryRepair(new string('a', 5 * 1024 * 1024 + 1), out _));
    }

    [Fact]
    public async Task EmbeddedBrokenAssRecoversBehindTheOriginalTrackAndSurvivesSelectionSeekAndPlaylistAdvance()
    {
        RequireLibrary();
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "mambo-ass-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "first.mkv");
            var second = Path.Combine(directory, "second.mkv");
            await File.WriteAllBytesAsync(first, AssMatroskaFixture.Create("合成双语测试\\NEnglish test", broken: true), token);
            await File.WriteAllBytesAsync(second, AssMatroskaFixture.Create("下一集双语\\NNext episode", broken: true), token);
            await using var engine = await LibMpvEngine.CreateAsync(headless: true,
                optionOverrides: new Dictionary<string, string> { ["pause"] = "yes", ["sub-ass-override"] = "no" }, cancellationToken: token);
            var exposed = new List<EngineEvent>();
            await engine.LoadAsync(first, LoadMode.Replace, [], token);
            await UntilEvent(engine, exposed, value => value is EngineEvent.FileLoaded);
            await Until(() => SubtitleText(engine).Contains("English test", StringComparison.Ordinal));
            Assert.Contains("合成双语测试", SubtitleText(engine), StringComparison.Ordinal);
            Assert.Equal(3, NativeSubtitleCount(engine));
            AssertProjectedTracks(exposed);

            await engine.SetAsync("sid", new EngineValue.Text("2"), token);
            await Until(() => SubtitleText(engine) == "正常字幕");
            await engine.SetAsync("sid", new EngineValue.Text("1"), token);
            await Until(() => SubtitleText(engine).Contains("English test", StringComparison.Ordinal));
            Assert.Equal(3, NativeSubtitleCount(engine));
            await UntilEvent(engine, exposed, value => value is EngineEvent.PropertyChanged
                { Property: EngineProperty.SubtitleTrack, Value: EngineValue.WholeNumber { Value: 1 } });
            await engine.Core.SetPropertyAsync("sid", new NativeValue.WholeNumber(1), token);
            await Until(() => SubtitleText(engine).Contains("English test", StringComparison.Ordinal) &&
                engine.Core.GetProperty("sid") is NativeValue.WholeNumber { Value: 3 });
            await engine.SetAsync("sid", new EngineValue.Text("auto"), token);
            await Until(() => SubtitleText(engine).Contains("English test", StringComparison.Ordinal));
            Assert.Equal(3, NativeSubtitleCount(engine));
            await engine.SetAsync("sub-delay", new EngineValue.Number(.4), token);
            await engine.CommandAsync(SeekToFourSeconds, token);
            await Until(() => SubtitleText(engine).Contains("English test", StringComparison.Ordinal));
            Assert.Equal(.4, Assert.IsType<NativeValue.Number>(engine.Core.GetProperty("sub-delay")).Value, 5);
            Assert.True(Assert.IsType<NativeValue.Flag>(engine.Core.GetProperty("pause")).Value);
            await engine.SetAsync("sid", new EngineValue.Text("no"), token);
            await Until(() => SubtitleText(engine).Length == 0);
            await engine.SetAsync("sid", new EngineValue.WholeNumber(1), token);
            await Until(() => SubtitleText(engine).Contains("English test", StringComparison.Ordinal));

            await engine.LoadAsync(second, LoadMode.Append, [], token);
            await engine.CommandAsync(NextEpisode, token);
            await UntilEvent(engine, exposed, value => value is EngineEvent.FileLoaded);
            await engine.CommandAsync(SeekToFourSeconds, token);
            await Until(() => SubtitleText(engine).Contains("Next episode", StringComparison.Ordinal));
            Assert.DoesNotContain("English test", SubtitleText(engine), StringComparison.Ordinal);
            Assert.Equal(3, NativeSubtitleCount(engine));
            AssertProjectedTracks(exposed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NormalEmbeddedAssKeepsItsNativeTracksAndDisplaysItsDialogue()
    {
        RequireLibrary();
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "mambo-ass-normal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "normal.mkv");
            await File.WriteAllBytesAsync(file, AssMatroskaFixture.Create("正常双语\\NOrdinary English", broken: false), token);
            await using var engine = await LibMpvEngine.CreateAsync(headless: true,
                optionOverrides: new Dictionary<string, string> { ["pause"] = "yes" }, cancellationToken: token);
            await engine.LoadAsync(file, LoadMode.Replace, [], token);
            await UntilEvent(engine, [], value => value is EngineEvent.FileLoaded);
            await Until(() => SubtitleText(engine).Contains("Ordinary English", StringComparison.Ordinal));
            Assert.Equal(2, NativeSubtitleCount(engine));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("no")]
    [InlineData("2")]
    public async Task DisabledSubtitlesAndAnotherExplicitTrackStaySelectedUntilTheBrokenTrackIsRequested(string initial)
    {
        RequireLibrary();
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "mambo-ass-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "manual.mkv");
            await File.WriteAllBytesAsync(file, AssMatroskaFixture.Create("手选双语\\NSelected English", broken: true), token);
            await using var engine = await LibMpvEngine.CreateAsync(headless: true,
                optionOverrides: new Dictionary<string, string> { ["pause"] = "yes" }, cancellationToken: token);
            await engine.LoadAsync(file, LoadMode.Replace, [new("sid", initial)], token);
            await UntilEvent(engine, [], value => value is EngineEvent.FileLoaded);
            Assert.Equal(2, NativeSubtitleCount(engine));
            if (initial == "no") Assert.Empty(SubtitleText(engine));
            else await Until(() => SubtitleText(engine) == "正常字幕");
            await engine.SetAsync("sid", new EngineValue.Text("1"), token);
            await Until(() => SubtitleText(engine).Contains("Selected English", StringComparison.Ordinal));
            Assert.Equal(3, NativeSubtitleCount(engine));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertProjectedTracks(List<EngineEvent> exposed)
    {
        var lists = exposed.OfType<EngineEvent.PropertyChanged>().Where(value => value.Property == EngineProperty.TrackList)
            .Select(value => Assert.IsType<EngineValue.Array>(value.Value)).ToArray();
        Assert.NotEmpty(lists);
        foreach (var list in lists)
        {
            var tracks = list.Values.OfType<EngineValue.Map>().Where(track => track.Values.GetValueOrDefault("type") is EngineValue.Text { Value: "sub" }).ToArray();
            if (tracks.Length == 0) continue;
            Assert.Equal(2, tracks.Length);
            Assert.All(tracks, track =>
            {
                Assert.False(track.Values.GetValueOrDefault("external") is EngineValue.Flag { Value: true });
                Assert.False(track.Values.GetValueOrDefault("external-filename") is EngineValue.Text text && text.Value.StartsWith("memory://", StringComparison.Ordinal));
            });
        }
    }

    private static string SubtitleText(LibMpvEngine engine) => (engine.Core.GetProperty("sub-text") as NativeValue.Text)?.Value ?? "";
    private static int NativeSubtitleCount(LibMpvEngine engine) => ((NativeValue.Array)engine.Core.GetProperty("track-list")!).Values
        .OfType<NativeValue.Map>().Count(track => track.Values.GetValueOrDefault("type") is NativeValue.Text { Value: "sub" });

    private static void RequireLibrary() => Assert.SkipUnless(OperatingSystem.IsWindows() &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll")), "没有 Windows libmpv DLL，跳过真实组件验证。");

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(25, timeout.Token);
    }

    private static async Task UntilEvent(LibMpvEngine engine, List<EngineEvent> observed, Func<EngineEvent, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var value in engine.Events.ReadAllAsync(timeout.Token))
        {
            observed.Add(value);
            Assert.IsNotType<EngineEvent.Failure>(value);
            if (predicate(value)) return;
        }
        Assert.Fail("原生事件流提前关闭。");
    }
}
