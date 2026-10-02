using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Data;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class QueryCacheTimingTests
{
    [Fact]
    public async Task SwrRefreshStartsAtExactStaleBoundaryAndKeepsValueUntilSharedRefreshCompletes()
    {
        using var directory = new TimingDirectory();
        using var account = new AccountSession(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
            Guid.NewGuid().ToString("N"), "synthetic-user", "合成用户", Guid.NewGuid().ToString("N")));
        var clock = new FakeTimeProvider();
        var scheduler = new TimingScheduler();
        using var cache = new QueryCache(scheduler, new QueryPersistence(new AppPaths(directory.Root), clock), clock);
        var staleAfter = TimeSpan.FromSeconds(30);
        var firstResponse = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshedResponse = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<int> Load(CancellationToken token) => Interlocked.Increment(ref calls) switch
        {
            1 => firstResponse.Task.WaitAsync(token),
            2 => refreshedResponse.Task.WaitAsync(token),
            _ => Task.FromResult(9),
        };
        var key = new QueryKey(account.Scope, "detail", "swr-boundary");
        using var first = cache.Observe(key, account, Load, staleAfter: staleAfter, scopeToken: TestContext.Current.CancellationToken);
        var initialWait = first.RefreshAsync(TestContext.Current.CancellationToken);
        firstResponse.SetResult(7);
        await initialWait;
        scheduler.Drain();

        clock.Advance(staleAfter - TimeSpan.FromTicks(1));
        using var fresh = cache.Observe(key, account, Load, staleAfter: staleAfter, scopeToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(7, fresh.Current);
        Assert.False(fresh.IsRefreshing);

        clock.Advance(TimeSpan.FromTicks(1));
        using var stale = cache.Observe(key, account, Load, staleAfter: staleAfter, scopeToken: TestContext.Current.CancellationToken);
        using var coalesced = cache.Observe(key, account, Load, staleAfter: staleAfter, scopeToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.True(stale.IsInitialized);
        Assert.True(stale.IsRefreshing);
        Assert.Equal(7, stale.Current);
        Assert.Equal(7, first.Current);
        Assert.Null(stale.Error);
        var notifications = new List<object?>();
        first.Updated += (sender, _) => notifications.Add(sender);
        fresh.Updated += (sender, _) => notifications.Add(sender);
        stale.Updated += (sender, _) => notifications.Add(sender);
        coalesced.Updated += (sender, _) => notifications.Add(sender);
        var refreshWait = stale.RefreshAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        refreshedResponse.SetResult(8);
        await refreshWait;
        Assert.Equal(2, calls);
        Assert.Equal(8, first.Current);
        Assert.Equal(8, fresh.Current);
        Assert.Equal(8, stale.Current);
        Assert.Equal(8, coalesced.Current);
        Assert.False(stale.IsRefreshing);
        Assert.Empty(notifications);
        scheduler.Drain();
        Assert.Contains(first, notifications);
        Assert.Contains(fresh, notifications);
        Assert.Contains(stale, notifications);
        Assert.Contains(coalesced, notifications);

        // 新鲜期从刷新成功时开始，后台加载耗时不会缩短下一次新鲜期。
        clock.Advance(staleAfter - TimeSpan.FromTicks(1));
        using var stillFresh = cache.Observe(key, account, Load, staleAfter: staleAfter, scopeToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(8, stillFresh.Current);
        clock.Advance(TimeSpan.FromTicks(1));
        using var expiredAgain = cache.Observe(key, account, Load, staleAfter: staleAfter, scopeToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, calls);
        Assert.Equal(9, expiredAgain.Current);
    }

    [Fact]
    public async Task NewObservationStartsReplacementRequestWhileCancelledLoaderIsStillRunning()
    {
        using var directory = new TimingDirectory();
        using var account = Account();
        await using var cache = new QueryCache(new TimingScheduler(), new QueryPersistence(new AppPaths(directory.Root)));
        // 旧响应同步继续，让 SetResult 返回时已经执行完旧请求的 finally。
        var oldResponse = new TaskCompletionSource<int>();
        var newResponse = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<int> Load(CancellationToken _) => Interlocked.Increment(ref calls) == 1 ? oldResponse.Task : newResponse.Task;
        var key = new QueryKey(account.Scope, "detail", "replacement");
        var old = cache.Observe(key, account, Load, scopeToken: TestContext.Current.CancellationToken);
        var oldWait = old.RefreshAsync(TestContext.Current.CancellationToken);
        old.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWait);
        using var current = cache.Observe(key, account, Load, scopeToken: TestContext.Current.CancellationToken);
        var newWait = current.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        oldResponse.SetResult(1);
        Assert.True(current.IsRefreshing);
        Assert.False(current.IsInitialized);
        newResponse.SetResult(2);
        await newWait;
        Assert.Equal(2, current.Current);
        Assert.Null(current.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetRejectsLateOldResultOrErrorAndPersistsOnlyFreshGeneration(bool oldFails)
    {
        using var directory = new TimingDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await using var cache = new QueryCache(new TimingScheduler(), persistence);
        var oldResponse = new TaskCompletionSource<ImmutableArray<MediaLibrary>>();
        var newResponse = new TaskCompletionSource<ImmutableArray<MediaLibrary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<ImmutableArray<MediaLibrary>> Load(CancellationToken _) => Interlocked.Increment(ref calls) == 1 ? oldResponse.Task : newResponse.Task;
        var key = new QueryKey(account.Scope, "libraries");
        using var query = cache.Observe(key, account, Load, scopeToken: TestContext.Current.CancellationToken);
        var oldWait = query.RefreshAsync(TestContext.Current.CancellationToken);
        await cache.ResetAsync(account.Scope, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWait);
        var newWait = query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        if (oldFails) oldResponse.SetException(new AppException(ErrorText.Network("加载旧数据")));
        else oldResponse.SetResult([new("old", "旧媒体库", LibraryKind.Movies)]);
        Assert.False(query.IsInitialized);
        Assert.True(query.IsRefreshing);
        Assert.Null(query.Error);
        newResponse.SetResult([new("fresh", "新媒体库", LibraryKind.Movies)]);
        await newWait;
        await cache.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal("fresh", Assert.Single(query.Current).Id);
        Assert.Equal("fresh", Assert.Single(persistence.Load(account.Scope)[key.LocalKey].Libraries).Id);
    }

    [Fact]
    public async Task PrefetchObservationCannotDowngradeExistingForegroundLoader()
    {
        using var directory = new TimingDirectory();
        using var account = Account();
        await using var cache = new QueryCache(new TimingScheduler(), new QueryPersistence(new AppPaths(directory.Root)));
        var key = new QueryKey(account.Scope, "detail", "priority");
        var foregroundCalls = 0; var backgroundCalls = 0;
        using var foreground = cache.Observe(key, account, _ => { foregroundCalls++; return Task.FromResult(2); }, scopeToken: TestContext.Current.CancellationToken);
        using var prefetch = cache.Observe(key, account, _ => { backgroundCalls++; return Task.FromResult(1); },
            updateFetcher: false, scopeToken: TestContext.Current.CancellationToken);
        await foreground.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, foregroundCalls);
        Assert.Equal(0, backgroundCalls);
        Assert.Equal(2, prefetch.Current);
    }

    [Fact]
    public async Task ClearWaitsForInFlightFlushDeletesItsFileAndRejectsLateResponse()
    {
        using var directory = new TimingDirectory();
        using var account = Account();
        var paths = new AppPaths(directory.Root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var persistence = new QueryPersistence(paths, clock);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Write(string scope, Dictionary<string, QuerySnapshot> values, CancellationToken token)
        {
            writeStarted.TrySetResult();
            await releaseWrite.Task.ConfigureAwait(false);
            await persistence.SaveAsync(scope, values, token).ConfigureAwait(false);
        }
        await using var cache = new QueryCache(new TimingScheduler(), persistence, clock, Write);
        var late = new TaskCompletionSource<ImmutableArray<MediaLibrary>>();
        var calls = 0;
        Task<ImmutableArray<MediaLibrary>> Load(CancellationToken _) => Interlocked.Increment(ref calls) == 1 ?
            Task.FromResult(ImmutableArray.Create(new MediaLibrary("old", "媒体库", LibraryKind.Movies))) : late.Task;
        using var query = cache.Observe(new(account.Scope, "libraries"), account, Load, scopeToken: TestContext.Current.CancellationToken);
        Assert.True(query.IsInitialized);
        var flush = cache.FlushAsync(TestContext.Current.CancellationToken);
        await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var read = query.RefreshAsync(TestContext.Current.CancellationToken);
        var clearing = cache.ClearAsync(account.Scope, TestContext.Current.CancellationToken);
        Assert.False(clearing.IsCompleted);
        releaseWrite.SetResult();
        await Task.WhenAll(flush, clearing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        late.SetResult([new("late", "迟到媒体库", LibraryKind.Movies)]);
        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.FlushAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(paths.QueryCache, account.Scope + ".json")));
        Assert.ThrowsAny<OperationCanceledException>(() =>
        { using var blocked = cache.Observe(new QueryKey(account.Scope, "libraries"), account, Load, scopeToken: TestContext.Current.CancellationToken); });
        using var nextAccount = new AccountSession(account.Secret with { AccessToken = Guid.NewGuid().ToString("N") });
        using var replacement = cache.Observe(new QueryKey(nextAccount.Scope, "libraries"), nextAccount,
            _ => Task.FromResult(ImmutableArray.Create(new MediaLibrary("next", "新账号媒体库", LibraryKind.Movies))), scopeToken: TestContext.Current.CancellationToken);
        Assert.Equal("next", Assert.Single(replacement.Current).Id);
    }

    [Fact]
    public async Task DisposeAsyncDrainsAlreadyRunningPersistenceBeforeReturning()
    {
        using var directory = new TimingDirectory();
        using var account = Account();
        var paths = new AppPaths(directory.Root);
        var persistence = new QueryPersistence(paths);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Write(string scope, Dictionary<string, QuerySnapshot> values, CancellationToken token)
        {
            writeStarted.SetResult();
            await releaseWrite.Task.ConfigureAwait(false);
            await persistence.SaveAsync(scope, values, token).ConfigureAwait(false);
        }
        var cache = new QueryCache(new TimingScheduler(), persistence, write: Write);
        using var query = cache.Observe(new QueryKey(account.Scope, "libraries"), account,
            _ => Task.FromResult(ImmutableArray.Create(new MediaLibrary("one", "媒体库", LibraryKind.Movies))), scopeToken: TestContext.Current.CancellationToken);
        var flush = cache.FlushAsync(TestContext.Current.CancellationToken);
        await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var disposing = cache.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        releaseWrite.SetResult();
        await Task.WhenAll(flush, disposing);
        Assert.True(File.Exists(Path.Combine(paths.QueryCache, account.Scope + ".json")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.RefreshAsync(TestContext.Current.CancellationToken));
        await cache.DisposeAsync();
    }

    [Fact]
    public async Task BurstsOfSnapshotChangesCancelOldDebouncesAndPersistAllLatestValues()
    {
        using var directory = new TimingDirectory();
        using var account = Account();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var paths = new AppPaths(directory.Root);
        var persistence = new QueryPersistence(paths, clock);
        await using var cache = new QueryCache(new TimingScheduler(), persistence, clock);
        var queries = Enumerable.Range(0, 30).Select(index => cache.Observe(
            new QueryKey(account.Scope, "latest", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), account,
            _ => Task.FromResult(ImmutableArray.Create(new MediaItem("item-" + index, "媒体" + index, MediaKind.Movie))),
            scopeToken: TestContext.Current.CancellationToken)).ToArray();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await cache.FlushAsync(TestContext.Current.CancellationToken);
            await cache.DisposeAsync();
            var snapshots = persistence.Load(account.Scope);
            Assert.Equal(30, snapshots.Count);
            foreach (var index in Enumerable.Range(0, 30))
                Assert.Equal("item-" + index, Assert.Single(snapshots["latest|" + index].Items).Id);
            Assert.Empty(Directory.GetFiles(paths.QueryCache, "*.tmp"));
        }
        finally { foreach (var query in queries) query.Dispose(); }
    }

    private static AccountSession Account() => new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
        Guid.NewGuid().ToString("N"), "synthetic-user", "合成用户", Guid.NewGuid().ToString("N")));

    private sealed class TimingScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> pending = new();
        public bool TryEnqueue(Action callback) { pending.Enqueue(callback); return true; }
        public void Drain() { while (pending.TryDequeue(out var callback)) callback(); }
    }

    private sealed class TimingDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Mambo-QueryTimingTests-" + Guid.NewGuid().ToString("N"));
        public TimingDirectory() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
