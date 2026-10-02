using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Playback;
using Mambo.Core.Session;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class ReviewedContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task MissingItemReturnsFailedRetryableSessionRatherThanThrowingAtEntryPoint()
    {
        using var harness = new Harness();
        var session = await harness.Player.PlayAsync(new PlayRequest(Guid.NewGuid().ToString("N")), Token);
        await harness.UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);
        Assert.Equal(ErrorCodes.ItemNotFound, session.Snapshot.Error?.Code);
        Assert.Same(session, harness.Player.Current);
        await session.RetryAsync(Token);
        await harness.UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Failed);
        Assert.Equal(ErrorCodes.ItemNotFound, session.Snapshot.Error?.Code);
        var closed = session.CloseAsync(Token);
        await harness.UntilAsync(() => closed.IsCompleted); await closed;
    }
    [Fact]
    public async Task SameResolvedSeriesIgnoresNewStartTicksAndKeepsSession()
    {
        using var harness = new Harness();
        var session = await harness.Player.PlayAsync(new PlayRequest("demo-series-001"), Token);
        await harness.UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Playing);
        var position = session.Snapshot.PositionTicks;
        var same = await harness.Player.PlayAsync(new PlayRequest("demo-series-001", TimeSpan.FromMinutes(9).Ticks), Token);
        Assert.Same(session, same);
        Assert.Equal(position, same.Snapshot.PositionTicks);
    }
    [Fact]
    public async Task PreviewRequiresConfirmationAndReplacementEventsHaveDefinedOrder()
    {
        using var harness = new Harness();
        var original = await harness.Player.PlayAsync(new PlayRequest(new DemoCatalog().AllItems.First(item => item.Kind == MediaKind.Movie).Id), Token);
        await harness.UntilAsync(() => original.Snapshot.Phase == PlayerPhase.Playing);
        var rejection = await Assert.ThrowsAsync<AppException>(() => harness.Player.PreviewAsync(Token));
        Assert.Equal(ErrorCodes.ReplaceConfirmationRequired, rejection.Error.Code);
        Assert.Same(original, harness.Player.Current);
        var events = new List<string>();
        harness.Player.SessionEnded += (_, args) =>
        {
            Assert.True(harness.Scheduler.InCallback);
            Assert.Equal(PlaybackEndReason.Replaced, args.EndReason);
            Assert.True(harness.Player.IsStarting);
            Assert.Null(harness.Player.Current);
            events.Add("ended");
        };
        harness.Player.SessionStarted += (_, _) => events.Add("started");
        var replacement = harness.Player.PreviewAsync(true, Token);
        Assert.Empty(events);
        await harness.UntilAsync(() => replacement.IsCompleted);
        await replacement; harness.Scheduler.Drain();
        Assert.Collection(events, value => Assert.Equal("ended", value), value => Assert.Equal("started", value));
    }
    [Fact]
    public async Task UnconfirmedCloseDoesNotPublishStoppedAndNotificationsNeverRunInline()
    {
        using var harness = new Harness(new FakeOptions { Delay = TimeSpan.FromHours(1) });
        var stopped = 0;
        harness.Messenger.Register<PlaybackStopped>(this, (_, _) => stopped++);
        var changed = 0;
        harness.Player.Changed += (_, _) => changed++;
        var session = await harness.Player.PreviewAsync(Token);
        Assert.Equal(0, changed);
        var closed = session.CloseAsync(PlaybackEndReason.AppShutdown, Token);
        Assert.False(closed.IsCompleted);
        await harness.UntilAsync(() => closed.IsCompleted); await closed;
        Assert.Equal(0, stopped);
        Assert.True(changed > 0);
    }
    [Fact]
    public async Task LogoutClosesConfirmedPlaybackBeforeClearingDemoSession()
    {
        using var harness = new Harness();
        using var identity = new FakeSessionService(harness.Operation, harness.Scheduler, harness.Messenger, harness.Player);
        var session = await harness.Player.PreviewAsync(Token);
        await harness.UntilAsync(() => session.Snapshot.Phase == PlayerPhase.Playing);
        PlaybackEndReason? reason = null;
        harness.Player.SessionEnded += (_, args) => { reason = args.EndReason; Assert.NotNull(identity.Current); };
        var logout = identity.LogoutAsync(Token);
        await harness.UntilAsync(() => logout.IsCompleted);
        Assert.False((await logout).RemoteLogoutFailed);
        Assert.Equal(PlaybackEndReason.Logout, reason);
        Assert.Null(identity.Current); Assert.Null(harness.Player.Current);
    }
    [Fact]
    public async Task RealServiceRejectsAuthenticationButReportsPendingEngineThroughSession()
    {
        using var accounts = new AccountContext();
        var queue = new QueueScheduler();
        using var service = new DeferredPlaybackService(accounts, queue, new WeakReferenceMessenger(), TimeProvider.System);
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(ErrorCodes.NotLoggedIn, (await Assert.ThrowsAsync<AppException>(() => service.PlayAsync(new PlayRequest(id), Token))).Error.Code);
        accounts.Set(new AccountSession(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "测试用户", Guid.NewGuid().ToString("N"))));
        var session = await service.PlayAsync(new PlayRequest(id), Token);
        Assert.Equal(PlayerPhase.Preparing, session.Snapshot.Phase);
        queue.Drain();
        Assert.Equal(PlayerPhase.Failed, session.Snapshot.Phase);
        Assert.Same(session, await service.PlayAsync(new PlayRequest(id, 100), Token));
        Assert.Equal(ErrorCodes.ReplaceConfirmationRequired, (await Assert.ThrowsAsync<AppException>(() => service.PreviewAsync(Token))).Error.Code);
        var closed = session.CloseAsync(Token); queue.Drain(); await closed;
        Assert.Equal(ErrorCodes.SessionClosed, (await Assert.ThrowsAsync<AppException>(() => session.RetryAsync(Token))).Error.Code);
    }
    [Fact]
    public async Task CancelledPlayDoesNotCreateSession()
    {
        using var harness = new Harness();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Player.PreviewAsync(cancelled.Token));
        Assert.Null(harness.Player.Current); Assert.False(harness.Player.IsStarting);
    }
    [Fact]
    public async Task PlaybackRejectsOversizedUtf8IdsBeforeStartingSession()
    {
        using var harness = new Harness();
        var rejected = await Assert.ThrowsAsync<AppException>(() => harness.Player.PlayAsync(new PlayRequest(new string('集', 86)), Token));
        Assert.Equal(ErrorCodes.InvalidArgument, rejected.Error.Code); Assert.Null(harness.Player.Current);
    }
    private sealed class Harness : IDisposable
    {
        public QueueScheduler Scheduler { get; } = new();
        public WeakReferenceMessenger Messenger { get; } = new();
        public FakeOperation Operation { get; }
        public FakePlaybackService Player { get; }
        public Harness(FakeOptions? options = null)
        {
            options ??= new FakeOptions { Delay = TimeSpan.Zero, BufferEvery = TimeSpan.Zero };
            Operation = new(options, TimeProvider.System);
            Player = new(new DemoCatalog(), Operation, options, TimeProvider.System, Scheduler, Messenger);
        }
        public async Task UntilAsync(Func<bool> condition)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (!condition()) { Scheduler.Drain(); if (!condition()) await Task.Delay(1, limit.Token); }
            Scheduler.Drain();
        }
        public void Dispose() { Player.Dispose(); Messenger.Reset(); }
    }
    private sealed class QueueScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> callbacks = new();
        public bool InCallback { get; private set; }
        public bool TryEnqueue(Action action) { callbacks.Enqueue(action); return true; }
        public void Drain()
        { while (callbacks.TryDequeue(out var action)) { InCallback = true; try { action(); } finally { InCallback = false; } } }
    }
}
