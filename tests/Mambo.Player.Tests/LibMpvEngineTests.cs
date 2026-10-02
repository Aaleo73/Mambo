using Mambo.Core.Playback;
using Mambo.Player.LibMpv;
using Xunit;
using MpvValue = Mambo.Core.Playback.MpvValue;

namespace Mambo.Player.Tests;

public sealed class LibMpvEngineTests
{
    private const string TestSource = "av://lavfi:testsrc=size=128x72:rate=24";
    private static readonly string[] PlaylistNextCommand = ["playlist-next"];
    private static readonly string[] QuitCommand = ["quit"];
    private static void RequireLibrary() => Assert.SkipUnless(
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll")),
        "没有 Windows libmpv DLL，跳过真实组件验证。");

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
