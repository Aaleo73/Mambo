using System.Collections.Immutable;
using Mambo.Core.Contracts;

namespace Mambo.Core.Data;

public sealed record QueryPage<T>(ImmutableArray<T> Items, int? TotalCount, bool HasMore)
{
    public int? NextOffset { get; init; }
}

/// <summary>共享请求的演示分页；代际校验避免已取消的加载追加到新结果。</summary>
public class ObservablePagedQuery<T> : IPagedQuery<T>
{
    private readonly object gate = new();
    private readonly IUiScheduler scheduler;
    private readonly Func<int, int, CancellationToken, Task<QueryPage<T>>> loader;
    private readonly int pageSize;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? operationCancellation;
    private Task? active;
    private bool activeRefresh;
    private long generation;
    private bool disposed;
    private bool notificationQueued;
    private ImmutableArray<T> items = [];
    private AppError? error;
    private int? totalCount;
    private bool initialized;
    private bool hasMore = true;
    private bool loading;

    public ObservablePagedQuery(IUiScheduler scheduler,
        Func<int, int, CancellationToken, Task<QueryPage<T>>> loader, int pageSize = 60,
        QueryPage<T>? initial = null, CancellationToken scopeToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        this.scheduler = scheduler;
        this.loader = loader;
        this.pageSize = pageSize;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(scopeToken);
        if (initial is not null)
        {
            if (initial.Items.IsDefault) throw new ArgumentException("缓存页条目无效。", nameof(initial));
            items = initial.Items; totalCount = initial.TotalCount; hasMore = initial.HasMore; initialized = true;
        }
    }

    public ImmutableArray<T> Items { get { lock (gate) return items; } }
    public AppError? Error { get { lock (gate) return error; } }
    public int? TotalCount { get { lock (gate) return totalCount; } }
    public bool IsInitialized { get { lock (gate) return initialized; } }
    public bool HasMore { get { lock (gate) return hasMore; } }
    public bool IsLoading { get { lock (gate) return loading; } }
    public bool IsRefreshing { get { lock (gate) return loading && activeRefresh; } }
    public event EventHandler? Updated;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Begin(true, cancellationToken);
    public Task LoadMoreAsync(CancellationToken cancellationToken = default) => Begin(false, cancellationToken);

    private Task Begin(bool refresh, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        CancellationTokenSource cancellation;
        long requestGeneration;
        int offset;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (active is not null)
            {
                if (!refresh || activeRefresh) return WaitAsync(active, cancellationToken);
                operationCancellation?.Cancel();
            }
            else if (!refresh && initialized && !hasMore) return Task.CompletedTask;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            operationCancellation = cancellation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            active = completion.Task;
            activeRefresh = refresh;
            requestGeneration = ++generation;
            offset = refresh ? 0 : items.Length;
            loading = true;
        }
        Notify();
        _ = LoadCoreAsync(completion, cancellation, requestGeneration, offset, refresh);
        return WaitAsync(completion.Task, cancellationToken);
    }

    private async Task LoadCoreAsync(TaskCompletionSource completion, CancellationTokenSource cancellation,
        long requestGeneration, int offset, bool refresh)
    {
        var cancelled = false;
        try
        {
            var page = await loader(offset, pageSize, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (page.Items.IsDefault || page.Items.Length > pageSize || page.TotalCount < 0 ||
                (page.TotalCount is { } count && (count < offset + page.Items.Length ||
                    page.HasMore != (offset + page.Items.Length < count))) ||
                (page.HasMore && page.Items.IsEmpty))
                throw new AppException(new AppError(AppErrorKind.Contract, "demo.invalid_page", "演示分页结果无效。", false));
            lock (gate)
            {
                if (!disposed && !cancellation.IsCancellationRequested && requestGeneration == generation)
                {
                    items = refresh ? page.Items : items.AddRange(page.Items);
                    totalCount = page.TotalCount;
                    hasMore = page.HasMore;
                    initialized = true;
                    error = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            cancelled = cancellation.IsCancellationRequested;
            lock (gate)
            {
                if (!disposed && !cancellation.IsCancellationRequested && requestGeneration == generation) error = exception is AppException app ? app.Error :
                    new AppError(AppErrorKind.Network, ErrorCodes.NetworkUnavailable, "数据暂时无法加载，请重试。", true);
            }
        }
        finally
        {
            lock (gate)
            {
                if (requestGeneration == generation)
                {
                    active = null;
                    operationCancellation = null;
                    loading = false;
                }
            }
            Notify();
            if (cancelled) completion.TrySetCanceled(cancellation.Token);
            else completion.TrySetResult();
            cancellation.Dispose();
        }
    }

    private void Notify()
    {
        lock (gate)
        {
            if (disposed || notificationQueued) return;
            notificationQueued = true;
        }
        if (!scheduler.TryEnqueue(() =>
        {
            lock (gate)
            {
                notificationQueued = false;
                if (disposed) return;
            }
            Updated?.Invoke(this, EventArgs.Empty);
        }))
        {
            lock (gate) notificationQueued = false;
        }
    }

    private static Task WaitAsync(Task task, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            loading = false;
            generation++;
            Updated = null;
            lifetime.Cancel();
            operationCancellation?.Cancel();
        }
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
