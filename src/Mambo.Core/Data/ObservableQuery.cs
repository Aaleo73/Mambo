using Mambo.Core.Contracts;

namespace Mambo.Core.Data;

/// <summary>保留上次值的演示读取；调用者取消自己的等待，观察作用域控制共享工作。</summary>
public class ObservableQuery<T> : IQuery<T>
{
    private readonly object gate = new();
    private readonly IUiScheduler scheduler;
    private readonly Func<CancellationToken, Task<T>> loader;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? operationCancellation;
    private Task? active;
    private bool disposed;
    private bool notificationQueued;
    private T? current;
    private AppError? error;
    private bool initialized;
    private bool refreshing;

    public ObservableQuery(IUiScheduler scheduler, Func<CancellationToken, Task<T>> loader,
        CancellationToken scopeToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(loader);
        this.scheduler = scheduler;
        this.loader = loader;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(scopeToken);
    }

    public T? Current { get { lock (gate) return current; } }
    public AppError? Error { get { lock (gate) return error; } }
    public bool IsInitialized { get { lock (gate) return initialized; } }
    public bool IsRefreshing { get { lock (gate) return refreshing; } }
    public event EventHandler? Updated;

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        CancellationTokenSource cancellation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (active is not null) return WaitAsync(active, cancellationToken);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            operationCancellation = cancellation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            active = completion.Task;
            refreshing = true;
        }
        Notify();
        _ = RefreshCoreAsync(completion, cancellation);
        return WaitAsync(completion.Task, cancellationToken);
    }

    private async Task RefreshCoreAsync(TaskCompletionSource completion, CancellationTokenSource cancellation)
    {
        var cancelled = false;
        try
        {
            var value = await loader(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!disposed && !cancellation.IsCancellationRequested)
                {
                    current = value;
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
                if (!disposed && !cancellation.IsCancellationRequested) error = exception is AppException app ? app.Error :
                    new AppError(AppErrorKind.Network, ErrorCodes.NetworkUnavailable, "数据暂时无法加载，请重试。", true);
            }
        }
        finally
        {
            lock (gate)
            {
                active = null;
                operationCancellation = null;
                refreshing = false;
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
            refreshing = false;
            Updated = null;
            lifetime.Cancel();
            operationCancellation?.Cancel();
        }
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
