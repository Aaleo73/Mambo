using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class FakePlaybackTests
{
    [Fact]
    public async Task PlaybackControlsPreserveProgressAndNotifyOnUiScheduler()
    {
        using var harness = new Harness();
        var session = await harness.Service.PlayAsync(new PlayRequest("demo-episode-001-01-01", 0), TestContext.Current.CancellationToken);
        Assert.Contains(session.Snapshot.Phase, new[] { PlayerPhase.Preparing, PlayerPhase.Opening });
        Assert.Equal(EngineKind.Demo, session.Snapshot.EngineKind);
        Assert.NotNull(session.Snapshot.DemoColorArgb);
        var changes = 0;
        session.SnapshotChanged += (_, _) => { Assert.True(harness.Scheduler.IsInCallback); changes++; };
        await harness.OpenAsync(session);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, session.Snapshot.PositionTicks);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, session.Snapshot.PositionTicks);
        await session.SetRateAsync(2, TestContext.Current.CancellationToken);
        await session.TogglePauseAsync(TestContext.Current.CancellationToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(6).Ticks, session.Snapshot.PositionTicks);
        await session.SeekAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);
        await session.StepFrameAsync(FrameStepDirection.Forward, TestContext.Current.CancellationToken);
        Assert.True(session.Snapshot.IsPaused);
        Assert.InRange(session.Snapshot.PositionTicks, TimeSpan.FromSeconds(40).Ticks + 1, TimeSpan.FromSeconds(41).Ticks - 1);
        var steppedPosition = session.Snapshot.PositionTicks;
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(steppedPosition, session.Snapshot.PositionTicks);
        await session.SelectAudioTrackAsync("audio-2", TestContext.Current.CancellationToken);
        await session.SelectSubtitleTrackAsync(null, TestContext.Current.CancellationToken);
        await session.SetVolumeAsync(45, TestContext.Current.CancellationToken);
        await session.SetMutedAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal("audio-2", session.Snapshot.SelectedAudioTrackId);
        Assert.Null(session.Snapshot.SelectedSubtitleTrackId);
        Assert.Equal(45, session.Snapshot.Volume);
        Assert.True(session.Snapshot.IsMuted);
        Assert.True(changes > 0);
        await session.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BufferingAndFailureDoNotAdvanceAndRetryKeepsResumePosition()
    {
        using var harness = new Harness();
        var session = (FakePlaybackSession)await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        await session.SimulateBufferingAsync(true, TestContext.Current.CancellationToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(session.Snapshot.IsBuffering);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, session.Snapshot.PositionTicks);
        await session.SimulateBufferingAsync(false, TestContext.Current.CancellationToken);
        await session.SimulateFailureAsync(cancellationToken: TestContext.Current.CancellationToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PlayerPhase.Failed, session.Snapshot.Phase);
        Assert.True(session.Snapshot.Error?.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, session.Snapshot.PositionTicks);
        await session.RetryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlayerPhase.Opening, session.Snapshot.Phase);
        await harness.OpenAsync(session);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(session.Snapshot.Error);
        Assert.Equal(TimeSpan.FromSeconds(6).Ticks, session.Snapshot.PositionTicks);
        await session.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FrequentVolumeChangesDoNotFreezePlaybackBetweenTimerTicks()
    {
        using var harness = new Harness();
        var session = await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        for (var index = 0; index < 20; index++)
        {
            harness.Clock.Advance(TimeSpan.FromMilliseconds(50));
            await session.SetVolumeAsync(index, TestContext.Current.CancellationToken);
        }
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, session.Snapshot.PositionTicks);
        await session.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwelveEpisodeSeasonEndsOnceAndUnsubscribedViewsStayQuiet()
    {
        using var harness = new Harness();
        var stoppedItems = new List<string>();
        var recipient = new object();
        harness.Messenger.Register<PlaybackStopped>(recipient, (_, message) =>
        {
            Assert.True(harness.Scheduler.IsInCallback);
            stoppedItems.Add(message.ItemId);
        });
        var ended = 0;
        harness.Service.SessionEnded += (_, _) => { Assert.True(harness.Scheduler.IsInCallback); ended++; };
        var session = await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        Assert.Equal(12, session.Snapshot.Entries.Length);
        for (var index = 0; index < 12; index++)
        {
            Assert.Equal(index, session.Snapshot.CurrentEntryIndex);
            Assert.Equal(index > 0, session.Snapshot.CanPrevious);
            Assert.Equal(index < 11, session.Snapshot.CanNext);
            await session.SeekAsync(TimeSpan.FromTicks(session.Snapshot.DurationTicks), TestContext.Current.CancellationToken);
            harness.Clock.Advance(harness.Options.PlaybackTick);
            if (index < 11) await harness.OpenAsync(session);
        }
        await session.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
        Assert.Null(harness.Service.Current);
        Assert.Equal(1, ended);
        Assert.Equal(12, stoppedItems.Distinct(StringComparer.Ordinal).Count());
        var count = 0;
        EventHandler handler = (_, _) => count++;
        session.SnapshotChanged += handler;
        session.SnapshotChanged -= handler;
        harness.Messenger.UnregisterAll(recipient);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        await session.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, count);
        Assert.Equal(1, ended);
        Assert.Equal(12, stoppedItems.Count);
        var error = await Assert.ThrowsAsync<AppException>(() => session.TogglePauseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AppErrorKind.Cancelled, error.Error.Kind);
    }

    [Fact]
    public async Task EntryNavigationRequiresReplacementConfirmationAndClosesOldSession()
    {
        using var harness = new Harness();
        var session = await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        await session.NextAsync(TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        Assert.Equal(1, session.Snapshot.CurrentEntryIndex);
        await session.PreviousAsync(TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        await session.SelectEntryAsync("demo-episode-001-01-08", TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        Assert.Equal(7, session.Snapshot.CurrentEntryIndex);
        var error = await Assert.ThrowsAsync<AppException>(() => harness.Service.PlayAsync(
            new PlayRequest("demo-movie-0001"), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ReplaceConfirmationRequired, error.Error.Code);
        Assert.Same(session, harness.Service.Current);
        var replacement = await harness.Service.PlayAsync(new PlayRequest("demo-movie-0001", 0, true), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
        Assert.Same(replacement, harness.Service.Current);
        await replacement.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClosingDuringOpeningCancelsWorkAndDropsQueuedCallbacks()
    {
        var scheduler = new QueuedScheduler();
        using var harness = new Harness(scheduler);
        var session = await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        var notifications = 0;
        session.SnapshotChanged += (_, _) => notifications++;
        var close = session.CloseAsync(TestContext.Current.CancellationToken);
        scheduler.Drain();
        await close;
        Assert.Equal(1, notifications);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        scheduler.Drain();
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
        Assert.Equal(1, notifications);
        Assert.Null(harness.Service.Current);
    }

    [Fact]
    public async Task ConfiguredOpenFailureReachesSafeFailedState()
    {
        using var harness = new Harness(options: new FakeOptions { Delay = TimeSpan.FromMilliseconds(100), FailureRate = 1 });
        var session = await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        await harness.AdvanceUntilAsync(session, PlayerPhase.Failed);
        Assert.Equal(AppErrorKind.Network, session.Snapshot.Error?.Kind);
        Assert.Equal(ErrorCodes.NetworkUnavailable, session.Snapshot.Error?.Code);
        Assert.DoesNotContain("http", session.Snapshot.Error!.Message, StringComparison.OrdinalIgnoreCase);
        await session.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SkippedEntryNotificationUsesUiSchedulerAndMovesPastFailedEpisode()
    {
        using var harness = new Harness();
        PlaybackEntrySkippedEventArgs? skipped = null;
        harness.Service.EntrySkipped += (_, arguments) =>
        {
            Assert.True(harness.Scheduler.IsInCallback);
            skipped = arguments;
        };
        var session = (FakePlaybackSession)await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        await harness.OpenAsync(session);
        await session.SimulateNextEntryFailureAsync(TestContext.Current.CancellationToken);
        Assert.Equal("demo-episode-001-01-02", skipped?.Entry.ItemId);
        Assert.Equal("demo.playback.skipped", skipped?.Error.Code);
        await harness.OpenAsync(session);
        Assert.Equal("demo-episode-001-01-03", session.Snapshot.Entry?.ItemId);
        await session.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShutdownCompletesWhenUiQueueHasAlreadyStopped()
    {
        using var harness = new Harness(new StoppedScheduler());
        var session = await harness.Service.PreviewAsync(TestContext.Current.CancellationToken);
        await session.CloseAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
        Assert.Null(harness.Service.Current);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PlayerPhase.Closed, session.Snapshot.Phase);
    }

    private static async Task WaitForPhaseAsync(IPlaybackSession session, PlayerPhase phase)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs arguments)
        {
            if (session.Snapshot.Phase == phase) completion.TrySetResult();
        }
        session.SnapshotChanged += Changed;
        try
        {
            Changed(null, EventArgs.Empty);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { session.SnapshotChanged -= Changed; }
    }

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Clock { get; } = new();
        public UiScheduler Scheduler { get; }
        public WeakReferenceMessenger Messenger { get; } = new();
        public FakeOptions Options { get; }
        public FakePlaybackService Service { get; }

        public Harness(UiScheduler? scheduler = null, FakeOptions? options = null)
        {
            Scheduler = scheduler ?? new UiScheduler();
            Options = options ?? new FakeOptions { Delay = TimeSpan.FromMilliseconds(100), BufferEvery = TimeSpan.Zero };
            Service = new FakePlaybackService(new DemoCatalog(), new FakeOperation(Options, Clock), Options, Clock, Scheduler, Messenger);
        }

        public Task OpenAsync(IPlaybackSession session) => AdvanceUntilAsync(session, PlayerPhase.Playing);

        public async Task AdvanceUntilAsync(IPlaybackSession session, PlayerPhase phase)
        {
            for (var attempt = 0; attempt < 500 && session.Snapshot.Phase != phase; attempt++)
            {
                Clock.Advance(Options.Delay > TimeSpan.Zero ? Options.Delay : TimeSpan.FromMilliseconds(1));
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
            Assert.Equal(phase, session.Snapshot.Phase);
        }

        public void Dispose() => Service.Dispose();
    }

    private class UiScheduler : IUiScheduler
    {
        private readonly AsyncLocal<bool> isInCallback = new();
        public bool IsInCallback => isInCallback.Value;
        public virtual bool TryEnqueue(Action action)
        {
            var previous = isInCallback.Value;
            isInCallback.Value = true;
            try { action(); }
            finally { isInCallback.Value = previous; }
            return true;
        }
    }

    private sealed class QueuedScheduler : UiScheduler
    {
        private readonly ConcurrentQueue<Action> callbacks = new();
        public override bool TryEnqueue(Action action) { callbacks.Enqueue(action); return true; }
        public void Drain()
        {
            while (callbacks.TryDequeue(out var callback)) base.TryEnqueue(callback);
        }
    }

    private sealed class StoppedScheduler : UiScheduler
    {
        public override bool TryEnqueue(Action action) => false;
    }
}
