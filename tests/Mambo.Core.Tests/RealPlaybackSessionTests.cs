using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class RealPlaybackSessionTests
{
    private static readonly string[] ReportKinds = ["Playing", "Progress", "Progress", "Progress", "Stopped"];
    private static readonly string[] ProgressNames = ["Pause", "TimeUpdate", "Unpause"];
    private static readonly string[] CloseLifecycle = ["stop", "detach-start", "detach-complete", "dispose"];
    [Fact]
    public async Task VideoQualityPreparesBeforeLoadAndRenderingConfirmationDoesNotBlockPlaybackCommands()
    {
        await using var harness = new Harness(videoQuality: true);
        await harness.PreferAsync(VideoQualityMode.Clear);
        var session = await harness.StartAsync();
        var engine = harness.QualityEngine;
        Assert.Equal(VideoQualityMode.Clear, Assert.Single(engine.Prepared));
        Assert.StartsWith("quality.prepare:", harness.NetworkAndLoads.First(), StringComparison.Ordinal);
        var hold = engine.Hold(VideoQualityMode.Clear);
        await harness.ConfirmAsync(session);
        await UntilAsync(() => engine.Applied.Contains(VideoQualityMode.Clear));
        Assert.True(session.Snapshot.IsVideoQualityChanging);
        Assert.Equal(VideoQualityMode.Standard, session.Snapshot.VideoQualityMode);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        await session.SeekAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(session.Snapshot.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(12).Ticks, session.Snapshot.PositionTicks);
        hold.TrySetResult();
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        Assert.Equal(VideoQualityMode.Clear, session.Snapshot.VideoQualityMode);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
        Assert.Single(engine.Loads);
    }

    [Fact]
    public async Task VideoQualityChoicePersistsOnlyAfterSuccessfulApplyAndDoesNotReloadOrResetControls()
    {
        await using var harness = new Harness(videoQuality: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        await session.SetRateAsync(1.5, TestContext.Current.CancellationToken);
        await session.SetVolumeAsync(37, TestContext.Current.CancellationToken);
        var engine = harness.QualityEngine;
        var hold = engine.Hold(VideoQualityMode.Anime);
        var selection = session.SetVideoQualityModeAsync(VideoQualityMode.Anime, TestContext.Current.CancellationToken);
        await UntilAsync(() => engine.Applied.Contains(VideoQualityMode.Anime));
        Assert.Equal(VideoQualityMode.Standard, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        Assert.True(session.Snapshot.IsVideoQualityChanging);
        hold.TrySetResult();
        await selection;
        Assert.Equal(VideoQualityMode.Anime, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        Assert.Equal(VideoQualityMode.Anime, session.Snapshot.VideoQualityMode);
        Assert.True(session.Snapshot.IsPaused);
        Assert.Equal(1.5, session.Snapshot.PlaybackRate);
        Assert.Equal(37, session.Snapshot.Volume);
        Assert.Single(engine.Loads);
        engine.ApplyFailureMode = VideoQualityMode.Clear;
        await Assert.ThrowsAsync<AppException>(() => session.SetVideoQualityModeAsync(VideoQualityMode.Clear, TestContext.Current.CancellationToken));
        Assert.Equal(VideoQualityMode.Anime, session.Snapshot.VideoQualityMode);
        Assert.Null(session.Snapshot.VideoQualityError);
        Assert.Equal(VideoQualityMode.Anime, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
    }

    [Fact]
    public async Task AutomaticQualityFailureIsNonFatalAndRetainsTheSavedPreference()
    {
        await using var harness = new Harness(videoQuality: true);
        await harness.PreferAsync(VideoQualityMode.Anime);
        var session = await harness.StartAsync();
        harness.QualityEngine.ApplyFailureMode = VideoQualityMode.Anime;
        await harness.ConfirmAsync(session);
        await UntilAsync(() => session.Snapshot.VideoQualityError is not null);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
        Assert.Null(session.Snapshot.Error);
        Assert.False(session.Snapshot.IsVideoQualityChanging);
        Assert.Equal(VideoQualityMode.Standard, session.Snapshot.VideoQualityMode);
        Assert.Equal(VideoQualityMode.Anime, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        await session.SetVideoQualityModeAsync(VideoQualityMode.Standard, TestContext.Current.CancellationToken);
        Assert.Null(session.Snapshot.VideoQualityError);
        Assert.Equal(VideoQualityMode.Standard, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
    }

    [Fact]
    public async Task RestoringNewMovieQualityFailureUsesStandardRatherThanThePreviousMoviesEnhancement()
    {
        await using var harness = new Harness(entryCount: 2, videoQuality: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging && harness.Engine.Loads.Count == 2);
        var first = session.Snapshot.Entry!;
        var next = session.Snapshot.Entries[1];
        await session.SetVideoQualityModeAsync(VideoQualityMode.Anime, TestContext.Current.CancellationToken);
        await harness.QualityPreferences.SaveAsync(harness.Account, next, VideoQualityMode.Clear, TestContext.Current.CancellationToken);
        harness.QualityEngine.ApplyFailureMode = VideoQualityMode.Clear;
        var fallback = harness.QualityEngine.Hold(VideoQualityMode.Standard);
        await session.NextAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.Entry?.ItemId == next.ItemId);
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.QualityEngine.Applied.Count(mode => mode == VideoQualityMode.Standard) == 2);
        Assert.True(session.Snapshot.IsVideoQualityChanging);
        fallback.TrySetResult();
        await UntilAsync(() => session.Snapshot.VideoQualityError is not null && !session.Snapshot.IsVideoQualityChanging);
        Assert.Equal(VideoQualityMode.Standard, session.Snapshot.VideoQualityMode);
        Assert.Equal(VideoQualityMode.Standard, harness.QualityEngine.Applied.Last());
        Assert.Equal(VideoQualityMode.Anime, harness.QualityPreferences.Get(harness.Account, first));
        Assert.Equal(VideoQualityMode.Clear, harness.QualityPreferences.Get(harness.Account, next));
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
        Assert.Null(session.Snapshot.Error);
    }

    [Fact]
    public async Task MissingQualityResourcesFallBackToStandardBeforeLoadingWithoutErasingThePreference()
    {
        await using var harness = new Harness(videoQuality: true, prepareFailureMode: VideoQualityMode.Anime);
        await harness.PreferAsync(VideoQualityMode.Anime);
        var session = await harness.StartAsync();
        Assert.Equal([VideoQualityMode.Anime, VideoQualityMode.Standard], harness.QualityEngine.Prepared.ToArray());
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        Assert.Equal(VideoQualityMode.Standard, session.Snapshot.VideoQualityMode);
        Assert.NotNull(session.Snapshot.VideoQualityError);
        Assert.Null(session.Snapshot.Error);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
        Assert.Equal(VideoQualityMode.Anime, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        Assert.Single(harness.Engine.Loads);
    }

    [Fact]
    public async Task PreparingNextEpisodeDoesNotChangeQualityAndTheConfirmedSeriesChoiceFollowsStartFile()
    {
        await using var harness = new Harness(entryCount: 2, videoQuality: true, seriesId: "series");
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging && harness.Engine.Loads.Count == 2);
        await session.SetVideoQualityModeAsync(VideoQualityMode.Anime, TestContext.Current.CancellationToken);
        Assert.Single(harness.QualityEngine.Prepared);
        var applyCount = harness.QualityEngine.Applied.Count;
        Assert.Equal("entry-0", session.Snapshot.Entry!.ItemId);
        await session.NextAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.Entry?.ItemId == "entry-1");
        Assert.Equal(applyCount, harness.QualityEngine.Applied.Count);
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        Assert.Equal(VideoQualityMode.Anime, harness.QualityEngine.Applied.Last());
        Assert.Equal(VideoQualityMode.Anime, session.Snapshot.VideoQualityMode);
        Assert.Single(harness.QualityEngine.Prepared);
    }

    [Fact]
    public async Task SupersededQualityResultCannotOverwriteTheLatestChoiceOrPersistItsPreference()
    {
        await using var harness = new Harness(videoQuality: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        var engine = harness.QualityEngine;
        var oldGate = engine.Hold(VideoQualityMode.Anime, ignoreCancellation: true);
        var old = session.SetVideoQualityModeAsync(VideoQualityMode.Anime, TestContext.Current.CancellationToken);
        await UntilAsync(() => engine.Applied.Contains(VideoQualityMode.Anime));
        var latest = session.SetVideoQualityModeAsync(VideoQualityMode.Clear, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        oldGate.TrySetResult();
        await latest;
        Assert.Equal(VideoQualityMode.Clear, session.Snapshot.VideoQualityMode);
        Assert.Equal(VideoQualityMode.Clear, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        Assert.Equal(VideoQualityMode.Clear, engine.Applied.Last());
        Assert.Equal(1, engine.MaximumConcurrentApplies);
    }

    [Fact]
    public async Task ChangingEntryCancelsPendingQualityChoiceAndDoesNotSaveItForEitherMovie()
    {
        await using var harness = new Harness(entryCount: 2, videoQuality: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging && harness.Engine.Loads.Count == 2);
        var first = session.Snapshot.Entry!;
        var gate = harness.QualityEngine.Hold(VideoQualityMode.Anime, ignoreCancellation: true);
        var old = session.SetVideoQualityModeAsync(VideoQualityMode.Anime, TestContext.Current.CancellationToken);
        await UntilAsync(() => harness.QualityEngine.Applied.Contains(VideoQualityMode.Anime));
        await session.NextAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        await UntilAsync(() => session.Snapshot.Entry?.ItemId == "entry-1");
        gate.TrySetResult();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        Assert.Equal(VideoQualityMode.Standard, session.Snapshot.VideoQualityMode);
        Assert.Equal(VideoQualityMode.Standard, harness.QualityPreferences.Get(harness.Account, first));
        Assert.Equal(VideoQualityMode.Standard, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
    }

    [Fact]
    public async Task CancelledQualityCommandClearsChangingStateWithoutSavingOrFailingPlayback()
    {
        await using var harness = new Harness(videoQuality: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        harness.QualityEngine.Hold(VideoQualityMode.Anime);
        var selection = session.SetVideoQualityModeAsync(VideoQualityMode.Anime, cancellation.Token);
        await UntilAsync(() => harness.QualityEngine.Applied.Contains(VideoQualityMode.Anime));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.False(session.Snapshot.IsVideoQualityChanging);
        Assert.Null(session.Snapshot.VideoQualityError);
        Assert.Equal(VideoQualityMode.Standard, session.Snapshot.VideoQualityMode);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
        Assert.Equal(VideoQualityMode.Standard, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
    }

    [Fact]
    public async Task FailedRollbackUpdatesActualStandardModeWithoutErasingTheSavedChoice()
    {
        await using var harness = new Harness(videoQuality: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        await session.SetVideoQualityModeAsync(VideoQualityMode.Anime, TestContext.Current.CancellationToken);
        harness.QualityEngine.ApplyFailureMode = VideoQualityMode.Clear;
        harness.QualityEngine.ResetToStandardOnFailure = true;
        var failure = await Assert.ThrowsAsync<AppException>(() => session.SetVideoQualityModeAsync(VideoQualityMode.Clear, TestContext.Current.CancellationToken));
        Assert.Equal("player.video_quality_reset_to_standard", failure.Error.Code);
        Assert.Equal(VideoQualityMode.Standard, session.Snapshot.VideoQualityMode);
        Assert.Equal(VideoQualityMode.Anime, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
    }
    [Fact]
    public async Task EnhancementLostAfterConfirmationFallsBackToStandardWithoutErasingTheSavedChoice()
    {
        await using var harness = new Harness(videoQuality: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !session.Snapshot.IsVideoQualityChanging);
        await session.SetVideoQualityModeAsync(VideoQualityMode.Anime, TestContext.Current.CancellationToken);
        harness.Engine.Emit(new EngineEvent.VideoQualityLost());
        await UntilAsync(() => session.Snapshot.VideoQualityMode == VideoQualityMode.Standard && !session.Snapshot.IsVideoQualityChanging);
        Assert.Equal("player.video_quality_lost", session.Snapshot.VideoQualityError?.Code);
        Assert.Equal(VideoQualityMode.Standard, harness.QualityEngine.Applied.Last());
        Assert.Equal(VideoQualityMode.Anime, harness.QualityPreferences.Get(harness.Account, session.Snapshot.Entry!));
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
        Assert.Null(session.Snapshot.Error);
        Assert.Single(harness.Engine.Loads);
        await session.SetVideoQualityModeAsync(VideoQualityMode.Clear, TestContext.Current.CancellationToken);
        Assert.Null(session.Snapshot.VideoQualityError);
        Assert.Equal(VideoQualityMode.Clear, session.Snapshot.VideoQualityMode);
    }

    [Fact]
    public async Task RestartBeforeFileLoadedDoesNotConfirmAndUnconfirmedCloseNeverReportsStopped()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        var engine = harness.Engine;
        engine.Emit(new EngineEvent.PlaybackRestart());
        await session.SetVolumeAsync(65, TestContext.Current.CancellationToken);
        Assert.Equal(PlayerPhase.Opening, session.Snapshot.Phase);
        Assert.Empty(harness.Reports);
        await session.CloseAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(harness.Reports, report => report.Kind == "Stopped");
        Assert.Empty(harness.Outbox.Snapshot);
    }

    [Fact]
    public async Task UnconfirmedErrorTriesNextCandidateWithItsOwnNativeId()
    {
        await using var harness = new Harness(candidateCount: 2);
        var session = await harness.StartAsync();
        var first = harness.Engine.Loads.Single();
        harness.Engine.Emit(new EngineEvent.EndFile(first.Id, EngineEndReason.Error, -13));
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        var second = harness.Engine.Loads.Last();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, second.CandidateIndex);
        Assert.Empty(harness.Reports);
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Reports.Count(report => report.Kind == "Playing") == 1);
        Assert.Equal("playback.candidate.end-file", Assert.Single(harness.Diagnostics).Stage);
        Assert.Null(session.Snapshot.Error);
        await session.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Single(harness.Reports, report => report.Kind == "Stopped");
    }

    [Fact]
    public async Task ExhaustedCandidatesLogEachNativeFailureOnceAndLinkFinalError()
    {
        await using var harness = new Harness(candidateCount: 2);
        var session = await harness.StartAsync();
        var first = harness.Engine.Loads.Single();
        harness.Engine.Emit(new EngineEvent.EndFile(first.Id, EngineEndReason.Error, -13));
        harness.Engine.Emit(new EngineEvent.EndFile(first.Id, EngineEndReason.Error, -13));
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        var second = harness.Engine.Loads.Last();
        harness.Engine.Emit(new EngineEvent.EndFile(second.Id, EngineEndReason.Error, -14));
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);

        var diagnostics = harness.Diagnostics.ToArray();
        Assert.Equal(3, diagnostics.Length);
        Assert.Equal("playback.candidate.end-file", diagnostics[0].Stage);
        Assert.Contains("候选 1，原生错误 -13", diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(-13, diagnostics[0].Status);
        Assert.Equal("playback.candidate.end-file", diagnostics[1].Stage);
        Assert.Contains("候选 2，原生错误 -14", diagnostics[1].Message, StringComparison.Ordinal);
        Assert.Equal(-14, diagnostics[1].Status);
        Assert.NotEqual(diagnostics[0].DiagnosticId, diagnostics[1].DiagnosticId);
        Assert.True(Guid.TryParseExact(diagnostics[1].DiagnosticId, "N", out _));
        var finalError = Assert.IsType<AppError>(session.Snapshot.Error);
        Assert.Same(finalError, diagnostics[2]);
        Assert.Equal("playback.candidates.exhausted", finalError.Stage);
        Assert.Equal(ErrorCodes.PlaybackFailed, finalError.Code);
        Assert.Equal(-14, finalError.Status);
        Assert.True(finalError.Retryable);
        Assert.Equal(diagnostics[1].DiagnosticId, finalError.DiagnosticId);
        Assert.Contains("片源无法播放，请重试。", finalError.Message, StringComparison.Ordinal);
        Assert.Contains("错误代码：-14", finalError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(finalError.DiagnosticId!, finalError.Message, StringComparison.Ordinal);
        Assert.All(diagnostics, error =>
        {
            Assert.DoesNotContain("entry-", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("测试条目", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(".invalid", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("://", error.Message, StringComparison.Ordinal);
        });
        Assert.Empty(harness.Reports);
        Assert.Empty(harness.Outbox.Snapshot);
    }

    [Fact]
    public async Task EmptyCandidateListLogsFinalFailureWithoutInventingNativeError()
    {
        await using var harness = new Harness(candidateCount: 0);
        var session = await harness.Coordinator.PlayAsync(new("entry-0"), TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);
        var error = Assert.IsType<AppError>(session.Snapshot.Error);
        Assert.Same(error, Assert.Single(harness.Diagnostics));
        Assert.Equal("playback.candidates.exhausted", error.Stage);
        Assert.Null(error.Status);
        Assert.True(Guid.TryParseExact(error.DiagnosticId, "N", out _));
        Assert.Equal("片源无法播放，请重试。", error.Message);
        Assert.DoesNotContain(error.DiagnosticId!, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("原生错误", error.Message, StringComparison.Ordinal);
        Assert.True(error.Retryable);
        Assert.Empty(harness.Engine.Loads);
    }

    [Fact]
    public async Task ConfirmedPlaybackFailureLogsNativeAndFinalDiagnostic()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        harness.Engine.Emit(new EngineEvent.EndFile(harness.Engine.ActiveId, EngineEndReason.Error, -13));
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);
        var diagnostics = harness.Diagnostics.ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Equal("playback.candidate.end-file", diagnostics[0].Stage);
        var error = Assert.IsType<AppError>(session.Snapshot.Error);
        Assert.Same(error, diagnostics[1]);
        Assert.Equal("playback.end-file", error.Stage);
        Assert.Equal(-13, error.Status);
        Assert.Equal(diagnostics[0].DiagnosticId, error.DiagnosticId);
        Assert.Contains("播放意外中断，已保存最新进度。", error.Message, StringComparison.Ordinal);
        Assert.Contains("错误代码：-13", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(error.DiagnosticId!, error.Message, StringComparison.Ordinal);
        Assert.Single(harness.Engine.Loads);
    }

    [Fact]
    public async Task NativeErrorWhileClosingDoesNotCreateFailureDiagnostic()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        harness.Engine.AutoEndOnStop = false;
        var close = session.CloseAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Closing);
        harness.Engine.Emit(new EngineEvent.EndFile(harness.Engine.ActiveId, EngineEndReason.Error, -13));
        await close.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
        Assert.Null(session.Snapshot.Error);
        Assert.Empty(harness.Diagnostics);
        Assert.Single(harness.Engine.Loads);
        Assert.Empty(harness.Reports);
    }

    [Fact]
    public async Task PlayingPauseSeekAndStoppedAreOrderedAndUseActualRateAndPosition()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => !harness.Reports.IsEmpty);
        await session.SetRateAsync(1.5, TestContext.Current.CancellationToken);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        await session.SeekAsync(TimeSpan.FromSeconds(37.125), TestContext.Current.CancellationToken);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        await session.CloseAsync(TestContext.Current.CancellationToken);
        var reports = harness.Reports.ToArray();
        Assert.Equal(ReportKinds, reports.Select(report => report.Kind));
        Assert.Equal(ProgressNames, reports.Where(report => report.Kind == "Progress").Select(report => report.Text("EventName")));
        Assert.All(reports.Where(report => report.Kind == "Progress"), report => Assert.Equal(1.5, report.Number("PlaybackRate")));
        Assert.True(reports[1].Boolean("IsPaused"));
        Assert.False(reports[3].Boolean("IsPaused"));
        Assert.Equal(TimeSpan.FromSeconds(37.125).Ticks, reports[^1].Integer("PositionTicks"));
        Assert.Equal(reports[0].Integer("PlaybackStartTimeTicks"), reports[^1].Integer("PlaybackStartTimeTicks"));
        Assert.Empty(harness.Outbox.Snapshot);
    }

    [Fact]
    public async Task AppendedNextEntryStartsByIdAndSeasonEndCloses()
    {
        await using var harness = new Harness(entryCount: 2);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        var first = harness.Engine.Loads.First();
        var next = harness.Engine.Loads.Last();
        Assert.Equal(LoadMode.Append, next.Mode);
        harness.Engine.Emit(new EngineEvent.EndFile(first.Id, EngineEndReason.Eof, 0));
        harness.Engine.Emit(new EngineEvent.StartFile(next.Id));
        await UntilAsync(() => session.Snapshot.CurrentEntryIndex == 1);
        await harness.ConfirmAsync(session);
        harness.Engine.Emit(new EngineEvent.EndFile(next.Id, EngineEndReason.Eof, 0));
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Closed);
        Assert.Null(harness.Coordinator.Current);
        Assert.Equal(2, harness.Reports.Count(report => report.Kind == "Playing"));
        Assert.Equal(2, harness.Reports.Count(report => report.Kind == "Stopped"));
    }

    [Fact]
    public async Task ManualNextToAppendedEntryDoesNotLeaveSwitchingSetAtSeasonEnd()
    {
        await using var harness = new Harness(entryCount: 2);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        await session.NextAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => session.Snapshot.CurrentEntryIndex == 1);
        await harness.ConfirmAsync(session);
        harness.Engine.Emit(new EngineEvent.EndFile(harness.Engine.ActiveId, EngineEndReason.Eof, 0));
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Closed);
        Assert.Equal(2, harness.Reports.Count(report => report.Kind == "Stopped"));
        Assert.Empty(harness.Diagnostics);
    }

    [Fact]
    public async Task ProgressBeginsOnlyAfterConfirmationAndHasTenSecondInterval()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await session.SetVolumeAsync(60, TestContext.Current.CancellationToken);
        Assert.Empty(harness.Reports);
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Reports.Any(report => report.Kind == "Playing"));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(9999));
        await session.SetVolumeAsync(61, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(harness.Reports, report => report.Kind == "Progress");
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await UntilAsync(() => harness.Reports.Any(report => report.Kind == "Progress"));
        Assert.Single(harness.Reports, report => report.Kind == "Progress");
        Assert.Equal("TimeUpdate", harness.Reports.Last().Text("EventName"));
    }

    [Fact]
    public async Task TwentySecondOpeningFlagClearsWhenPlaybackIsConfirmed()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        Assert.False(session.Snapshot.IsSlowOpening);
        harness.Clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => session.Snapshot.IsSlowOpening);
        Assert.Equal(PlayerPhase.Opening, session.Snapshot.Phase);
        await harness.ConfirmAsync(session);
        Assert.False(session.Snapshot.IsSlowOpening);
    }

    [Fact]
    public async Task QueueOverflowAcceptsRefreshedPropertiesWithoutStoppingPlayback()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        harness.Engine.Emit(new EngineEvent.QueueOverflow());
        harness.Engine.Emit(new EngineEvent.PropertyChanged(EngineProperty.Volume, new MpvValue.Number(46)));
        await UntilAsync(() => session.Snapshot.Volume == 46);
        Assert.Equal(PlayerPhase.Playing, session.Snapshot.Phase);
        Assert.DoesNotContain(harness.Reports, report => report.Kind == "Stopped");
    }

    [Fact]
    public async Task EofDuringNextPreparationReusesWorkAndLoadsNextOnlyOnce()
    {
        await using var harness = new Harness(entryCount: 2);
        var gate = harness.Preparer.Hold("entry-1");
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Preparer.Count("entry-1") == 1);
        var first = harness.Engine.Loads.First();
        harness.Engine.Emit(new EngineEvent.EndFile(first.Id, EngineEndReason.Eof, 0));
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Interstitial);
        gate.TrySetResult();
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        Assert.Equal(1, harness.Preparer.Count("entry-1"));
        Assert.Single(harness.Engine.Loads, load => load.ItemId == "entry-1");
        Assert.Equal(LoadMode.Replace, harness.Engine.Loads.Last().Mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseWaitsForEndOrTimeoutThenDetachesBeforeDisposal(bool useTimeout)
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        var engine = harness.Engine;
        engine.AutoEndOnStop = false;
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Detaching += async () =>
        {
            engine.Lifecycle.Enqueue("detach-start");
            await detached.Task;
            engine.Lifecycle.Enqueue("detach-complete");
        };
        var close = session.CloseAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => engine.Commands.Any(command => command[0] == "stop"));
        // 这个拒绝的命令是 actor 屏障，保证关闭计时器已创建再推进虚拟时钟。
        await Assert.ThrowsAsync<AppException>(() => session.SetVolumeAsync(60, TestContext.Current.CancellationToken));
        Assert.False(close.IsCompleted);
        Assert.False(engine.Disposed);
        if (useTimeout) harness.Clock.Advance(TimeSpan.FromSeconds(2));
        else engine.Emit(new EngineEvent.EndFile(engine.ActiveId, EngineEndReason.Stop, 0));
        await UntilAsync(() => engine.Lifecycle.Contains("detach-start"));
        Assert.False(engine.Disposed);
        detached.TrySetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(CloseLifecycle, engine.Lifecycle);
        Assert.Single(harness.Reports, report => report.Kind == "Stopped");
    }

    [Fact]
    public async Task ShutdownReportsStoppedOnceAndRetryStartsAtLastKnownPosition()
    {
        await using var harness = new Harness();
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await session.SetRateAsync(1.5, TestContext.Current.CancellationToken);
        await session.SetVolumeAsync(36, TestContext.Current.CancellationToken);
        await session.SetMutedAsync(true, TestContext.Current.CancellationToken);
        harness.Engine.Emit(new EngineEvent.PropertyChanged(EngineProperty.TimePosition, new MpvValue.Number(42.5)));
        await UntilAsync(() => session.Snapshot.PositionTicks == TimeSpan.FromSeconds(42.5).Ticks);
        harness.Engine.Emit(new EngineEvent.Shutdown());
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);
        Assert.Contains(harness.Diagnostics, error => error.Message == "播放器已退出。");
        await UntilAsync(() => harness.Reports.Count(report => report.Kind == "Stopped") == 1);
        var old = harness.Engine;
        await session.RetryAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => harness.Engines.Count == 2 && harness.Engine.Loads.Count == 1);
        Assert.True(old.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(42.5).Ticks, harness.Preparer.Starts.Last());
        Assert.Single(harness.Reports, report => report.Kind == "Stopped");
        await harness.ConfirmAsync(session);
        Assert.Equal(1.5, session.Snapshot.PlaybackRate);
        Assert.Equal(36, session.Snapshot.Volume);
        Assert.True(session.Snapshot.IsMuted);
        Assert.Equal(1.5, Assert.IsType<MpvValue.Number>(harness.Engine.Values["speed"]).Value);
        Assert.Equal(36, Assert.IsType<MpvValue.Number>(harness.Engine.Values["volume"]).Value);
        Assert.True(Assert.IsType<MpvValue.Flag>(harness.Engine.Values["mute"]).Value);
        await UntilAsync(() => harness.Reports.Count(report => report.Kind == "Playing") == 2);
        var restarted = harness.Reports.Last(report => report.Kind == "Playing");
        Assert.Equal(1.5, restarted.Number("PlaybackRate"));
        Assert.Equal(36, restarted.Number("VolumeLevel"));
        Assert.True(restarted.Boolean("IsMuted"));
    }

    [Fact]
    public async Task OlderPreparationResultCannotReplaceExplicitSelection()
    {
        await using var harness = new Harness(entryCount: 3);
        var delayed = harness.Preparer.Hold("entry-1", ignoreCancellation: true);
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Preparer.Count("entry-1") == 1);
        await session.SelectEntryAsync("entry-2", TestContext.Current.CancellationToken);
        await UntilAsync(() => harness.Engine.Loads.Any(load => load.ItemId == "entry-2"));
        delayed.TrySetResult();
        await UntilAsync(() => session.Snapshot.Entry?.ItemId == "entry-2");
        await session.SetVolumeAsync(62, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(harness.Engine.Loads, load => load.ItemId == "entry-1");
        Assert.Equal("entry-2", session.Snapshot.Entry?.ItemId);
    }

    [Fact]
    public async Task CoordinatorReusesSameItemAndRejectsUnconfirmedReplacement()
    {
        await using var harness = new Harness(entryCount: 2);
        var first = await harness.StartAsync();
        var same = await harness.Coordinator.PlayAsync(new("entry-0", 999), TestContext.Current.CancellationToken);
        Assert.Same(first, same);
        var error = await Assert.ThrowsAsync<AppException>(() => harness.Coordinator.PlayAsync(new("entry-1"), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ReplaceConfirmationRequired, error.Error.Code);
        Assert.Same(first, harness.Coordinator.Current);
        Assert.Single(harness.Engines);
    }

    [Fact]
    public async Task CloseDuringPlanResolutionCancelsPreparationAndDisposesUnattachedEngine()
    {
        await using var harness = new Harness();
        harness.Preparer.HoldPlan();
        var session = Assert.IsType<PlaybackSession>(await harness.Coordinator.PlayAsync(
            new("entry-0"), TestContext.Current.CancellationToken));
        await UntilAsync(() => harness.Engines.Count == 1);
        Assert.Null(session.Engine);
        Assert.Empty(harness.Engine.Loads);
        await session.CloseAsync(TestContext.Current.CancellationToken).WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(harness.Preparer.PlanCancelled.Task.IsCompletedSuccessfully);
        Assert.True(harness.Engine.Disposed);
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
        Assert.Empty(harness.Reports);
        Assert.Empty(harness.Diagnostics);
    }

    [Fact]
    public async Task UnconfirmedTranscodeCloseCleansEncodingWithoutPlayingOrStopped()
    {
        await using var harness = new Harness();
        harness.Preparer.SetTranscode("entry-0");
        var session = await harness.StartAsync();
        await session.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Equal("session-entry-0", Assert.Single(harness.Cleanups));
        Assert.Empty(harness.Reports);
        Assert.Empty(harness.Outbox.Snapshot);
    }

    [Fact]
    public async Task CloseAlsoCleansAppendedTranscodeThatNeverStarted()
    {
        await using var harness = new Harness(entryCount: 2);
        harness.Preparer.SetTranscode("entry-1");
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        Assert.Equal(LoadMode.Append, harness.Engine.Loads.Last().Mode);
        await session.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Equal("session-entry-1", Assert.Single(harness.Cleanups));
        Assert.All(harness.Reports, report => Assert.Equal("entry-0", report.Text("ItemId")));
        Assert.Single(harness.Reports, report => report.Kind == "Stopped");
    }

    [Fact]
    public async Task UnconfirmedTranscodeErrorCleansOldEncodingBeforeLoadingFallback()
    {
        await using var harness = new Harness(candidateCount: 2);
        harness.Preparer.SetTranscode("entry-0", firstCandidateOnly: true);
        var session = await harness.StartAsync();
        harness.Engine.Emit(new EngineEvent.EndFile(harness.Engine.ActiveId, EngineEndReason.Error, -13));
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        var actions = harness.NetworkAndLoads.ToArray();
        Assert.Equal("load:entry-0:0", actions[0]);
        Assert.Equal("cleanup:session-entry-0", actions[1]);
        Assert.Equal("load:entry-0:1", actions[2]);
        Assert.Empty(harness.Reports);
        await session.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Equal("session-entry-0", Assert.Single(harness.Cleanups));
        Assert.Empty(harness.Reports);
    }

    [Fact]
    public async Task RetryAfterShutdownCleansPreviouslyAppendedTranscodeWithoutReportingThatEntry()
    {
        await using var harness = new Harness(entryCount: 2);
        harness.Preparer.SetTranscode("entry-1");
        var session = await harness.StartAsync();
        await harness.ConfirmAsync(session);
        await UntilAsync(() => harness.Engine.Loads.Count == 2);
        var oldEngine = harness.Engine;
        oldEngine.Emit(new EngineEvent.Shutdown());
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);
        await session.RetryAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => harness.Engines.Count == 2 && harness.Engine.Loads.Count == 1);
        Assert.True(oldEngine.Disposed);
        Assert.Equal("session-entry-1", Assert.Single(harness.Cleanups));
        Assert.DoesNotContain(harness.Reports, report => report.Text("ItemId") == "entry-1");
        Assert.Single(harness.Reports, report => report.Kind == "Stopped");
    }

    [Fact]
    public async Task RetryAfterExhaustedUnconfirmedTranscodeCleansOldEncodingWithoutStopReport()
    {
        await using var harness = new Harness();
        harness.Preparer.SetTranscode("entry-0");
        var session = await harness.StartAsync();
        var oldEngine = harness.Engine;
        oldEngine.Emit(new EngineEvent.EndFile(oldEngine.ActiveId, EngineEndReason.Error, -13));
        await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);
        await session.RetryAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => harness.Engines.Count == 2 && harness.Engine.Loads.Count == 1);
        Assert.True(oldEngine.Disposed);
        Assert.Equal("session-entry-0", Assert.Single(harness.Cleanups));
        Assert.Empty(harness.Reports);
        Assert.Empty(harness.Outbox.Snapshot);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AppPaths paths = new(Path.Combine(Path.GetTempPath(), "mambo-session-" + Guid.NewGuid().ToString("N")));
        private readonly AccountContext accounts = new();
        private readonly SettingsStore settings;
        public SettingsStore Settings => settings;
        public AccountSession Account => accounts.Current!;
        public VideoQualityPreferences QualityPreferences { get; }
        private readonly EmbyApi api;
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        public QueueScheduler Scheduler { get; } = new();
        public ConcurrentQueue<Report> Reports { get; } = new();
        public ConcurrentQueue<AppError> Diagnostics { get; } = new();
        public ConcurrentQueue<string> Cleanups { get; } = new();
        public ConcurrentQueue<string> NetworkAndLoads { get; } = new();
        public ConcurrentQueue<FakeEngine> Engines { get; } = new();
        public FakeEngine Engine => Engines.Last();
        public FakeVideoQualityEngine QualityEngine => Assert.IsType<FakeVideoQualityEngine>(Engine);
        public FakePreparer Preparer { get; }
        public StopOutbox Outbox { get; }
        public PlaybackCoordinator Coordinator { get; }

        public Harness(int entryCount = 1, int candidateCount = 1, bool videoQuality = false, string? seriesId = null,
            VideoQualityMode? prepareFailureMode = null)
        {
            accounts.Set(new(new("https://" + Guid.NewGuid().ToString("N") + ".invalid", Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), "测试用户", Guid.NewGuid().ToString("N"))));
            api = new(Guid.NewGuid(), new Handler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Delete)
                {
                    Assert.EndsWith("/Videos/ActiveEncodings", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
                    var sessionId = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?').Split('&')
                        .Single(parameter => parameter.StartsWith("PlaySessionId=", StringComparison.Ordinal))["PlaySessionId=".Length..]);
                    Cleanups.Enqueue(sessionId);
                    NetworkAndLoads.Enqueue("cleanup:" + sessionId);
                }
                if (request.Content is not null)
                {
                    using var json = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(token));
                    var path = request.RequestUri!.AbsolutePath;
                    var kind = path.EndsWith("/Stopped", StringComparison.Ordinal) ? "Stopped" :
                        path.EndsWith("/Progress", StringComparison.Ordinal) ? "Progress" : "Playing";
                    Reports.Enqueue(new(kind, json.RootElement.Clone()));
                }
                return new(HttpStatusCode.OK);
            }));
            settings = new(paths, Scheduler);
            QualityPreferences = new(settings, accounts);
            Outbox = new(paths, api, Clock);
            Preparer = new(entryCount, candidateCount, seriesId);
            Coordinator = new(accounts, Preparer, _ =>
            {
                var engine = videoQuality ? new FakeVideoQualityEngine(NetworkAndLoads) : new FakeEngine(NetworkAndLoads);
                if (engine is FakeVideoQualityEngine quality) quality.PrepareFailureMode = prepareFailureMode;
                engine.EmitInitialControls();
                Engines.Enqueue(engine); return Task.FromResult<IPlayerEngine>(engine);
            }, api, Outbox, settings, Scheduler, new WeakReferenceMessenger(), Clock, Diagnostics.Enqueue, QualityPreferences);
        }
        public Task PreferAsync(VideoQualityMode mode) =>
            QualityPreferences.SaveAsync(Account, new("entry-0", "测试条目 0"), mode, TestContext.Current.CancellationToken);
        public async Task<PlaybackSession> StartAsync()
        {
            var session = Assert.IsType<PlaybackSession>(await Coordinator.PlayAsync(new("entry-0"), TestContext.Current.CancellationToken));
            await UntilAsync(() => !Engines.IsEmpty && !Engine.Loads.IsEmpty && session.Snapshot.Phase == PlayerPhase.Opening);
            Scheduler.Drain();
            return session;
        }
        public async Task ConfirmAsync(PlaybackSession session)
        {
            Engine.Emit(new EngineEvent.FileLoaded());
            Engine.Emit(new EngineEvent.PlaybackRestart());
            await UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Playing);
            Scheduler.Drain();
        }
        public async ValueTask DisposeAsync()
        {
            Preparer.ReleaseAll();
            foreach (var engine in Engines.OfType<FakeVideoQualityEngine>()) engine.ReleaseAll();
            foreach (var engine in Engines) engine.AutoEndOnStop = true;
            if (Coordinator.Current is PlaybackSession session)
            {
                var closing = session.CloseAsync(CancellationToken.None);
                Clock.Advance(TimeSpan.FromSeconds(3));
                foreach (var engine in Engines) engine.Emit(new EngineEvent.EndFile(engine.ActiveId, EngineEndReason.Stop, 0));
                await closing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await Coordinator.DisposeAsync();
            await Outbox.DisposeAsync();
            settings.Dispose(); api.Dispose(); accounts.Dispose();
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private sealed class FakePreparer(int entryCount, int candidateCount, string? seriesId) : IEntryPreparer
    {
        private readonly ImmutableArray<PlaybackEntry> entries = Enumerable.Range(0, entryCount).Select(index =>
            new PlaybackEntry("entry-" + index, "测试条目 " + index) { SeriesId = seriesId, SeasonId = entryCount > 1 ? "season" : null }).ToImmutableArray();
        private readonly ConcurrentDictionary<string, (TaskCompletionSource Source, bool IgnoreCancellation)> held = new();
        private readonly ConcurrentDictionary<string, int> counts = new();
        private readonly ConcurrentDictionary<string, bool> transcode = new();
        private TaskCompletionSource? planGate;
        public TaskCompletionSource PlanCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<long> Starts { get; } = new();
        public int Count(string id) => counts.GetValueOrDefault(id);
        public TaskCompletionSource Hold(string id, bool ignoreCancellation = false)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            held[id] = (gate, ignoreCancellation); return gate;
        }
        public void HoldPlan() => planGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SetTranscode(string itemId, bool firstCandidateOnly = false) => transcode[itemId] = firstCandidateOnly;
        public void ReleaseAll() { planGate?.TrySetResult(); foreach (var pair in held) pair.Value.Source.TrySetResult(); }
        public async Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken)
        {
            if (planGate is { } gate)
                try { await gate.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { PlanCancelled.TrySetResult(); throw; }
            var index = 0;
            for (var i = 0; i < entries.Length; i++) if (entries[i].ItemId == request.ItemId) index = i;
            return new PreparedPlan(entries, index, request.StartTicks ?? 0);
        }
        public async Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken)
        {
            counts.AddOrUpdate(entry.ItemId, 1, (_, count) => count + 1); Starts.Enqueue(startTicks);
            if (held.TryGetValue(entry.ItemId, out var gate))
                await gate.Source.Task.WaitAsync(gate.IgnoreCancellation ? CancellationToken.None : cancellationToken);
            var source = new EmbyMediaSource { Id = "source", MediaStreams = [] };
            var candidates = Enumerable.Range(0, candidateCount).Select(index => new StreamCandidate(
                account.Address.Endpoint("Videos/" + entry.ItemId + "/candidate/" + index),
                transcode.TryGetValue(entry.ItemId, out var firstOnly) && (!firstOnly || index == 0) ? "Transcode" : "DirectPlay", source,
                ImmutableDictionary<string, string>.Empty)).ToImmutableArray();
            return new(entry, new EmbyPlaybackInfo { PlaySessionId = "session-" + entry.ItemId, MediaSources = [source] }, candidates, [], startTicks);
        }
        public Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken)
        {
            var candidate = entry.Candidates[candidateIndex];
            return Task.FromResult(new ResolvedCandidate(candidate, new(candidate.Address, candidate.RequiredHeaders, 0), []));
        }
        public Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry, ResolvedCandidate selectedCandidate, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ResolvedSubtitle>.Empty);
        public Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles) => Task.CompletedTask;
    }

    private class FakeEngine(ConcurrentQueue<string> operationOrder) : IPlayerEngine
    {
        private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>();
        private long nextId;
        public EngineKind Kind => EngineKind.Embedded;
        public ChannelReader<EngineEvent> Events => events.Reader;
        public ConcurrentQueue<Load> Loads { get; } = new();
        public ConcurrentQueue<string[]> Commands { get; } = new();
        public ConcurrentQueue<string> Lifecycle { get; } = new();
        public ConcurrentDictionary<string, MpvValue> Values { get; } = new();
        public bool AutoEndOnStop { get; set; } = true;
        public bool Disposed { get; private set; }
        public long ActiveId { get; private set; }
        public void EmitInitialControls()
        {
            Emit(new EngineEvent.PropertyChanged(EngineProperty.Speed, new MpvValue.Number(1)));
            Emit(new EngineEvent.PropertyChanged(EngineProperty.Volume, new MpvValue.Number(100)));
            Emit(new EngineEvent.PropertyChanged(EngineProperty.Mute, new MpvValue.Flag(false)));
        }
        public void Emit(EngineEvent message)
        {
            if (message is EngineEvent.StartFile start) ActiveId = start.EntryId;
            events.Writer.TryWrite(message);
        }
        public ValueTask<long> LoadAsync(string url, LoadMode mode, IReadOnlyList<KeyValuePair<string, string>> fileOptions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = Interlocked.Increment(ref nextId);
            Loads.Enqueue(new(id, segments[1], int.Parse(segments[^1], System.Globalization.CultureInfo.InvariantCulture), mode));
            operationOrder.Enqueue("load:" + segments[1] + ":" + segments[^1]);
            if (mode == LoadMode.Replace) Emit(new EngineEvent.StartFile(id));
            return ValueTask.FromResult(id);
        }
        public ValueTask CommandAsync(ReadOnlyMemory<string> arguments, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray(); Commands.Enqueue(args);
            if (args[0] == "stop")
            {
                Lifecycle.Enqueue("stop");
                if (AutoEndOnStop) Emit(new EngineEvent.EndFile(ActiveId, EngineEndReason.Stop, 0));
            }
            else if (args[0] == "playlist-next")
            {
                var next = Loads.First(load => load.Id > ActiveId && load.Mode == LoadMode.Append);
                Emit(new EngineEvent.EndFile(ActiveId, EngineEndReason.Stop, 0));
                Emit(new EngineEvent.StartFile(next.Id));
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask SetAsync(string propertyName, MpvValue value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[propertyName] = value;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            if (!Disposed) { Disposed = true; Lifecycle.Enqueue("dispose"); events.Writer.TryComplete(); }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class FakeVideoQualityEngine(ConcurrentQueue<string> operationOrder) : FakeEngine(operationOrder), IVideoQualityEngine
    {
        private readonly ConcurrentQueue<string> qualityOperationOrder = operationOrder;
        private readonly ConcurrentDictionary<VideoQualityMode, (TaskCompletionSource Gate, bool IgnoreCancellation)> holds = new();
        private readonly ConcurrentBag<TaskCompletionSource> allHolds = [];
        private int concurrentApplies;
        public ConcurrentQueue<VideoQualityMode> Prepared { get; } = new();
        public ConcurrentQueue<VideoQualityMode> Applied { get; } = new();
        public VideoQualityMode? ApplyFailureMode { get; set; }
        public VideoQualityMode? PrepareFailureMode { get; set; }
        public bool ResetToStandardOnFailure { get; set; }
        public int MaximumConcurrentApplies { get; private set; }
        public TaskCompletionSource Hold(VideoQualityMode mode, bool ignoreCancellation = false)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            allHolds.Add(gate);
            holds[mode] = (gate, ignoreCancellation);
            return gate;
        }
        public void ReleaseAll() { foreach (var gate in allHolds) gate.TrySetResult(); }
        public ValueTask PrepareVideoQualityAsync(VideoQualityMode mode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prepared.Enqueue(mode); qualityOperationOrder.Enqueue("quality.prepare:" + mode);
            if (PrepareFailureMode == mode) throw new AppException(new(AppErrorKind.Player, "player.video_quality_failed", "测试画质资源缺失。", true));
            return ValueTask.CompletedTask;
        }
        public async ValueTask ApplyVideoQualityAsync(VideoQualityMode mode, CancellationToken cancellationToken)
        {
            MaximumConcurrentApplies = Math.Max(MaximumConcurrentApplies, Interlocked.Increment(ref concurrentApplies));
            try
            {
                Applied.Enqueue(mode);
                if (holds.TryRemove(mode, out var held)) await held.Gate.Task.WaitAsync(held.IgnoreCancellation ? CancellationToken.None : cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (ApplyFailureMode == mode) throw new AppException(new(AppErrorKind.Player,
                    ResetToStandardOnFailure ? "player.video_quality_reset_to_standard" : "player.video_quality_failed", "测试画质应用失败。", true));
            }
            finally { Interlocked.Decrement(ref concurrentApplies); }
        }
    }
    private sealed record Load(long Id, string ItemId, int CandidateIndex, LoadMode Mode);
    private sealed record Report(string Kind, JsonElement Body)
    {
        public string? Text(string key) => Body.GetProperty(key).GetString();
        public double Number(string key) => Body.GetProperty(key).GetDouble();
        public long Integer(string key) => Body.GetProperty(key).GetInt64();
        public bool Boolean(string key) => Body.GetProperty(key).GetBoolean();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class QueueScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> actions = new();
        public bool TryEnqueue(Action action) { actions.Enqueue(action); return true; }
        public void Drain() { while (actions.TryDequeue(out var action)) action(); }
    }
}
