using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class FakeServiceTests
{
    [Fact]
    public async Task LoginKeepsOnlyPublicIdentityAndNotifiesOnQueuedUiThread()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new ServiceScheduler();
        var messenger = new WeakReferenceMessenger();
        using var service = new FakeSessionService(Operation(clock), scheduler, messenger);
        await service.LogoutAsync(TestContext.Current.CancellationToken);
        scheduler.Drain();
        var changes = 0;
        var callbackThread = -1;
        var recipient = new SessionRecipient();
        messenger.Register<SessionChanged>(recipient, static (target, message) => ((SessionRecipient)target).Messages.Add(message));
        service.Changed += (_, _) => { changes++; callbackThread = Environment.CurrentManagedThreadId; };
        var address = Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        var request = new LoginRequest(address, "  演示读者  ", password);

        var login = service.LoginAsync(request, TestContext.Current.CancellationToken);
        Assert.False(login.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        await login;

        Assert.Equal(SessionState.LoggedIn, service.State);
        Assert.Equal("演示读者", service.Current?.UserName);
        Assert.DoesNotContain(address, service.Current!.ToString());
        Assert.DoesNotContain(password, service.Current.ToString());
        Assert.DoesNotContain(address, request.ToString());
        Assert.DoesNotContain(password, request.ToString());
        Assert.Equal(0, changes);
        Assert.Empty(recipient.Messages);
        var drainingThread = Environment.CurrentManagedThreadId;
        scheduler.Drain();
        Assert.Equal(1, changes);
        Assert.Equal(drainingThread, callbackThread);
        Assert.Equal(service.Current, Assert.Single(recipient.Messages).Session);

        var failure = await Assert.ThrowsAsync<AppException>(() => service.LoginAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("demo.already_logged_in", failure.Error.Code);
    }

    [Fact]
    public async Task SessionDisposeCancelsActiveAndQueuedCommandsAndDropsNotifications()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new ServiceScheduler();
        var messenger = new WeakReferenceMessenger();
        var service = new FakeSessionService(Operation(clock), scheduler, messenger);
        var notifications = 0;
        var recipient = new SessionRecipient();
        messenger.Register<SessionChanged>(recipient, static (target, message) => ((SessionRecipient)target).Messages.Add(message));
        service.Changed += (_, _) => notifications++;
        await service.LogoutAsync(TestContext.Current.CancellationToken);
        var login = service.LoginAsync(new LoginRequest(Guid.NewGuid().ToString("N"), "演示读者", Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);
        var restore = service.RestoreAsync(TestContext.Current.CancellationToken);

        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        clock.Advance(TimeSpan.FromHours(1));
        scheduler.Drain();
        Assert.Null(service.Current);
        Assert.Equal(SessionState.LoggedOut, service.State);
        Assert.Null(service.Error);
        Assert.Equal(0, notifications);
        Assert.Empty(recipient.Messages);
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RestoreAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingRestoreRestoresPreviousStateWithoutAnError()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new ServiceScheduler();
        using var service = new FakeSessionService(Operation(clock), scheduler, new WeakReferenceMessenger());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var previous = service.Current;
        var restore = service.RestoreAsync(cancellation.Token);
        Assert.Equal(SessionState.Restoring, service.State);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.Equal(SessionState.LoggedIn, service.State);
        Assert.Equal(previous, service.Current);
        Assert.Null(service.Error);
    }

    [Fact]
    public async Task RestoreFailureKeepsAccountAndThrowsSafeChineseError()
    {
        var clock = new FakeTimeProvider();
        using var service = new FakeSessionService(Operation(clock, 1), new ServiceScheduler(), new WeakReferenceMessenger());
        var previous = service.Current;
        var restore = service.RestoreAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<AppException>(() => restore);
        Assert.Equal(SessionState.Unreachable, service.State);
        Assert.Equal(previous, service.Current);
        Assert.Equal(failure.Error, service.Error);
        Assert.Equal("演示服务暂时不可用，请重试。", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task SettingsDisposeCancelsCommandsAndClearsFormDefaults()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new ServiceScheduler();
        var service = new FakeSettingsService(Operation(clock), scheduler);
        var savedDefaults = service.SaveConnectionDefaultsAsync(new ConnectionDefaults(Guid.NewGuid().ToString("N"), "演示读者"), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        await savedDefaults;
        scheduler.Drain();
        var notifications = 0;
        service.Changed += (_, _) => notifications++;
        var update = service.UpdateAsync(service.Current with { Volume = 20 }, TestContext.Current.CancellationToken);
        var validate = service.ValidateExternalPlayerAsync(Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validate);
        clock.Advance(TimeSpan.FromHours(1));
        scheduler.Drain();
        Assert.Equal(100, service.Current.Volume);
        Assert.Empty(service.ConnectionDefaults.ServerAddress);
        Assert.Empty(service.ConnectionDefaults.UserName);
        Assert.Equal(ExternalPlayerStatus.UsingEmbedded, service.ExternalPlayerStatus);
        Assert.Equal(0, notifications);
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ClearCacheAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SettingsCommandsAreSerializedAndUseTheFakeClock()
    {
        var clock = new FakeTimeProvider();
        var timerClock = new CountingTimeProvider(clock);
        var scheduler = new ServiceScheduler();
        using var service = new FakeSettingsService(Operation(timerClock), scheduler);
        var notifications = 0;
        service.Changed += (_, _) => notifications++;
        var update = service.UpdateAsync(service.Current with { Volume = 20 }, TestContext.Current.CancellationToken);
        var validation = service.ValidateExternalPlayerAsync(Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(ExternalPlayerStatus.UsingEmbedded, service.ExternalPlayerStatus);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(update.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await update;
        await WaitUntilAsync(() => timerClock.TimersCreated >= 2);
        Assert.Equal(ExternalPlayerStatus.Validating, service.ExternalPlayerStatus);
        Assert.Equal(20, service.Current.Volume);
        Assert.False(validation.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        await validation;
        Assert.Equal(ExternalPlayerStatus.Invalid, service.ExternalPlayerStatus);
        Assert.Equal(0, notifications);
        scheduler.Drain();
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task CancellingExternalValidationRestoresPreviousStatus()
    {
        var clock = new FakeTimeProvider();
        using var service = new FakeSettingsService(Operation(clock), new ServiceScheduler());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var validation = service.ValidateExternalPlayerAsync(Guid.NewGuid().ToString("N"), cancellation.Token);
        Assert.Equal(ExternalPlayerStatus.Validating, service.ExternalPlayerStatus);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        Assert.Equal(ExternalPlayerStatus.UsingEmbedded, service.ExternalPlayerStatus);
        Assert.Equal(PlaybackMode.Embedded, service.Current.PlaybackMode);
    }

    [Fact]
    public async Task DemoExternalSettingsNeverEnableOrRetainAnExecutablePath()
    {
        var clock = new FakeTimeProvider();
        using var service = new FakeSettingsService(new FakeOperation(new FakeOptions { Delay = TimeSpan.Zero }, clock), new ServiceScheduler());
        var path = Guid.NewGuid().ToString("N");
        await service.UpdateAsync(service.Current with { PlaybackMode = PlaybackMode.External, ExternalMpvPath = path }, TestContext.Current.CancellationToken);
        await service.ValidateExternalPlayerAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackMode.Embedded, service.Current.PlaybackMode);
        Assert.Null(service.Current.ExternalMpvPath);
        Assert.Equal(ExternalPlayerStatus.Invalid, service.ExternalPlayerStatus);
    }

    [Fact]
    public async Task PreferenceCancellationPreservesValueAndQueuedNotificationUsesLatestValue()
    {
        var scheduler = new ServiceScheduler();
        using var service = new FakeLibraryPreferences(scheduler);
        var events = new List<LibraryPreferenceChangedEventArgs>();
        service.Changed += (_, args) => events.Add(args);
        var query = new LibraryQuery { Sort = LibrarySort.Name, Direction = SortDirection.Ascending, Genres = ["科幻"] };
        await service.SetAsync("demo-library", query, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResetAsync("demo-library", cancellation.Token));
        Assert.Equal(query, service.Get("demo-library"));
        Assert.Empty(events);
        await service.ResetAsync("demo-library", TestContext.Current.CancellationToken);
        scheduler.Drain();
        Assert.Equal(LibrarySort.DateCreated, Assert.Single(events).Query.Sort);
        Assert.Empty(service.Get("demo-library").Genres);
    }

    [Fact]
    public async Task PreferenceDisposeDropsQueuedNotificationsAndRejectsAccess()
    {
        var scheduler = new ServiceScheduler();
        var service = new FakeLibraryPreferences(scheduler);
        var notifications = 0;
        service.Changed += (_, _) => notifications++;
        await service.SetAsync("demo-library", new LibraryQuery(), TestContext.Current.CancellationToken);
        service.Dispose();
        scheduler.Drain();
        Assert.Equal(0, notifications);
        Assert.Throws<ObjectDisposedException>(() => service.Get("demo-library"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SetAsync("demo-library", new LibraryQuery(), TestContext.Current.CancellationToken));
        service.Dispose();
    }

    private static FakeOperation Operation(TimeProvider clock, double failureRate = 0) =>
        new(new FakeOptions { Delay = TimeSpan.FromSeconds(5), FailureRate = failureRate }, clock);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class SessionRecipient
    {
        public List<SessionChanged> Messages { get; } = [];
    }

    private sealed class ServiceScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> callbacks = new();
        public bool TryEnqueue(Action callback) { callbacks.Enqueue(callback); return true; }
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }

    private sealed class CountingTimeProvider(TimeProvider clock) : TimeProvider
    {
        private int timersCreated;
        public int TimersCreated => Volatile.Read(ref timersCreated);
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetTimestamp();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref timersCreated);
            return timer;
        }
    }
}
