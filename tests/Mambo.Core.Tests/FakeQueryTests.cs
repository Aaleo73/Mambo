using System.Collections.Concurrent;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class FakeQueryTests
{
    [Fact]
    public async Task FailedRefreshRetainsValueAndNotifiesOnlyThroughUiScheduler()
    {
        var scheduler = new FakeReadScheduler();
        var calls = 0;
        using var query = new FakeQuery<int>(scheduler, _ => ++calls == 1 ? Task.FromResult(42) :
            Task.FromException<int>(Failure()));
        var updates = 0;
        query.Updated += (_, _) => updates++;

        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(query.IsInitialized);
        Assert.Equal(42, query.Current);
        Assert.Equal(0, updates);
        scheduler.Drain();
        Assert.Equal(1, updates);

        await query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(42, query.Current);
        Assert.Equal("demo.test_failed", query.Error?.Code);
        Assert.False(query.IsRefreshing);
        Assert.Equal(1, updates);
        scheduler.Drain();
        Assert.Equal(2, updates);
    }

    [Fact]
    public async Task CancellingOneWaiterKeepsSharedRefreshForAnotherWaiter()
    {
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var query = new FakeQuery<int>(new FakeReadScheduler(), _ => { calls++; return source.Task; });
        using var waiterCancellation = new CancellationTokenSource();
        var cancelledWait = query.RefreshAsync(waiterCancellation.Token);
        var sharedWait = query.RefreshAsync(TestContext.Current.CancellationToken);
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        Assert.True(query.IsRefreshing);
        Assert.Equal(1, calls);

        source.SetResult(7);
        await sharedWait;
        Assert.Equal(7, query.Current);
        Assert.Null(query.Error);
    }

    [Fact]
    public async Task DisposePreventsLateValueAndQueuedNotificationsEvenIfLoaderIgnoresCancellation()
    {
        var scheduler = new FakeReadScheduler();
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new FakeQuery<string>(scheduler, _ => source.Task);
        var updates = 0;
        query.Updated += (_, _) => updates++;
        var loading = query.RefreshAsync(TestContext.Current.CancellationToken);
        query.Dispose();
        source.SetResult("迟到的结果");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        scheduler.Drain();
        Assert.False(query.IsInitialized);
        Assert.Null(query.Current);
        Assert.False(query.IsRefreshing);
        Assert.Equal(0, updates);
        query.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = query.RefreshAsync(TestContext.Current.CancellationToken); });
    }

    [Fact]
    public async Task ScopeCancellationPreventsLateResult()
    {
        using var scope = new CancellationTokenSource();
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var query = new FakeQuery<int>(new FakeReadScheduler(), _ => source.Task, scope.Token);
        var loading = query.RefreshAsync(TestContext.Current.CancellationToken);
        scope.Cancel();
        source.SetResult(99);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.False(query.IsInitialized);
        Assert.Null(query.Error);
        Assert.False(query.IsRefreshing);
    }

    [Fact]
    public async Task ScopeCancellationPreventsLateErrorFromUncooperativeLoader()
    {
        using var scope = new CancellationTokenSource();
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var query = new FakeQuery<int>(new FakeReadScheduler(), _ => source.Task, scope.Token);
        var loading = query.RefreshAsync(TestContext.Current.CancellationToken);
        scope.Cancel();
        source.SetException(Failure());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Null(query.Error);
        Assert.False(query.IsInitialized);
    }

    [Fact]
    public async Task FakeDelayUsesProvidedClock()
    {
        var clock = new FakeTimeProvider();
        var operation = new FakeOperation(new FakeOptions { Delay = TimeSpan.FromSeconds(5) }, clock);
        using var query = new FakeQuery<int>(new FakeReadScheduler(), async cancellationToken =>
        {
            await operation.ExecuteAsync(cancellationToken);
            return 5;
        });
        var loading = query.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(query.IsRefreshing);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(loading.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await loading;
        Assert.Equal(5, query.Current);
    }

    [Fact]
    public async Task ConcurrentLoadMoreSharesOnePageAndDoesNotSkipOffsets()
    {
        var offsets = new List<int>();
        var firstPage = new TaskCompletionSource<FakePage<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var query = new FakePagedQuery<int>(new FakeReadScheduler(), (offset, _, _) =>
        {
            offsets.Add(offset);
            return offset == 0 ? firstPage.Task : Task.FromResult(new FakePage<int>([3, 4], 4, false));
        }, 2);
        var first = query.LoadMoreAsync(TestContext.Current.CancellationToken);
        var concurrent = query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Single(offsets);
        firstPage.SetResult(new([1, 2], 4, true));
        await Task.WhenAll(first, concurrent);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal([0, 2], offsets);
        Assert.Equal([1, 2, 3, 4], query.Items);
        Assert.False(query.HasMore);
        Assert.Equal(4, query.TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshSupersedesInFlightPageWithoutAppendingItsLateResultOrError(bool lateFailure)
    {
        var latePage = new TaskCompletionSource<FakePage<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var query = new FakePagedQuery<int>(new FakeReadScheduler(), (offset, _, _) =>
            offset > 0 ? latePage.Task : Task.FromResult(++calls == 1 ?
                new FakePage<int>([1, 2], 4, true) : new FakePage<int>([7, 8], 2, false)), 2);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        var loadingMore = query.LoadMoreAsync(TestContext.Current.CancellationToken);
        await query.RefreshAsync(TestContext.Current.CancellationToken);
        if (lateFailure) latePage.SetException(Failure());
        else latePage.SetResult(new([3, 4], 4, false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loadingMore);
        Assert.Equal([7, 8], query.Items);
        Assert.Equal(2, query.TotalCount);
        Assert.False(query.IsLoading);
        Assert.Null(query.Error);
    }

    [Fact]
    public async Task FailedPageRetainsItemsAndRetriesSameOffset()
    {
        var calls = 0;
        var offsets = new List<int>();
        using var query = new FakePagedQuery<int>(new FakeReadScheduler(), (offset, _, _) =>
        {
            offsets.Add(offset);
            return ++calls switch
            {
                1 => Task.FromResult(new FakePage<int>([1, 2], 3, true)),
                2 => Task.FromException<FakePage<int>>(Failure()),
                _ => Task.FromResult(new FakePage<int>([3], 3, false)),
            };
        }, 2);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], query.Items);
        Assert.NotNull(query.Error);
        Assert.True(query.HasMore);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal([0, 2, 2], offsets);
        Assert.Equal([1, 2, 3], query.Items);
        Assert.Null(query.Error);
    }

    [Fact]
    public async Task InvalidEmptyPageWithMoreDoesNotCreateEndlessPaging()
    {
        using var query = new FakePagedQuery<int>(new FakeReadScheduler(), (_, _, _) =>
            Task.FromResult(new FakePage<int>([], 10, true)), 2);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Empty(query.Items);
        Assert.False(query.IsInitialized);
        Assert.Equal(AppErrorKind.Contract, query.Error?.Kind);
        Assert.False(query.IsLoading);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public async Task InconsistentTotalCountDoesNotPublishInvalidPage(int totalCount, bool hasMore)
    {
        using var query = new FakePagedQuery<int>(new FakeReadScheduler(), (_, _, _) =>
            Task.FromResult(new FakePage<int>([1, 2], totalCount, hasMore)), 2);
        await query.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Empty(query.Items);
        Assert.False(query.IsInitialized);
        Assert.Equal(AppErrorKind.Contract, query.Error?.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void QueryRejectsInvalidPageSizes(int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakePagedQuery<int>(new FakeReadScheduler(),
            (_, _, _) => Task.FromResult(new FakePage<int>([], 0, false)), pageSize));

    [Fact]
    public async Task PagingScopeCancellationPreventsLateAppend()
    {
        using var scope = new CancellationTokenSource();
        var latePage = new TaskCompletionSource<FakePage<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var query = new FakePagedQuery<int>(new FakeReadScheduler(), (_, _, _) => latePage.Task, 2, scope.Token);
        var loading = query.LoadMoreAsync(TestContext.Current.CancellationToken);
        scope.Cancel();
        latePage.SetResult(new([1, 2], 2, false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Empty(query.Items);
        Assert.False(query.IsInitialized);
        Assert.Null(query.Error);
        Assert.False(query.IsLoading);
    }

    private static AppException Failure() => new(new AppError(AppErrorKind.Network,
        "demo.test_failed", "演示请求失败。", true));
}

internal sealed class FakeReadScheduler : IUiScheduler
{
    private readonly ConcurrentQueue<Action> callbacks = new();
    public bool TryEnqueue(Action callback) { callbacks.Enqueue(callback); return true; }
    public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
}
