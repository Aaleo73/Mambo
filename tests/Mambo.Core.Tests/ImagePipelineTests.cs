using System.Collections.Concurrent;
using System.Net;
using Mambo.Core.Contracts;
using Mambo.Core.Images;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class ImagePipelineTests
{
    [Theory]
    [InlineData(1, 160)]
    [InlineData(160, 160)]
    [InlineData(161, 240)]
    [InlineData(241, 320)]
    [InlineData(641, 960)]
    [InlineData(1921, 2560)]
    [InlineData(5000, 2560)]
    public void WidthRoundingUsesRequestedPixelBuckets(int input, int expected) =>
        Assert.Equal(expected, ImageFetcher.RoundPixelWidth(input));

    [Theory]
    [InlineData(85, false)]
    [InlineData(86, true)]
    public async Task ItemIdentifierLengthUsesUtf8BytesRatherThanCharacterCount(int chineseCharacters, bool rejected)
    {
        using var sandbox = new Sandbox();
        using var accounts = new AccountContext();
        using var cache = new ImageByteCache(sandbox.Paths);
        using var handler = new Handler((_, _) => throw new InvalidOperationException("不应访问网络。"));
        using var fetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: handler);
        var failure = await Assert.ThrowsAsync<AppException>(() => fetcher.FetchAsync(new(new string('海', chineseCharacters), ImageKind.Primary),
            160, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(rejected ? ErrorCodes.InvalidArgument : ErrorCodes.NotLoggedIn, failure.Error.Code);
    }

    [Fact]
    public async Task ImageRouteEscapesIdentityAndUsesPlanQueryOrder()
    {
        using var sandbox = new Sandbox();
        using var accounts = new AccountContext();
        accounts.Set(Account());
        using var cache = new ImageByteCache(sandbox.Paths);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("/Items/item%20with%20space/Images/Backdrop", request.RequestUri!.AbsolutePath);
            Assert.Equal("?Tag=v%26one&MaxWidth=240&MaxHeight=0&Index=2&Quality=90", request.RequestUri.Query);
            return Task.FromResult(Bytes(1, 2, 3));
        });
        using var fetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: handler);
        await fetcher.FetchAsync(new("item with space", ImageKind.Backdrop, "v&one", 2), 161,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void EveryImageIdentityFieldChangesTheCacheKeyAndDisplayIsRedacted()
    {
        var original = Key("item-1", "v1");
        ImageRequestKey[] keys = [original, original with { Server = "https://other.example.invalid" },
            original with { ItemId = "item-2" }, original with { Type = ImageKind.Backdrop }, original with { Tag = "v2" },
            original with { MaxWidth = 240 }, original with { MaxHeight = 240 }, original with { Index = 1 }, original with { Quality = 80 }];
        Assert.Equal(keys.Length, keys.Select(key => key.Hash).Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.Equal(64, key.Hash.Length));
        Assert.DoesNotContain("example.invalid", original.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryLruRespectsAccessOrderAndEntryLimits()
    {
        using var sandbox = new Sandbox();
        using var cache = new ImageByteCache(sandbox.Paths, limits: new(8, 8, 0, 1));
        await cache.StoreAsync(Key("a"), new byte[] { 1, 2, 3, 4 }, cancellationToken: TestContext.Current.CancellationToken);
        await cache.StoreAsync(Key("b"), new byte[] { 5, 6, 7, 8 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(await cache.TryGetAsync(Key("a"), TestContext.Current.CancellationToken));
        await cache.StoreAsync(Key("c"), new byte[] { 9, 10, 11, 12 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await cache.TryGetAsync(Key("b"), TestContext.Current.CancellationToken));
        Assert.NotNull(await cache.TryGetAsync(Key("a"), TestContext.Current.CancellationToken));
        Assert.Equal(8, cache.MemoryBytes);
        await cache.StoreAsync(Key("oversized"), new byte[9], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await cache.TryGetAsync(Key("oversized"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Paths.Images, "*.img", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DiskCacheRequiresTagUpdatesAccessTimeAndRejectsCorruptVersion()
    {
        using var sandbox = new Sandbox();
        var clock = new FakeTimeProvider();
        using var cache = new ImageByteCache(sandbox.Paths, clock, new(0, 128, 1024, 1024));
        var tagged = Key("tagged", "v1");
        await cache.StoreAsync(tagged, new byte[] { 1, 2, 3 }, cancellationToken: TestContext.Current.CancellationToken);
        await cache.StoreAsync(Key("untagged"), new byte[] { 4, 5, 6 }, cancellationToken: TestContext.Current.CancellationToken);
        var path = Assert.Single(Directory.EnumerateFiles(sandbox.Paths.Images, "*.img", SearchOption.AllDirectories));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new byte[] { 1, 2, 3 }, (await cache.TryGetAsync(tagged, TestContext.Current.CancellationToken))!.Value.ToArray());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, File.GetLastWriteTimeUtc(path));
        var corrupt = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        corrupt[8] = 2;
        await File.WriteAllBytesAsync(path, corrupt, TestContext.Current.CancellationToken);
        Assert.Null(await cache.TryGetAsync(tagged, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DiskBudgetTrimsOldestToNinetyPercentAndClearRejectsLateWrites()
    {
        using var sandbox = new Sandbox();
        var clock = new FakeTimeProvider();
        using var cache = new ImageByteCache(sandbox.Paths, clock, new(0, 128, 250, 1));
        for (var index = 0; index < 3; index++)
        {
            await cache.StoreAsync(Key("item-" + index, "v1"), new byte[100], cancellationToken: TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Null(await cache.TryGetAsync(Key("item-0", "v1"), TestContext.Current.CancellationToken));
        var files = Directory.EnumerateFiles(sandbox.Paths.Images, "*.img", SearchOption.AllDirectories).ToArray();
        Assert.True(files.Sum(path => new FileInfo(path).Length) <= 225);
        Assert.NotNull(await cache.TryGetAsync(Key("item-2", "v1"), TestContext.Current.CancellationToken));
        var generation = cache.Generation;
        await cache.ClearAsync(TestContext.Current.CancellationToken);
        await cache.StoreAsync(Key("late", "v1"), new byte[100], generation, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Paths.Images, "*.img", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DuplicateWaitersShareDownloadButOneCancellationDoesNotCancelTheOther()
    {
        using var sandbox = new Sandbox();
        using var accounts = new AccountContext();
        accounts.Set(Account());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult();
            return await response.Task.WaitAsync(token);
        });
        using var cache = new ImageByteCache(sandbox.Paths);
        using var fetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = fetcher.FetchAsync(new("item", ImageKind.Primary), 161, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = fetcher.FetchAsync(new("item", ImageKind.Primary), 239, cancellationToken: TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        response.TrySetResult(Bytes(7, 8, 9));
        Assert.Equal(new byte[] { 7, 8, 9 }, (await second).ToArray());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LastWaiterAndAccountCancellationStopNetworkDownloads()
    {
        using var sandbox = new Sandbox();
        using var accounts = new AccountContext();
        accounts.Set(Account());
        var started = new SemaphoreSlim(0);
        var canceled = new SemaphoreSlim(0);
        using var handler = new Handler(async (_, token) =>
        {
            started.Release();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { canceled.Release(); }
            return Bytes(1);
        });
        using var cache = new ImageByteCache(sandbox.Paths);
        using var fetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: handler);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = fetcher.FetchAsync(new("a", ImageKind.Primary), 160, cancellationToken: caller.Token);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(await canceled.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var second = fetcher.FetchAsync(new("b", ImageKind.Primary), 160, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        accounts.Set(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(await canceled.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        started.Dispose(); canceled.Dispose();
    }

    [Fact]
    public async Task SixSlotsHonorHeroBeforeQueuedPrefetchAndPromoteDuplicateRequest()
    {
        using var sandbox = new Sandbox();
        using var accounts = new AccountContext();
        accounts.Set(Account());
        var calls = new ConcurrentQueue<string>();
        var started = new SemaphoreSlim(0);
        var releases = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        using var handler = new Handler(async (request, token) =>
        {
            var item = request.RequestUri!.Segments[2].TrimEnd('/');
            calls.Enqueue(item);
            var release = releases.GetOrAdd(item, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            started.Release();
            await release.Task.WaitAsync(token);
            return Bytes(1, 2);
        });
        using var cache = new ImageByteCache(sandbox.Paths);
        using var fetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: handler);
        var active = Enumerable.Range(0, 6).Select(index => fetcher.FetchAsync(new("active-" + index, ImageKind.Primary), 160,
            ImagePriority.Prefetch, TestContext.Current.CancellationToken)).ToArray();
        for (var index = 0; index < 6; index++) Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var low = fetcher.FetchAsync(new("low", ImageKind.Primary), 160, ImagePriority.Prefetch, TestContext.Current.CancellationToken);
        var promote = fetcher.FetchAsync(new("promote", ImageKind.Primary), 160, ImagePriority.Prefetch, TestContext.Current.CancellationToken);
        var heroDuplicate = fetcher.FetchAsync(new("promote", ImageKind.Primary), 160, ImagePriority.Hero, TestContext.Current.CancellationToken);
        Assert.Equal(6, calls.Count);
        releases["active-0"].TrySetResult();
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("promote", calls.Last());
        releases["promote"].TrySetResult();
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("low", calls.Last());
        foreach (var release in releases.Values) release.TrySetResult();
        await Task.WhenAll(active.Append(low).Append(promote).Append(heroDuplicate)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(8, calls.Count);
        started.Dispose();
    }

    [Fact]
    public async Task RedirectAcrossHostNeverForwardsAuthenticationAnd404HasStableCode()
    {
        using var sandbox = new Sandbox();
        using var accounts = new AccountContext();
        accounts.Set(Account());
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            calls++;
            Assert.True(request.Headers.Contains("X-Emby-Token"));
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://cdn.example.invalid/image");
            return Task.FromResult(response);
        });
        using var cache = new ImageByteCache(sandbox.Paths);
        using var fetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: handler);
        var blocked = await Assert.ThrowsAsync<AppException>(() => fetcher.FetchAsync(new("item", ImageKind.Primary), 160,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(blocked.Error.Retryable);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(accounts.Current!.Secret.AccessToken, blocked.Message, StringComparison.Ordinal);
        using var notFoundHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var notFoundFetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: notFoundHandler);
        var missing = await Assert.ThrowsAsync<AppException>(() => notFoundFetcher.FetchAsync(new("missing", ImageKind.Primary), 160,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ImageNotFound, missing.Error.Code);
        Assert.False(missing.Error.Retryable);
    }

    [Fact]
    public async Task SameOriginRedirectStripsTokenQueryAndKeepsHeaders()
    {
        using var sandbox = new Sandbox();
        using var accounts = new AccountContext();
        accounts.Set(Account());
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            Assert.True(request.Headers.Contains("X-Emby-Token"));
            if (calls++ == 0)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new("/image?api_key=" + Guid.NewGuid().ToString("N") + "&size=160", UriKind.Relative);
                return Task.FromResult(response);
            }
            Assert.Equal("?size=160", request.RequestUri!.Query);
            return Task.FromResult(Bytes(3));
        });
        using var cache = new ImageByteCache(sandbox.Paths);
        using var fetcher = new ImageFetcher(accounts, cache, Guid.NewGuid(), handler: handler);
        Assert.Equal(new byte[] { 3 }, (await fetcher.FetchAsync(new("item", ImageKind.Primary), 160,
            cancellationToken: TestContext.Current.CancellationToken)).ToArray());
        Assert.Equal(2, calls);
    }

    private static ImageRequestKey Key(string item, string? tag = null) => new("https://media.example.invalid", item, ImageKind.Primary, tag, 160);
    private static AccountSession Account() => new(new SessionSecret("https://media.example.invalid", "server", "user", "演示", Guid.NewGuid().ToString("N")));
    private static HttpResponseMessage Bytes(params byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Sandbox : IDisposable
    {
        private readonly string root = Path.Combine(FindRoot(), "artifacts", "tests", "images-" + Guid.NewGuid().ToString("N"));
        public AppPaths Paths { get; }
        public Sandbox() => Paths = new AppPaths(root);
        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mambo.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? AppContext.BaseDirectory;
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.Combine(FindRoot(), "artifacts", "tests")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录超出 artifacts/tests。");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
