using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Networking;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class SessionManagerTests
{
    [Fact]
    public async Task LoginNormalizesAddressPersistsSecretAndPublishesOnlyThroughUiScheduler()
    {
        var secret = Secret();
        var password = Guid.NewGuid().ToString("N");
        var requests = 0;
        using var handler = new SessionHttpHandler(async (request, token) =>
        {
            requests++;
            Assert.EndsWith("/emby/Users/AuthenticateByName", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(password, body.RootElement.GetProperty("Pw").GetString());
            return Authentication(secret);
        });
        using var api = Api(handler);
        var store = new MemorySecretStore();
        using var accounts = new AccountContext();
        var scheduler = new SessionScheduler();
        var messenger = new WeakReferenceMessenger();
        var recipient = new SessionMessages();
        messenger.Register<SessionChanged>(recipient, static (target, value) => ((SessionMessages)target).Changes.Add(value));
        using var manager = new SessionManager(api, store, accounts, scheduler, messenger);
        var changed = 0;
        manager.Changed += (_, _) => changed++;

        Assert.Equal(SessionState.LoggedOut, manager.State);
        Assert.Equal(0, requests);
        await manager.LoginAsync(new LoginRequest("  " + secret.ServerAddress + "///  ", "  读者  ", password), TestContext.Current.CancellationToken);

        Assert.Equal(1, requests);
        Assert.Equal(secret.ServerAddress, store.Value?.ServerAddress);
        Assert.Equal(secret.AccessToken, store.Value?.AccessToken);
        Assert.Equal(secret.UserId, accounts.Current?.Secret.UserId);
        Assert.Equal(SessionState.LoggedIn, manager.State);
        Assert.DoesNotContain(password, store.Value!.ToString());
        Assert.DoesNotContain(secret.AccessToken, manager.Current!.ToString());
        Assert.Equal(0, changed);
        await DrainUntilAsync(scheduler, () => recipient.Changes.Count == 1);
        Assert.Equal(1, changed);
        Assert.Equal(manager.Current, Assert.Single(recipient.Changes).Session);

        var duplicate = await Assert.ThrowsAsync<AppException>(() => manager.LoginAsync(new LoginRequest(secret.ServerAddress, "读者", password), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.AlreadyLoggedIn, duplicate.Error.Code);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DisposeAfterLateSecretSaveRollsBackTheUnfinishedLogin()
    {
        var secret = Secret();
        using var handler = new SessionHttpHandler((_, _) => Task.FromResult(Authentication(secret)));
        using var api = Api(handler);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemorySecretStore
        {
            Writing = async () => { saved.SetResult(); await finishSave.Task; },
        };
        using var accounts = new AccountContext();
        var scheduler = new SessionScheduler();
        var manager = new SessionManager(api, store, accounts, scheduler, new WeakReferenceMessenger());
        var notifications = 0;
        manager.Changed += (_, _) => notifications++;
        var login = manager.LoginAsync(new LoginRequest(secret.ServerAddress, "读者", Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);
        await saved.Task.WaitAsync(TestContext.Current.CancellationToken);
        manager.Dispose();
        finishSave.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Null(store.Value);
        Assert.Null(accounts.Current);
        Assert.Null(manager.Current);
        scheduler.Drain();
        Assert.Equal(0, notifications);
        manager.Dispose();
    }

    [Fact]
    public async Task RestoreWithoutCredentialsDoesNotContactTheServer()
    {
        using var handler = new SessionHttpHandler((_, _) => throw new InvalidOperationException("不应发出请求。"));
        using var api = Api(handler);
        using var accounts = new AccountContext();
        using var manager = new SessionManager(api, new MemorySecretStore(), accounts, new SessionScheduler(), new WeakReferenceMessenger());
        await manager.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.LoggedOut, manager.State);
        Assert.Null(manager.Current);
        Assert.Null(manager.Error);
    }

    [Fact]
    public async Task RestoreMakesAccountAvailableBeforeNetworkValidationCompletes()
    {
        var secret = Secret();
        var responding = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SessionHttpHandler((request, _) =>
        {
            Assert.Equal(secret.AccessToken, Assert.Single(request.Headers.GetValues("X-Emby-Token")));
            validating.SetResult();
            return responding.Task;
        });
        using var api = Api(handler);
        using var accounts = new AccountContext();
        var store = new MemorySecretStore { Value = secret };
        using var manager = new SessionManager(api, store, accounts, new SessionScheduler(), new WeakReferenceMessenger());
        var restore = manager.RestoreAsync(TestContext.Current.CancellationToken);
        await validating.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.Restoring, manager.State);
        Assert.Equal(secret.UserId, manager.Current?.UserId);
        Assert.Equal(secret.UserId, accounts.Current?.Secret.UserId);
        responding.SetResult(new(HttpStatusCode.OK));
        await restore;
        Assert.Equal(SessionState.LoggedIn, manager.State);
        Assert.Equal(0, store.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRetriesNetworkAndServerFailuresWithOneTwoFourSecondBackoff(bool serverFailure)
    {
        var clock = new FakeTimeProvider();
        var timerClock = new RetryClock(clock);
        var times = new List<DateTimeOffset>();
        using var handler = new SessionHttpHandler((_, _) =>
        {
            times.Add(clock.GetUtcNow());
            return serverFailure ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) :
                Task.FromException<HttpResponseMessage>(new HttpRequestException(Guid.NewGuid().ToString("N")));
        });
        using var api = Api(handler);
        var secret = Secret();
        var store = new MemorySecretStore { Value = secret };
        using var accounts = new AccountContext();
        using var manager = new SessionManager(api, store, accounts, new SessionScheduler(), new WeakReferenceMessenger(), clock: timerClock);
        var restore = manager.RestoreAsync(TestContext.Current.CancellationToken);
        for (var retry = 0; retry < 3; retry++)
        {
            await WaitUntilAsync(() => timerClock.TimersCreated > retry);
            clock.Advance(TimeSpan.FromSeconds(1 << retry));
        }
        var failure = await Assert.ThrowsAsync<AppException>(() => restore);
        Assert.Equal(4, times.Count);
        Assert.Equal([0d, 1d, 3d, 7d], times.Select(value => (value - times[0]).TotalSeconds));
        Assert.Equal(SessionState.Unreachable, manager.State);
        Assert.Equal(secret.UserId, manager.Current?.UserId);
        Assert.Equal(secret, store.Value);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(failure.Error, manager.Error);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task CancellingRestoreRetainsCredentialWithoutCancellationError()
    {
        var clock = new FakeTimeProvider();
        var timerClock = new RetryClock(clock);
        using var handler = new SessionHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var api = Api(handler);
        var secret = Secret();
        var store = new MemorySecretStore { Value = secret };
        using var accounts = new AccountContext();
        using var manager = new SessionManager(api, store, accounts, new SessionScheduler(), new WeakReferenceMessenger(), clock: timerClock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var restore = manager.RestoreAsync(cancellation.Token);
        await WaitUntilAsync(() => timerClock.TimersCreated == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.Equal(secret, store.Value);
        Assert.Equal(secret.UserId, manager.Current?.UserId);
        Assert.Equal(SessionState.Unreachable, manager.State);
        Assert.Null(manager.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UnauthorizedRestoreDeletesCredentialAndBroadcastsExpirationOnce(HttpStatusCode status)
    {
        using var handler = new SessionHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var api = Api(handler);
        var store = new MemorySecretStore { Value = Secret() };
        using var accounts = new AccountContext();
        var scheduler = new SessionScheduler();
        var messenger = new WeakReferenceMessenger();
        var recipient = new SessionMessages();
        messenger.Register<SessionExpired>(recipient, static (target, _) => ((SessionMessages)target).Expirations++);
        using var manager = new SessionManager(api, store, accounts, scheduler, messenger);
        await Assert.ThrowsAsync<AppException>(() => manager.RestoreAsync(TestContext.Current.CancellationToken));
        await DrainUntilAsync(scheduler, () => recipient.Expirations == 1);
        Assert.Null(store.Value);
        Assert.Equal(1, store.Deletes);
        Assert.Null(accounts.Current);
        Assert.Null(manager.Current);
        Assert.Equal(SessionState.LoggedOut, manager.State);
    }

    [Fact]
    public async Task AuthFailureOutsideRestoreClearsOnlyTheActiveAccount()
    {
        var calls = 0;
        using var handler = new SessionHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(++calls == 1 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)));
        using var api = Api(handler);
        var store = new MemorySecretStore { Value = Secret() };
        using var accounts = new AccountContext();
        var scheduler = new SessionScheduler();
        var messenger = new WeakReferenceMessenger();
        var recipient = new SessionMessages();
        messenger.Register<SessionExpired>(recipient, static (target, _) => ((SessionMessages)target).Expirations++);
        using var manager = new SessionManager(api, store, accounts, scheduler, messenger);
        await manager.RestoreAsync(TestContext.Current.CancellationToken);
        var active = accounts.Current!;
        await Assert.ThrowsAsync<AppException>(() => api.ItemsAsync(active, "Items", TestContext.Current.CancellationToken));
        await DrainUntilAsync(scheduler, () => recipient.Expirations == 1);
        Assert.Null(accounts.Current);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task ExpirationFromAnOldAccountCannotInvalidateAReplacementLogin()
    {
        var first = Secret();
        var second = Secret();
        using var handler = new SessionHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("AuthenticateByName", StringComparison.Ordinal) ? Authentication(second) :
                new HttpResponseMessage(HttpStatusCode.OK)));
        using var api = Api(handler);
        var store = new MemorySecretStore { Value = first };
        using var accounts = new AccountContext();
        var scheduler = new SessionScheduler();
        var messenger = new WeakReferenceMessenger();
        var recipient = new SessionMessages();
        messenger.Register<SessionExpired>(recipient, static (target, _) => ((SessionMessages)target).Expirations++);
        using var manager = new SessionManager(api, store, accounts, scheduler, messenger);
        await manager.RestoreAsync(TestContext.Current.CancellationToken);
        var old = accounts.Current!;
        await manager.LogoutAsync(TestContext.Current.CancellationToken);
        await manager.LoginAsync(new LoginRequest(second.ServerAddress, "读者", Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);

        manager.NotifyAuthenticationExpired(old);
        Assert.Equal(second.UserId, accounts.Current?.Secret.UserId);
        Assert.Equal(second.UserId, manager.Current?.UserId);
        Assert.Equal(second.AccessToken, store.Value?.AccessToken);
        manager.NotifyAuthenticationExpired(accounts.Current!);
        await DrainUntilAsync(scheduler, () => recipient.Expirations == 1);
        Assert.Null(accounts.Current);
        Assert.Null(store.Value);
        Assert.Equal(2, store.Deletes);
    }

    [Fact]
    public async Task LogoutStopsPlaybackFlushesOldIdentityThenClearsAndAttemptsRemoteLogout()
    {
        var secret = Secret();
        var order = new List<string>();
        using var accounts = new AccountContext();
        accounts.Set(new AccountSession(secret));
        var account = accounts.Current!;
        var store = new MemorySecretStore { Value = secret, Deleting = () => order.Add("delete") };
        var scheduler = new SessionScheduler();
        var messenger = new WeakReferenceMessenger();
        var options = new FakeOptions { Delay = TimeSpan.Zero };
        var clock = new FakeTimeProvider();
        using var playback = new FakePlaybackService(new DemoCatalog(), new FakeOperation(options, clock), options, clock, scheduler, messenger);
        await playback.PreviewAsync(TestContext.Current.CancellationToken);
        var playing = playback.Current!;
        using var handler = new SessionHttpHandler((request, _) =>
        {
            order.Add("remote");
            Assert.Null(accounts.Current);
            Assert.Null(store.Value);
            Assert.Equal(secret.AccessToken, Assert.Single(request.Headers.GetValues("X-Emby-Token")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var api = Api(handler);
        using var manager = new SessionManager(api, store, accounts, scheduler, messenger, playback, clock,
            flush: (old, _) =>
            {
                order.Add("flush");
                Assert.Same(account, old);
                Assert.Same(account, accounts.Current);
                Assert.Null(playback.Current);
                Assert.Equal(PlayerPhase.Closed, playing.Snapshot.Phase);
                return Task.CompletedTask;
            },
            clear: scope => { order.Add("clear"); Assert.Equal(account.Scope, scope); return Task.CompletedTask; });
        var logout = manager.LogoutAsync(TestContext.Current.CancellationToken);
        await DrainUntilAsync(scheduler, () => logout.IsCompleted);
        var result = await logout;
        Assert.True(result.RemoteLogoutFailed);
        Assert.Equal(["flush", "delete", "clear", "remote"], order);
        Assert.Equal(SessionState.LoggedOut, manager.State);
        Assert.True(account.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LogoutTreatsSuccessfulOrAlreadyInvalidRemoteSessionAsSuccess(HttpStatusCode status)
    {
        using var accounts = new AccountContext();
        var secret = Secret();
        accounts.Set(new AccountSession(secret));
        using var handler = new SessionHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var api = Api(handler);
        using var manager = new SessionManager(api, new MemorySecretStore { Value = secret }, accounts, new SessionScheduler(), new WeakReferenceMessenger());
        Assert.False((await manager.LogoutAsync(TestContext.Current.CancellationToken)).RemoteLogoutFailed);
    }

    private static SessionSecret Secret()
    {
        var address = new UriBuilder(Uri.UriSchemeHttps, Guid.NewGuid().ToString("N") + ".invalid") { Path = "emby" }.Uri.AbsoluteUri.TrimEnd('/');
        return new(address, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "读者", Guid.NewGuid().ToString("N"));
    }

    private static EmbyApi Api(HttpMessageHandler handler) => new(Guid.NewGuid(), handler);

    private static HttpResponseMessage Authentication(SessionSecret value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new EmbyAuthentication
        {
            AccessToken = value.AccessToken, ServerId = value.ServerId, User = new EmbyItem { Id = value.UserId, Name = value.UserName },
        }, EmbyJsonContext.Default.EmbyAuthentication), Encoding.UTF8, "application/json"),
    };

    private static async Task DrainUntilAsync(SessionScheduler scheduler, Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) { scheduler.Drain(); if (!condition()) await Task.Delay(1, timeout.Token); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class SessionHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public SessionSecret? Value { get; set; }
        public int Deletes { get; private set; }
        public Func<Task>? Writing { get; init; }
        public Action? Deleting { get; init; }
        public Task<SessionSecret?> ReadAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Value); }
        public async Task WriteAsync(SessionSecret secret, CancellationToken cancellationToken = default)
        { if (Writing is not null) await Writing(); else cancellationToken.ThrowIfCancellationRequested(); Value = secret; }
        public Task DeleteAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Deletes++; Deleting?.Invoke(); Value = null; return Task.CompletedTask; }
    }

    private sealed class SessionMessages
    {
        public List<SessionChanged> Changes { get; } = [];
        public int Expirations { get; set; }
    }

    private sealed class SessionScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> callbacks = new();
        public bool TryEnqueue(Action callback) { callbacks.Enqueue(callback); return true; }
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }

    private sealed class RetryClock(TimeProvider clock) : TimeProvider
    {
        private int timersCreated;
        public int TimersCreated => Volatile.Read(ref timersCreated);
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetTimestamp();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref timersCreated);
            return timer;
        }
    }
}
