using Mambo.Core.Playback;
using Mambo.Player.LibMpv;
using Xunit;
using MpvValue = Mambo.Core.Playback.MpvValue;

namespace Mambo.Player.Tests;

public sealed class LibMpvEngineTests
{
    [Fact]
    public async Task NativeTextSubtitlesExposeTracksDelayAndAllStylePropertiesWhilePaused()
    {
        RequireLibrary();
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "mambo-subtitle-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var srt = Path.Combine(directory, "sample.srt");
            var ass = Path.Combine(directory, "sample.ass");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:20,000\nSubtitle\n", token);
            await File.WriteAllTextAsync(ass, """
                [Script Info]
                ScriptType: v4.00+
                PlayResX: 1280
                PlayResY: 720
                [V4+ Styles]
                Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
                Style: Default,Arial,40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,20,1
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:00.00,0:00:20.00,Default,,0,0,0,,Subtitle
                """, token);
            await using var engine = await LibMpvEngine.CreateAsync(headless: true, enableAudio: false, cancellationToken: token);
            await engine.LoadAsync(TestSource, LoadMode.Replace, [], token);
            await ReadUntil(engine, value => value is EngineEvent.FileLoaded);
            await engine.SetAsync("pause", new MpvValue.Flag(true), token);
            await engine.CommandAsync(new[] { "sub-add", srt, "auto", "Text", "en" }, token);
            await engine.CommandAsync(new[] { "sub-add", ass, "auto", "Styled", "en" }, token);
            var trackEvent = Assert.IsType<EngineEvent.PropertyChanged>(await ReadUntil(engine, value => value is EngineEvent.PropertyChanged
            {
                Property: EngineProperty.TrackList, Value: MpvValue.Array array,
            } && array.Values.OfType<MpvValue.Map>().Count(track => track.Values.GetValueOrDefault("type") is MpvValue.Text { Value: "sub" }) == 2));
            var tracks = Assert.IsType<MpvValue.Array>(trackEvent.Value).Values.OfType<MpvValue.Map>()
                .Where(track => track.Values.GetValueOrDefault("type") is MpvValue.Text { Value: "sub" }).ToArray();
            Assert.Contains(tracks, track => track.Values.GetValueOrDefault("codec") is MpvValue.Text { Value: "ass" });
            var id = Assert.IsType<MpvValue.WholeNumber>(tracks[0].Values["id"]).Value;
            await engine.SetAsync("sid", new MpvValue.WholeNumber(id), token);
            await engine.SetAsync("sub-delay", new MpvValue.Number(0.4), token);
            await ReadUntil(engine, value => value is EngineEvent.PropertyChanged
            { Property: EngineProperty.SubtitleDelay, Value: MpvValue.Number number } && Math.Abs(number.Value - 0.4) < 0.0001);
            var style = new Mambo.Core.Contracts.SubtitleStyleSettings { FontSize = 48, OutlineSize = 2, BottomMargin = 40, OverrideAssStyle = true };
            foreach (var pair in SubtitleStyle.Properties(style))
            {
                var error = await Record.ExceptionAsync(async () => await engine.SetAsync(pair.Key, pair.Value, token));
                Assert.True(error is null, $"字幕样式属性 {pair.Key} 未被原生播放器接受：{error?.Message}");
            }
            Assert.Equal("force", Assert.IsType<Mambo.Player.LibMpv.MpvValue.Text>(engine.Core.GetProperty("sub-ass-override")).Value);
            Assert.Equal(40, Assert.IsType<Mambo.Player.LibMpv.MpvValue.WholeNumber>(engine.Core.GetProperty("sub-margin-y")).Value);
            Assert.True(Assert.IsType<Mambo.Player.LibMpv.MpvValue.Flag>(engine.Core.GetProperty("pause")).Value);
            await engine.SetAsync("sub-ass-override", new MpvValue.Text("no"), token);
            Assert.False(Assert.IsType<Mambo.Player.LibMpv.MpvValue.Flag>(engine.Core.GetProperty("sub-ass-override")).Value);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private const string TestSource = "av://lavfi:testsrc=size=128x72:rate=24";
    private static readonly string[] PlaylistNextCommand = ["playlist-next"];
    private static readonly string[] QuitCommand = ["quit"];
    private static void RequireLibrary() => Assert.SkipUnless(
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll")),
        "没有 Windows libmpv DLL，跳过真实组件验证。");

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task AudioOutputUsesNativeAutoselectionUnlessAudioIsDisabled(bool headless, bool enableAudio)
    {
        RequireLibrary();
        // Keep the production audio branch, but never create a window, load media or open an audio device.
        await using var core = new MpvCore(1, 1, headless, enableAudio,
            new Dictionary<string, string> { ["vo"] = "null", ["force-window"] = "no", ["hwdec"] = "no" });
        var drivers = Assert.IsType<Mambo.Player.LibMpv.MpvValue.Array>(core.GetProperty("options/ao"));
        if (!headless && enableAudio)
        {
            // The actual native settings list must remain empty so mpv probes the supported drivers.
            // A literal "auto" driver is nonempty and prevents that probe.
            Assert.Empty(drivers.Values);
        }
        else
        {
            var driver = Assert.IsType<Mambo.Player.LibMpv.MpvValue.Map>(Assert.Single(drivers.Values));
            Assert.Equal("null", Assert.IsType<Mambo.Player.LibMpv.MpvValue.Text>(driver.Values["name"]).Value);
        }
        Assert.Equal("auto", Assert.IsType<Mambo.Player.LibMpv.MpvValue.Text>(core.GetProperty("options/audio-device")).Value);
        Assert.Null(core.GetProperty("current-ao"));
    }

    [Fact]
    public async Task LoadCopiesLifecycleAndTypedPropertiesWithRealEntryId()
    {
        RequireLibrary();
        await using var engine = await LibMpvEngine.CreateAsync(headless: true, cancellationToken: TestContext.Current.CancellationToken);
        var id = await engine.LoadAsync(TestSource, LoadMode.Replace, [], TestContext.Current.CancellationToken);
        Assert.True(id >= 0);
        var started = false;
        var loaded = false;
        var restarted = false;
        var tracksCopied = false;
        await ReadUntil(engine, value =>
        {
            switch (value)
            {
                case EngineEvent.StartFile start: Assert.Equal(id, start.EntryId); started = true; break;
                case EngineEvent.FileLoaded: loaded = true; break;
                case EngineEvent.PlaybackRestart: restarted = true; break;
                case EngineEvent.PropertyChanged { Property: EngineProperty.TrackList, Value: MpvValue.Array { Values.Count: > 0 } array }:
                    Assert.IsType<MpvValue.Map>(array.Values[0]); tracksCopied = true; break;
            }
            return started && loaded && restarted && tracksCopied;
        });
        await engine.SetAsync("pause", new MpvValue.Flag(true), TestContext.Current.CancellationToken);
        var paused = Assert.IsType<EngineEvent.PropertyChanged>(await ReadUntil(engine, value =>
            value is EngineEvent.PropertyChanged { Property: EngineProperty.Pause, Value: MpvValue.Flag { Value: true } }));
        Assert.IsType<MpvValue.Flag>(paused.Value);
        var trackList = engine.Core.GetProperty("track-list");
        Assert.IsType<Mambo.Player.LibMpv.MpvValue.Map>(Assert.IsType<Mambo.Player.LibMpv.MpvValue.Array>(trackList).Values[0]);
        Assert.Null(engine.CurrentSwapChain);
    }

    [Fact]
    public async Task AppendReturnsDifferentIdAndPlaylistNextStartsThatEntry()
    {
        RequireLibrary();
        await using var engine = await LibMpvEngine.CreateAsync(headless: true, cancellationToken: TestContext.Current.CancellationToken);
        var first = await engine.LoadAsync(TestSource, LoadMode.Replace, [], TestContext.Current.CancellationToken);
        await ReadUntil(engine, value => value is EngineEvent.PlaybackRestart);
        var next = await engine.LoadAsync(TestSource, LoadMode.Append, [], TestContext.Current.CancellationToken);
        Assert.NotEqual(first, next);
        await engine.CommandAsync(PlaylistNextCommand, TestContext.Current.CancellationToken);
        var stopped = Assert.IsType<EngineEvent.EndFile>(await ReadUntil(engine, value => value is EngineEvent.EndFile));
        Assert.Equal(first, stopped.EntryId);
        Assert.Equal(EngineEndReason.Stop, stopped.Reason);
        var started = Assert.IsType<EngineEvent.StartFile>(await ReadUntil(engine, value => value is EngineEvent.StartFile));
        Assert.Equal(next, started.EntryId);
    }

    [Fact]
    public async Task FileOptionsUseNodeMapAndFiniteClipEndsWithEof()
    {
        RequireLibrary();
        await using var engine = await LibMpvEngine.CreateAsync(headless: true, cancellationToken: TestContext.Current.CancellationToken);
        var id = await engine.LoadAsync(TestSource, LoadMode.Replace,
            [new("length", "0.3")], TestContext.Current.CancellationToken);
        var ended = Assert.IsType<EngineEvent.EndFile>(await ReadUntil(engine, value => value is EngineEvent.EndFile));
        Assert.Equal(id, ended.EntryId);
        Assert.Equal(EngineEndReason.Eof, ended.Reason);
        Assert.Equal(0, ended.Error);
    }

    [Fact]
    public async Task MissingFileProducesEndFileErrorInsteadOfHangingOpening()
    {
        RequireLibrary();
        await using var engine = await LibMpvEngine.CreateAsync(headless: true, cancellationToken: TestContext.Current.CancellationToken);
        var missing = Path.Combine(Path.GetTempPath(), "mambo-missing-" + Guid.NewGuid().ToString("N") + ".mp4");
        var id = await engine.LoadAsync(missing, LoadMode.Replace, [], TestContext.Current.CancellationToken);
        var ended = Assert.IsType<EngineEvent.EndFile>(await ReadUntil(engine, value => value is EngineEvent.EndFile));
        Assert.Equal(id, ended.EntryId);
        Assert.Equal(EngineEndReason.Error, ended.Reason);
        Assert.True(ended.Error < 0);
    }

    [Fact]
    public async Task TypedSetReportsRealErrorsAndCancellationPreventsDispatch()
    {
        RequireLibrary();
        await using var engine = await LibMpvEngine.CreateAsync(headless: true, cancellationToken: TestContext.Current.CancellationToken);
        await engine.SetAsync("volume", new MpvValue.Number(41), TestContext.Current.CancellationToken);
        var volume = Assert.IsType<EngineEvent.PropertyChanged>(await ReadUntil(engine, value =>
            value is EngineEvent.PropertyChanged { Property: EngineProperty.Volume, Value: MpvValue.Number { Value: 41 } }));
        Assert.Equal(41, Assert.IsType<MpvValue.Number>(volume.Value).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.SetAsync("mambo-invalid-property", new MpvValue.Flag(true), TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.SetAsync("volume", new MpvValue.Number(0), cancelled.Token));
        Assert.Equal(41, Assert.IsType<Mambo.Player.LibMpv.MpvValue.Number>(engine.Core.GetProperty("volume")).Value);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.CommandAsync(QuitCommand, cancelled.Token));
        await engine.SetAsync("mute", new MpvValue.Flag(true), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisposeIsIdempotentCompletesEventsAndRejectsFurtherRequests()
    {
        RequireLibrary();
        var engine = await LibMpvEngine.CreateAsync(headless: true, cancellationToken: TestContext.Current.CancellationToken);
        await engine.LoadAsync(TestSource, LoadMode.Replace, [], TestContext.Current.CancellationToken);
        await ReadUntil(engine, value => value is EngineEvent.PlaybackRestart);
        await engine.DisposeAsync();
        await engine.DisposeAsync();
        var remaining = new List<EngineEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in engine.Events.ReadAllAsync(timeout.Token)) remaining.Add(item);
        Assert.Contains(remaining, item => item is EngineEvent.Shutdown);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await engine.SetAsync("pause", new MpvValue.Flag(true), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await engine.LoadAsync(TestSource, LoadMode.Replace, [], TestContext.Current.CancellationToken));
    }

    private static async Task<EngineEvent> ReadUntil(LibMpvEngine engine, Func<EngineEvent, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var item in engine.Events.ReadAllAsync(timeout.Token))
        {
            Assert.IsNotType<EngineEvent.Failure>(item);
            if (predicate(item)) return item;
        }
        throw new InvalidOperationException("播放器事件流提前结束。");
    }
}
