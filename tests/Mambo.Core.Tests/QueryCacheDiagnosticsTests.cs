using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Data;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class QueryCacheDiagnosticsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RestoredLibraryHeroAndLibraryPageDoNotProveVisibleHomeCardsWereRestored()
    {
        using var directory = new DiagnosticDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await persistence.SaveAsync(account.Scope, new(StringComparer.Ordinal)
        {
            ["libraries|"] = new() { Kind = "libraries", UpdatedAt = DateTimeOffset.UtcNow,
                Libraries = [new("synthetic-library", "合成媒体库", LibraryKind.Movies)] },
            ["hero|"] = Items(new MediaItem("synthetic-hero", "合成海报", MediaKind.Movie)),
            ["library-first|synthetic-library"] = new() { Kind = "page", UpdatedAt = DateTimeOffset.UtcNow,
                Items = [new("synthetic-library-item", "合成媒体", MediaKind.Movie)] },
            ["continue|"] = Items(),
            ["latest|synthetic-library"] = Items(),
        }, Token);
        await using var cache = new QueryCache(new DiagnosticScheduler(), persistence);
        using var libraries = cache.Observe(new(account.Scope, "libraries"), account,
            NoNetwork<ImmutableArray<MediaLibrary>>, staleAfter: TimeSpan.FromDays(1), scopeToken: Token);
        using var hero = ObserveItems(cache, account, "hero", NoNetwork<ImmutableArray<MediaItem>>);
        using var page = cache.Observe(new(account.Scope, "library-first", "synthetic-library"), account,
            NoNetwork<QueryPage<MediaItem>>, staleAfter: TimeSpan.FromDays(1), scopeToken: Token);
        using var recent = ObserveItems(cache, account, "continue", NoNetwork<ImmutableArray<MediaItem>>);
        using var latest = ObserveItems(cache, account, "latest", NoNetwork<ImmutableArray<MediaItem>>, "synthetic-library");

        Assert.True(libraries.IsInitialized);
        Assert.Equal(5, cache.RestoredSnapshotCount);
        Assert.Empty(recent.Current);
        Assert.Empty(latest.Current);
        Assert.Equal(0, cache.CountRestoredHomeItems([Assert.Single(hero.Current), Assert.Single(page.Current!.Items)]));
    }

    [Fact]
    public async Task CountsOnlyVisibleExactReferencesAndDeduplicatesRepeatedCards()
    {
        using var directory = new DiagnosticDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await persistence.SaveAsync(account.Scope, new(StringComparer.Ordinal)
        {
            ["continue|"] = Items(new MediaItem("synthetic-recent", "合成最近播放", MediaKind.Episode),
                new MediaItem("synthetic-offscreen", "合成未显示媒体", MediaKind.Episode)),
            ["latest|synthetic-library"] = Items(new MediaItem("synthetic-latest", "合成最新媒体", MediaKind.Movie)),
        }, Token);
        await using var cache = new QueryCache(new DiagnosticScheduler(), persistence);
        using var recent = ObserveItems(cache, account, "continue", NoNetwork<ImmutableArray<MediaItem>>);
        using var latest = ObserveItems(cache, account, "latest", NoNetwork<ImmutableArray<MediaItem>>, "synthetic-library");
        var visibleRecent = recent.Current[0];
        var visibleLatest = Assert.Single(latest.Current);
        var equalReplacement = visibleRecent with { };

        Assert.Equal(visibleRecent, equalReplacement);
        Assert.NotSame(visibleRecent, equalReplacement);
        Assert.Equal(2, cache.CountRestoredHomeItems([visibleRecent, visibleRecent, visibleLatest, equalReplacement]));
        Assert.Equal(0, cache.CountRestoredHomeItems([equalReplacement]));
        Assert.Equal(0, cache.CountRestoredHomeItems([]));
    }

    [Fact]
    public async Task SuccessfulNetworkRefreshRevokesOriginEvenForEqualMediaWithSameId()
    {
        using var directory = new DiagnosticDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await persistence.SaveAsync(account.Scope, new(StringComparer.Ordinal)
        {
            ["continue|"] = Items(new MediaItem("synthetic-item", "合成媒体", MediaKind.Episode)),
        }, Token);
        await using var cache = new QueryCache(new DiagnosticScheduler(), persistence);
        var response = new TaskCompletionSource<ImmutableArray<MediaItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var query = ObserveItems(cache, account, "continue", token => response.Task.WaitAsync(token));
        var restored = Assert.Single(query.Current);
        var fresh = restored with { };
        Assert.Equal(1, cache.CountRestoredHomeItems([restored]));

        var refresh = query.RefreshAsync(Token);
        Assert.True(query.IsRefreshing);
        Assert.Equal(1, cache.CountRestoredHomeItems([restored]));
        response.SetResult([fresh]);
        await refresh;

        Assert.Same(fresh, Assert.Single(query.Current));
        Assert.Equal(restored, fresh);
        Assert.Equal(0, cache.CountRestoredHomeItems([restored, fresh]));
        Assert.Equal(1, cache.RestoredSnapshotCount);
    }

    [Fact]
    public async Task FailedNetworkRefreshKeepsTheStillDisplayedDiskSnapshotOrigin()
    {
        using var directory = new DiagnosticDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await persistence.SaveAsync(account.Scope, new(StringComparer.Ordinal)
        {
            ["latest|synthetic-library"] = Items(new MediaItem("synthetic-item", "合成媒体", MediaKind.Movie)),
        }, Token);
        await using var cache = new QueryCache(new DiagnosticScheduler(), persistence);
        using var query = ObserveItems(cache, account, "latest", NoNetwork<ImmutableArray<MediaItem>>, "synthetic-library");
        var restored = Assert.Single(query.Current);

        await query.RefreshAsync(Token);

        Assert.NotNull(query.Error);
        Assert.Same(restored, Assert.Single(query.Current));
        Assert.Equal(1, cache.CountRestoredHomeItems([restored]));
    }

    [Fact]
    public async Task SameProcessFreshScopeValuesAreNotDiskOriginWhenReobservedAfterRelogin()
    {
        using var directory = new DiagnosticDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await using var cache = new QueryCache(new DiagnosticScheduler(), persistence);
        var fresh = new MediaItem("synthetic-item", "合成媒体", MediaKind.Episode);
        using var first = ObserveItems(cache, account, "continue", _ => Task.FromResult(ImmutableArray.Create(fresh)));
        await first.RefreshAsync(Token);
        Assert.Same(fresh, Assert.Single(first.Current));
        Assert.Equal(0, cache.CountRestoredHomeItems([fresh]));
        // 即使已经写盘，当前进程的 scope.Values 仍是网络对象；重新建 Entry 不能误报冷恢复。
        await cache.FlushAsync(Token);
        using var relogin = new AccountSession(account.Secret);
        using var next = ObserveItems(cache, relogin, "continue", NoNetwork<ImmutableArray<MediaItem>>);

        Assert.True(next.IsInitialized);
        Assert.Same(fresh, Assert.Single(next.Current));
        Assert.Equal(0, cache.CountRestoredHomeItems([fresh]));
        Assert.Equal(0, cache.RestoredSnapshotCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetAndClearImmediatelyExcludePreviouslyVisibleDiskItems(bool clear)
    {
        using var directory = new DiagnosticDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await persistence.SaveAsync(account.Scope, new(StringComparer.Ordinal)
        {
            ["continue|"] = Items(new MediaItem("synthetic-item", "合成媒体", MediaKind.Episode)),
        }, Token);
        await using var cache = new QueryCache(new DiagnosticScheduler(), persistence);
        var pending = new TaskCompletionSource<ImmutableArray<MediaItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var query = ObserveItems(cache, account, "continue", token => pending.Task.WaitAsync(token));
        var restored = Assert.Single(query.Current);
        Assert.Equal(1, cache.CountRestoredHomeItems([restored]));

        var removal = clear ? cache.ClearAsync(account.Scope, Token) : cache.ResetAsync(account.Scope, Token);

        Assert.Equal(0, cache.CountRestoredHomeItems([restored]));
        await removal;
        Assert.Equal(0, cache.CountRestoredHomeItems([restored]));
        Assert.Equal(1, cache.RestoredSnapshotCount);
    }

    [Fact]
    public async Task DisposedCacheDoesNotCountPreviouslyVisibleDiskItems()
    {
        using var directory = new DiagnosticDirectory();
        using var account = Account();
        var persistence = new QueryPersistence(new AppPaths(directory.Root));
        await persistence.SaveAsync(account.Scope, new(StringComparer.Ordinal)
        {
            ["continue|"] = Items(new MediaItem("synthetic-item", "合成媒体", MediaKind.Episode)),
        }, Token);
        await using var cache = new QueryCache(new DiagnosticScheduler(), persistence);
        using var query = ObserveItems(cache, account, "continue", NoNetwork<ImmutableArray<MediaItem>>);
        var restored = Assert.Single(query.Current);
        Assert.Equal(1, cache.CountRestoredHomeItems([restored]));

        cache.Dispose();

        Assert.Equal(0, cache.CountRestoredHomeItems([restored]));
        Assert.Equal(1, cache.RestoredSnapshotCount);
    }

    private static QuerySnapshot Items(params MediaItem[] items) => new()
    {
        Kind = "items", UpdatedAt = DateTimeOffset.UtcNow, Items = [.. items],
    };

    private static IQuery<ImmutableArray<MediaItem>> ObserveItems(QueryCache cache, AccountSession account, string kind,
        Func<CancellationToken, Task<ImmutableArray<MediaItem>>> fetch, string args = "") =>
        cache.Observe(new(account.Scope, kind, args), account, fetch, staleAfter: TimeSpan.FromDays(1), scopeToken: Token);

    private static Task<T> NoNetwork<T>(CancellationToken _) =>
        Task.FromException<T>(new AppException(ErrorText.Network("加载合成内容")));

    private static AccountSession Account() => new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
        Guid.NewGuid().ToString("N"), "synthetic-user", "合成用户", Guid.NewGuid().ToString("N")));

    private sealed class DiagnosticScheduler : IUiScheduler
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }

    private sealed class DiagnosticDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Mambo-QueryDiagnostics-" + Guid.NewGuid().ToString("N"));
        public DiagnosticDirectory() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            var boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
