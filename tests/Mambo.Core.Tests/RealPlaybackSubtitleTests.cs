using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Mambo.Core.Session;
using Mambo.Core.Subtitles;
using Xunit;

namespace Mambo.Core.Tests;

public sealed partial class RealPlaybackSessionTests
{
    [Fact]
    public async Task TrackChoicesRestoreAcrossEpisodesAndOldControlsCannotChangeNewEntry()
    {
        await using var harness = new Harness(entryCount: 2, seriesId: "series");
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "简体", "subrip"), SubtitleTrack(2, "en", "English", "subrip"));
        await UntilAsync(() => session.Snapshot.SubtitleTracks.Length == 2);
        await session.SelectSubtitleTrackAsync("2", session.Snapshot.EntryGeneration, cancellationToken: TestContext.Current.CancellationToken);
        await session.SetSubtitleDelayAsync(0.4, session.Snapshot.EntryGeneration, "2", cancellationToken: TestContext.Current.CancellationToken);
        await session.SelectSubtitleTrackAsync("2", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0.4, session.Snapshot.SubtitleDelaySeconds);
        await session.SeekAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0.4, session.Snapshot.SubtitleDelaySeconds);
        var oldGeneration = session.Snapshot.EntryGeneration;
        await UntilAsync(() => harness.Engine.Loads.Any(load => load.Mode == LoadMode.Append));
        await session.NextAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.EntryGeneration != oldGeneration);
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(5, "zh", "简体", "subrip"), SubtitleTrack(7, "eng", "English", "subrip"));
        await UntilAsync(() => session.Snapshot.SelectedSubtitleTrackId == "7");
        Assert.Equal(0, session.Snapshot.SubtitleDelaySeconds);
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleDelayAsync(0.2, oldGeneration, "2", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AppException>(() => session.SelectSubtitleTrackAsync("5", oldGeneration, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("7", session.Snapshot.SelectedSubtitleTrackId);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
    }

    [Fact]
    public async Task SubtitleDelayRejectsWrongTrackAndFailedNativeChangeRetainsActualValue()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "简体", "subrip"));
        await UntilAsync(() => session.Snapshot.SelectedSubtitleTrackId == "1");
        var generation = session.Snapshot.EntryGeneration;
        await session.SetSubtitleDelayAsync(-0.3, generation, "1", cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleDelayAsync(0.25, generation, "1", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleDelayAsync(61, generation, "1", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleDelayAsync(1, generation, "2", cancellationToken: TestContext.Current.CancellationToken));
        harness.Engine.FailNextProperty = "sub-delay";
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleDelayAsync(0.5, generation, "1", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(-0.3, session.Snapshot.SubtitleDelaySeconds);
        await session.SelectSubtitleTrackAsync(null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, session.Snapshot.SubtitleDelaySeconds);
        Assert.False(session.Snapshot.CanAdjustSubtitleDelay);
    }

    [Fact]
    public async Task StylesApplyWhilePausedSaveGloballyAndFailedUpdateRollsBackWholeStyle()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "简体", "subrip"));
        await UntilAsync(() => session.Snapshot.SubtitleStyleKind == SubtitleStyleKind.Text);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        var style = session.Snapshot.SubtitleStyle with { FontSize = 50, TextColor = "#FFAA00", BottomMargin = 70 };
        await session.SetSubtitleStyleAsync(style, session.Snapshot.EntryGeneration, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(style, session.Snapshot.SubtitleStyle);
        Assert.Equal(style, harness.Settings.Current.SubtitleStyle);
        Assert.True(session.Snapshot.IsPaused);
        harness.Engine.FailNextProperty = "sub-color";
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleStyleAsync(style with { FontSize = 60, TextColor = "#FFFFFF" }, session.Snapshot.EntryGeneration, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new MpvValue.Number(50), harness.Engine.Values["sub-font-size"]);
        Assert.Equal(new MpvValue.Text("#FFAA00"), harness.Engine.Values["sub-color"]);
        Assert.Equal(style, session.Snapshot.SubtitleStyle);
        Assert.Equal(style, harness.Settings.Current.SubtitleStyle);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
    }

    [Fact]
    public async Task AssRequiresExplicitOverrideAndBitmapRejectsTextStyling()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "特效", "ass"), SubtitleTrack(2, "en", "图形", "hdmv_pgs_subtitle"));
        await UntilAsync(() => session.Snapshot.SubtitleStyleKind == SubtitleStyleKind.Ass);
        var style = session.Snapshot.SubtitleStyle;
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleStyleAsync(style with { FontSize = 50 }, session.Snapshot.EntryGeneration, cancellationToken: TestContext.Current.CancellationToken));
        await session.SetSubtitleStyleAsync(style with { OverrideAssStyle = true }, session.Snapshot.EntryGeneration, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new MpvValue.Text("force"), harness.Engine.Values["sub-ass-override"]);
        await session.SelectSubtitleTrackAsync("2", cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AppException>(() => session.SetSubtitleStyleAsync(style, session.Snapshot.EntryGeneration, cancellationToken: TestContext.Current.CancellationToken));
        await session.SetSubtitleDelayAsync(0.1, session.Snapshot.EntryGeneration, "2", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0.1, session.Snapshot.SubtitleDelaySeconds);
    }

    [Fact]
    public async Task ImportCopiesForOtherEpisodesAndOnlyLoadsTheirOwnFilesAutomatically()
    {
        await using var harness = new Harness(entryCount: 2, seriesId: "series");
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        var first = harness.SubtitleSource("entry-0.srt");
        var second = harness.SubtitleSource("entry-1.srt", "1\n00:00:00,000 --> 00:00:30,000\n第二集\n");
        var context = Assert.IsType<SubtitleImportContext>(session.BeginSubtitleImport());
        await session.ImportSubtitlesAsync(context, [new(first), new(second)], TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.SubtitleTracks.Length == 1 && session.Snapshot.SelectedSubtitleTrackId is not null);
        Assert.Single(harness.Engine.Commands, args => args[0] == "sub-add");
        Assert.Equal(TrackSource.Local, session.Snapshot.SubtitleTracks[0].Source);
        Assert.Contains("entry-0.srt", session.Snapshot.SubtitleTracks[0].Label, StringComparison.Ordinal);
        File.Delete(first); File.Delete(second);
        var originalGeneration = session.Snapshot.EntryGeneration;
        await UntilAsync(() => harness.Engine.Loads.Any(load => load.Mode == LoadMode.Append));
        harness.Engine.NativeTracks.Clear();
        await session.NextAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.EntryGeneration != originalGeneration);
        await harness.ConfirmAsync(session);
        await UntilAsync(() => session.Snapshot.SubtitleTracks.Length == 1 && session.Snapshot.SelectedSubtitleTrackId is not null);
        Assert.Contains("entry-1.srt", session.Snapshot.SubtitleTracks[0].Label, StringComparison.Ordinal);
        Assert.Equal(2, harness.Engine.Commands.Count(args => args[0] == "sub-add"));
        Assert.Null(session.Snapshot.Error);
    }

    [Fact]
    public async Task SlowImportCannotOverrideLaterOffEvenWhenNativeAutoSelectsAddedTrack()
    {
        await using var harness = new Harness(seriesId: "series");
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "内封", "subrip"));
        await UntilAsync(() => session.Snapshot.SelectedSubtitleTrackId == "1");
        harness.SubtitleTargets.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.SimulateSubtitleAutoselection = true;
        var context = Assert.IsType<SubtitleImportContext>(session.BeginSubtitleImport());
        var import = session.ImportSubtitlesAsync(context, [new(harness.SubtitleSource("entry-0.srt"))], TestContext.Current.CancellationToken);
        await harness.SubtitleTargets.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await session.SelectSubtitleTrackAsync(null, cancellationToken: TestContext.Current.CancellationToken);
        harness.SubtitleTargets.Hold.SetResult();
        await import;
        await UntilAsync(() => session.Snapshot.SubtitleTracks.Any(track => track.Source == TrackSource.Local));
        // 所有已排队原生事件消费完后，手选关闭仍是最终状态。
        await session.SetRateAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(session.Snapshot.SelectedSubtitleTrackId);
        Assert.Equal(new MpvValue.Text("no"), harness.Engine.Values["sid"]);
        Assert.Null(session.Snapshot.Error);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
    }

    [Fact]
    public async Task DropContextRemainsBoundToOriginalEpisodeAfterStorageDataArrivesLate()
    {
        await using var harness = new Harness(entryCount: 2);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        var context = Assert.IsType<SubtitleImportContext>(session.BeginSubtitleImport());
        var generation = session.Snapshot.EntryGeneration;
        await UntilAsync(() => harness.Engine.Loads.Any(load => load.Mode == LoadMode.Append));
        await session.NextAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.EntryGeneration != generation);
        await harness.ConfirmAsync(session);
        await session.ImportSubtitlesAsync(context, [new(harness.SubtitleSource("unnumbered.srt"))], TestContext.Current.CancellationToken);
        await session.SetRateAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        var original = await harness.LocalSubtitles.GetForItemAsync(harness.Account, "entry-0", TestContext.Current.CancellationToken);
        Assert.Single(original.Files);
        Assert.Empty(session.Snapshot.SubtitleTracks);
        Assert.DoesNotContain(harness.Engine.Commands, args => args[0] == "sub-add");
    }

    private static void EmitTracks(FakeEngine engine, params MpvValue[] tracks) =>
        engine.Emit(new EngineEvent.PropertyChanged(EngineProperty.TrackList, new MpvValue.Array(tracks)));

    [Fact]
    public async Task NativeSubtitleFinishingAfterNaturalEofIsRemovedFromNextEpisode()
    {
        await using var harness = new Harness(entryCount: 2);
        await harness.Settings.UpdateAsync(settings => settings with { PreferredSubtitleLanguage = "auto" }, TestContext.Current.CancellationToken);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "简体", "subrip"), SubtitleTrack(2, "en", "English", "subrip"));
        await UntilAsync(() => session.Snapshot.SubtitleTracks.Length == 2);
        await UntilAsync(() => harness.Engine.Loads.Any(load => load.Mode == LoadMode.Append));
        var firstId = harness.Engine.ActiveId;
        var nextId = harness.Engine.Loads.Single(load => load.Mode == LoadMode.Append).Id;
        var oldGeneration = session.Snapshot.EntryGeneration;
        harness.Engine.SubAddHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.SimulateSubtitleAutoselection = true;
        harness.SubtitleTargets.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Assert.IsType<SubtitleImportContext>(session.BeginSubtitleImport());
        var import = session.ImportSubtitlesAsync(context, [new(harness.SubtitleSource("entry-0.srt"))], TestContext.Current.CancellationToken);
        await harness.SubtitleTargets.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await session.SelectSubtitleTrackAsync("2", TestContext.Current.CancellationToken);
        harness.SubtitleTargets.Hold.SetResult();
        await import;
        await harness.Engine.SubAddEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.Engine.Emit(new EngineEvent.EndFile(firstId, EngineEndReason.Eof, 0));
        harness.Engine.Emit(new EngineEvent.StartFile(nextId));
        harness.Engine.Emit(new EngineEvent.FileLoaded());
        harness.Engine.Emit(new EngineEvent.PlaybackRestart());
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "第二集内封", "subrip"), SubtitleTrack(2, "ja", "日语", "subrip"));
        harness.Engine.Emit(new EngineEvent.PropertyChanged(EngineProperty.SubtitleTrack, new MpvValue.Text("1")));
        await UntilAsync(() => harness.Engine.Events.Count == 0);
        harness.Engine.SubAddHold.SetResult();
        await UntilAsync(() => session.Snapshot.EntryGeneration != oldGeneration &&
            harness.Engine.Commands.Any(args => args[0] == "sub-remove") && session.Snapshot.SelectedSubtitleTrackId == "1");
        await session.SetRateAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(2, session.Snapshot.SubtitleTracks.Length);
        Assert.All(session.Snapshot.SubtitleTracks, track => Assert.Equal(TrackSource.Embedded, track.Source));
        Assert.Single(harness.Engine.PropertyWrites, write => write.Property == "sid" && write.Value == new MpvValue.Text("2"));
        Assert.Equal("entry-1", session.Snapshot.Entry?.ItemId);
        Assert.Null(session.Snapshot.Error);
    }

    [Fact]
    public async Task NativeAutoSelectionKeepsSelectedTrackInBoundedMenu()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, Enumerable.Range(1, 40).Select(id => (MpvValue)new MpvValue.Map(new Dictionary<string, MpvValue?>
        {
            ["id"] = new MpvValue.WholeNumber(id), ["type"] = new MpvValue.Text("audio"),
            ["lang"] = new MpvValue.Text("en"), ["title"] = new MpvValue.Text("音轨 " + id),
        })).ToArray());
        await UntilAsync(() => session.Snapshot.AudioTracks.Length == 32);
        harness.Engine.Emit(new EngineEvent.PropertyChanged(EngineProperty.AudioTrack, new MpvValue.Text("40")));
        await UntilAsync(() => session.Snapshot.SelectedAudioTrackId == "40" && session.Snapshot.AudioTracks.Any(track => track.Id == "40"));
        Assert.Equal(32, session.Snapshot.AudioTracks.Length);
    }

    [Fact]
    public async Task SelectingAnotherEpisodeDoesNotCancelOriginalEpisodesPersistentAdoption()
    {
        await using var harness = new Harness(entryCount: 2);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        harness.SubtitleTargets.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Assert.IsType<SubtitleImportContext>(session.BeginSubtitleImport());
        var import = session.ImportSubtitlesAsync(context, [new(harness.SubtitleSource("entry-0.srt"))], TestContext.Current.CancellationToken);
        await harness.SubtitleTargets.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var generation = session.Snapshot.EntryGeneration;
        await UntilAsync(() => harness.Engine.Loads.Any(load => load.Mode == LoadMode.Append));
        await session.NextAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.EntryGeneration != generation);
        await harness.ConfirmAsync(session);
        await session.SelectSubtitleTrackAsync(null, TestContext.Current.CancellationToken);
        harness.SubtitleTargets.Hold.SetResult();
        await import;
        await session.SetRateAsync(1, TestContext.Current.CancellationToken);
        var stored = await harness.LocalSubtitles.GetForItemAsync(harness.Account, "entry-0", TestContext.Current.CancellationToken);
        Assert.Equal(Assert.Single(stored.Files).Id, stored.AdoptedSubtitleId);
        Assert.DoesNotContain(harness.Engine.Commands, args => args[0] == "sub-add");
        Assert.Null(session.Snapshot.SelectedSubtitleTrackId);
    }

    [Fact]
    public async Task CloseCancelsNativeSubtitleLoadWithoutWaitingForItsCompletion()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        harness.Engine.SubAddHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Assert.IsType<SubtitleImportContext>(session.BeginSubtitleImport());
        await session.ImportSubtitlesAsync(context, [new(harness.SubtitleSource("first.srt")),
            new(harness.SubtitleSource("second.srt", "1\n00:00:00,000 --> 00:00:20,000\n第二种字幕\n"))], TestContext.Current.CancellationToken);
        await harness.Engine.SubAddEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await session.CloseAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
        Assert.True(harness.Engine.Disposed);
        Assert.Single(harness.Engine.Commands, args => args[0] == "sub-add");
    }

    [Fact]
    public async Task LocalSubtitleIndexNeverBecomesServerStreamIndex()
    {
        await using var harness = new Harness();
        harness.Preparer.MediaStreams = [new() { Index = 0, Type = "Subtitle", Codec = "subrip" }];
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        EmitTracks(harness.Engine, SubtitleTrack(1, "zh", "内封", "subrip"));
        await UntilAsync(() => session.Snapshot.SelectedSubtitleTrackId == "1");
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => harness.Reports.Any(report => report.Kind == "Progress"));
        Assert.Equal(0, harness.Reports.Last().Body.GetProperty("SubtitleStreamIndex").GetInt32());
        var context = Assert.IsType<SubtitleImportContext>(session.BeginSubtitleImport());
        await session.ImportSubtitlesAsync(context, [new(harness.SubtitleSource("entry-0.srt"))], TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.SubtitleTracks.Any(track => track.Source == TrackSource.Local && track.Id == session.Snapshot.SelectedSubtitleTrackId));
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => harness.Reports.Count(report => report.Kind == "Progress") == 2);
        Assert.False(harness.Reports.Last().Body.TryGetProperty("SubtitleStreamIndex", out _));
        Assert.False(harness.Reports.Last().Body.TryGetProperty("SubtitleOffset", out _));
    }

    private static MpvValue.Map SubtitleTrack(int id, string language, string title, string codec) => new(new Dictionary<string, MpvValue?>
    {
        ["id"] = new MpvValue.WholeNumber(id), ["type"] = new MpvValue.Text("sub"),
        ["lang"] = new MpvValue.Text(language), ["title"] = new MpvValue.Text(title), ["codec"] = new MpvValue.Text(codec),
        ["ff-index"] = new MpvValue.WholeNumber(0),
    });

    private sealed class TestSubtitleResolver : ILocalSubtitleTargetResolver
    {
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ImmutableDictionary<int, string>> ResolveAsync(AccountSession account, PlaybackEntry context,
            ImmutableArray<SubtitleImportCandidate> candidates, CancellationToken token)
        {
            Entered.TrySetResult();
            if (Hold is { } hold) await hold.Task.WaitAsync(token);
            return candidates.ToImmutableDictionary(candidate => candidate.InputIndex, candidate =>
                candidate.FileName.StartsWith("entry-", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(candidate.FileName) : context.ItemId);
        }
    }
}

