using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Mambo.Core.Contracts;
using Mambo.Core.Data;
using Mambo.Core.Diagnostics;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class P1InfrastructureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void AddressValidationAndEndpointPreserveBasePathWithoutExposingInput()
    {
        var host = Guid.NewGuid().ToString("N") + ".invalid";
        var address = ServerAddress.Normalize("  " + host + "/emby/  ");
        Assert.Equal("https", address.Uri.Scheme);
        Assert.Equal("/emby/Users/abc", address.Endpoint("Users/abc").AbsolutePath);
        Assert.DoesNotContain(host, address.ToString(), StringComparison.Ordinal);
        var invalid = Assert.Throws<AppException>(() => ServerAddress.Normalize("https://" + host + "?token=" + Guid.NewGuid()));
        Assert.Equal("服务器地址不能包含查询参数或片段", invalid.Message);
        Assert.Equal(ErrorCodes.InvalidArgument, invalid.Error.Code);
        Assert.Throws<AppException>(() => ServerAddress.Normalize("file://" + host));
        Assert.Throws<AppException>(() => ServerAddress.Normalize("https://user:pw@" + host));
    }
    [Fact]
    public void HeadersSeparateUnauthenticatedLoginAndNeverPutSecretsIntoUri()
    {
        using var account = Account();
        using var login = new HttpRequestMessage(HttpMethod.Post, account.Address.Endpoint("Users/AuthenticateByName"));
        AuthHeader.Apply(login, Guid.NewGuid(), "0.1.0");
        Assert.False(login.Headers.Contains("X-Emby-Token"));
        Assert.DoesNotContain("UserId", login.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, account.Address.Endpoint("Users/abc"));
        AuthHeader.Apply(request, Guid.NewGuid(), "0.1.0", account);
        Assert.Equal(account.Secret.AccessToken, request.Headers.GetValues("X-Emby-Token").Single());
        Assert.DoesNotContain(account.Secret.AccessToken, request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }
    [Fact]
    public async Task ApiDoesNotFollowRedirectAndMapsAuthAndMalformedJsonSafely()
    {
        using var account = Account();
        var calls = 0;
        using var api = new EmbyApi(Guid.NewGuid(), new Stub(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid") } });
        }));
        var redirect = await Assert.ThrowsAsync<AppException>(() => api.ItemsAsync(account, "Users/abc/Views", Token));
        Assert.Equal(302, redirect.Error.Status);
        Assert.Equal(1, calls);
        var expired = 0;
        using var auth = new EmbyApi(Guid.NewGuid(), new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        auth.AuthenticationExpired += actual => { Assert.Same(account, actual); expired++; };
        var failure = await Assert.ThrowsAsync<AppException>(() => auth.ValidateAsync(account, Token));
        Assert.Equal(ErrorCodes.SessionExpired, failure.Error.Code);
        Assert.Equal(1, expired);
        using var malformed = new EmbyApi(Guid.NewGuid(), new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not-json") })));
        Assert.Equal(ErrorCodes.InvalidResponse, (await Assert.ThrowsAsync<AppException>(() => malformed.ItemsAsync(account, "Items", Token))).Error.Code);
    }
    [Fact]
    public void FiltersAllowStringsObjectsMixedCaseAndStringNumbers()
    {
        var filters = JsonSerializer.Deserialize("{\"genres\":[\"剧情\",{\"name\":\"科幻\"}],\"years\":[\"2026\"],\"officialratings\":[{\"Name\":\"PG\"}]}", EmbyJsonContext.Default.EmbyFilters)!;
        Assert.Collection(filters.Genres!, value => Assert.Equal("剧情", value.Name), value => Assert.Equal("科幻", value.Name));
        Assert.Equal(2026, Assert.Single(filters.Years!));
        var profile = new DeviceProfile();
        Assert.All(profile.SubtitleProfiles.GroupBy(value => value.Format), group => Assert.Contains(group, value => value.Method == "Embed"));
    }
    [Fact]
    public async Task AtomicSettingsRetainDeviceIdentityAndRecoverBackup()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        using var settings = new SettingsStore(paths, new QueueScheduler());
        var identity = settings.Current.DeviceId;
        await settings.UpdateAsync(value => value with { Volume = 60 }, Token);
        await settings.UpdateAsync(value => value with { Volume = 70 }, Token);
        Assert.True(File.Exists(paths.Settings + ".bak"));
        await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { DeviceId = Guid.NewGuid() }, Token));
        await File.WriteAllTextAsync(paths.Settings, "{broken", Token);
        using var restored = new SettingsStore(paths, new QueueScheduler());
        Assert.Equal(identity, restored.Current.DeviceId);
        Assert.Equal(60, restored.Current.Volume);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
    [Fact]
    public void RedactorRemovesQuotedTokensSignedUrlsHeadersAndPlayPaths()
    {
        var secret = Guid.NewGuid().ToString("N");
        var text = "X-Emby-Token: " + secret + "\n?api_key=" + secret + "&x-amz-credential=" + secret + "&sig=" + secret + " /play/" + secret + " password: \"" + secret + "\"";
        var result = UrlRedactor.Redact(text);
        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("<redacted>", result, StringComparison.Ordinal);
    }
    [Fact]
    public async Task CacheSharesRefreshNotifiesBothObserversAndRetainsValueOnFailure()
    {
        using var directory = new TemporaryDirectory();
        using var account = Account();
        var scheduler = new QueueScheduler();
        using var cache = new QueryCache(scheduler, new QueryPersistence(new AppPaths(directory.Path)));
        var loading = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<int> Load(CancellationToken _) { calls++; return calls == 1 ? loading.Task : Task.FromException<int>(new AppException(ErrorText.Network("加载"))); }
        var key = new QueryKey(account.Scope, "detail", "one");
        using var first = cache.Observe(key, account, Load, scopeToken: Token);
        using var second = cache.Observe(key, account, Load, scopeToken: Token);
        var firstUpdates = 0; var secondUpdates = 0;
        first.Updated += (_, _) => firstUpdates++;
        second.Updated += (_, _) => secondUpdates++;
        var wait = second.RefreshAsync(Token);
        Assert.Equal(1, calls);
        loading.SetResult(42);
        await wait;
        scheduler.Drain();
        Assert.Equal(42, first.Current); Assert.Equal(42, second.Current);
        Assert.True(firstUpdates > 0 && secondUpdates > 0);
        await first.RefreshAsync(Token);
        scheduler.Drain();
        Assert.Equal(42, first.Current); Assert.Equal(42, second.Current);
        Assert.Equal(ErrorCodes.NetworkUnavailable, second.Error?.Code);
    }
    [Fact]
    public async Task CallerCancellationDoesNotCancelAnotherObserverButScopeDoes()
    {
        using var directory = new TemporaryDirectory();
        using var account = Account();
        using var scope = new CancellationTokenSource();
        using var cache = new QueryCache(new QueueScheduler(), new QueryPersistence(new AppPaths(directory.Path)));
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken backend = default;
        using var query = cache.Observe(new QueryKey(account.Scope, "detail"), account, token => { backend = token; return pending.Task.WaitAsync(token); }, scopeToken: scope.Token);
        using var caller = new CancellationTokenSource();
        var wait = query.RefreshAsync(caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(backend.IsCancellationRequested);
        scope.Cancel();
        Assert.True(backend.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.RefreshAsync(Token));
    }
    [Fact]
    public async Task CacheSnapshotHydratesImmediatelyAndSameAccountReloginUsesNewToken()
    {
        using var directory = new TemporaryDirectory();
        var scheduler = new QueueScheduler();
        using var account = Account();
        var paths = new AppPaths(directory.Path);
        var key = new QueryKey(account.Scope, "libraries");
        using (var cache = new QueryCache(scheduler, new QueryPersistence(paths)))
        {
            using var query = cache.Observe(key, account, _ => Task.FromResult(ImmutableArray.Create(new MediaLibrary("one", "电影", LibraryKind.Movies))), scopeToken: Token);
            await query.RefreshAsync(Token);
            await cache.FlushAsync(Token);
        }
        using var cold = new QueryCache(scheduler, new QueryPersistence(paths));
        using var restored = cold.Observe(key, account, _ => Task.FromException<ImmutableArray<MediaLibrary>>(new AppException(ErrorText.Network("加载"))), scopeToken: Token);
        Assert.True(restored.IsInitialized); Assert.Equal("one", Assert.Single(restored.Current).Id);
        account.Dispose();
        using var next = new AccountSession(account.Secret);
        using var switched = cold.Observe(key, next, _ => Task.FromResult(ImmutableArray.Create(new MediaLibrary("two", "剧集", LibraryKind.TvShows))), scopeToken: Token);
        await switched.RefreshAsync(Token);
        Assert.Equal("two", Assert.Single(switched.Current).Id);
        Assert.True(account.Token.IsCancellationRequested);
    }
    [Fact]
    public async Task SchedulerReservesMetadataForegroundSlotAndReliabilityLane()
    {
        using var scheduler = new RequestScheduler();
        var blocked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        Task<int> Block(CancellationToken token) { Interlocked.Increment(ref started); return blocked.Task.WaitAsync(token); }
        var backgrounds = Enumerable.Range(0, 4).Select(_ => scheduler.RunAsync(Block, RequestPriority.Background, scopeToken: Token)).ToArray();
        Assert.Equal(3, started);
        var foreground = scheduler.RunAsync(Block, scopeToken: Token);
        Assert.Equal(4, started);
        var reliable = Enumerable.Range(0, 3).Select(_ => scheduler.RunAsync(Block, lane: RequestLane.Reliability, scopeToken: Token)).ToArray();
        Assert.Equal(6, started);
        blocked.SetResult(1);
        await Task.WhenAll([.. backgrounds, foreground, .. reliable]);
        Assert.Equal(8, started);
    }
    [Fact]
    public async Task SchedulerDeadlineIncludesQueueAndOnlyRetriesIdempotentOperations()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = new RequestScheduler(clock);
        var started = 0;
        Task<int> Block(CancellationToken token) { started++; return Task.Delay(Timeout.Infinite, token).ContinueWith<int>(_ => throw new OperationCanceledException(token), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
        var background = Enumerable.Range(0, 4).Select(_ => scheduler.RunAsync(Block, RequestPriority.Background, scopeToken: Token)).ToArray();
        clock.Advance(TimeSpan.FromSeconds(20));
        foreach (var task in background) Assert.Equal(ErrorCodes.NetworkUnavailable, (await Assert.ThrowsAsync<AppException>(() => task)).Error.Code);
        Assert.Equal(3, started);
        var attempts = 0;
        Task<int> Retry(CancellationToken _) { attempts++; return attempts % 2 == 1 ? Task.FromException<int>(new AppException(ErrorText.Network("加载"))) : Task.FromResult(42); }
        Assert.Equal(42, await scheduler.RunAsync(Retry, scopeToken: Token));
        Assert.Equal(2, attempts);
        await Assert.ThrowsAsync<AppException>(() => scheduler.RunAsync(Retry, idempotent: false, scopeToken: Token));
        Assert.Equal(3, attempts);
    }
    private static AccountSession Account() => new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "测试用户", Guid.NewGuid().ToString("N")));
    [Fact]
    public async Task ForegroundObservationReplacesPrefetchLoaderForFutureRefreshes()
    {
        using var directory = new TemporaryDirectory();
        using var account = Account();
        using var cache = new QueryCache(new QueueScheduler(), new QueryPersistence(new AppPaths(directory.Path)));
        var key = new QueryKey(account.Scope, "detail", "priority");
        var backgroundCalls = 0; var foregroundCalls = 0;
        using var prefetch = cache.Observe(key, account, _ => { backgroundCalls++; return Task.FromResult(1); }, scopeToken: Token);
        Assert.Equal(1, prefetch.Current);
        using var foreground = cache.Observe(key, account, _ => { foregroundCalls++; return Task.FromResult(2); }, scopeToken: Token);
        await foreground.RefreshAsync(Token);
        Assert.Equal(1, backgroundCalls); Assert.Equal(1, foregroundCalls);
        Assert.Equal(2, prefetch.Current);
    }
    [Theory]
    [InlineData("{\"Version\":1,\"Values\":null}")]
    [InlineData("{\"Version\":1,\"Values\":{\"x\":null}}")]
    [InlineData("{\"Version\":1,\"Values\":{\"x\":{\"Kind\":\"items\",\"Items\":[null]}}}")]
    [InlineData("{\"Version\":1,\"Values\":{\"x\":{\"Kind\":\"libraries\",\"Libraries\":null}}}")]
    public async Task MalformedQuerySnapshotsCannotHydrateInvalidDomainArrays(string json)
    {
        using var directory = new TemporaryDirectory();
        using var account = Account();
        var paths = new AppPaths(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(paths.QueryCache, account.Scope + ".json"), json, Token);
        Assert.Empty(new QueryPersistence(paths).Load(account.Scope));
    }
    private sealed class QueueScheduler : IUiScheduler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> callbacks = new();
        public bool TryEnqueue(Action callback) { callbacks.Enqueue(callback); return true; }
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "p1-tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() { Directory.CreateDirectory(Path); }
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
