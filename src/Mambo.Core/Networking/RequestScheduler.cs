using Mambo.Core.Contracts;

namespace Mambo.Core.Networking;

public enum RequestPriority { Foreground, Visible, Background }
public enum RequestLane { Metadata, Reliability }

/// <summary>metadata 四并发，后台最多三；reliability 两并发。截止时间包含排队，GET 可重试一次。</summary>
public sealed class RequestScheduler(TimeProvider? timeProvider = null) : IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly PriorityQueue<Work, (int Priority, long Sequence)> queue = new();
    private readonly CancellationTokenSource lifetime = new();
    private long sequence;
    private int metadata, background, reliability;
    private bool disposed;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, RequestPriority priority = RequestPriority.Foreground,
        RequestLane lane = RequestLane.Metadata, bool idempotent = true, CancellationToken scopeToken = default)
    {
        using var deadline = new CancellationTokenSource(priority == RequestPriority.Foreground ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(20), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scopeToken, deadline.Token, lifetime.Token);
        var requestToken = linked.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(priority, lane, async () =>
        {
            try
            {
                T value;
                try { value = await operation(requestToken).ConfigureAwait(false); }
                catch (AppException error) when (idempotent && error.Error.Retryable && !requestToken.IsCancellationRequested)
                { value = await operation(requestToken).ConfigureAwait(false); }
                completion.TrySetResult(value);
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested) { completion.TrySetCanceled(requestToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }, () => completion.TrySetCanceled(requestToken), clock.GetUtcNow() + (priority == RequestPriority.Foreground ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(20)), requestToken);
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); queue.Enqueue(work, ((int)priority, sequence++)); }
        using var registration = requestToken.Register(Pump);
        Pump();
        try { return await completion.Task.WaitAsync(requestToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!scopeToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new AppException(ErrorText.Network("请求")); }
    }
    private void Pump()
    {
        var start = new List<Work>();
        var cancel = new List<Work>();
        lock (gate)
        {
            var pending = new List<(Work Work, (int, long) Priority)>();
            while (queue.TryDequeue(out var work, out var priority))
            {
                if (work.Token.IsCancellationRequested || clock.GetUtcNow() >= work.Deadline) { cancel.Add(work); continue; }
                var available = work.Lane == RequestLane.Reliability ? reliability < 2 : metadata < 4 && (work.Priority == RequestPriority.Foreground || background < 3);
                if (!available) { pending.Add((work, priority)); continue; }
                if (work.Lane == RequestLane.Reliability) reliability++;
                else { metadata++; if (work.Priority != RequestPriority.Foreground) background++; }
                start.Add(work);
            }
            foreach (var item in pending) queue.Enqueue(item.Work, item.Priority);
        }
        foreach (var work in cancel) work.Cancel();
        foreach (var work in start) _ = ExecuteAsync(work);
    }
    private async Task ExecuteAsync(Work work)
    {
        try { await work.Run().ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                if (work.Lane == RequestLane.Reliability) reliability--;
                else { metadata--; if (work.Priority != RequestPriority.Foreground) background--; }
            }
            Pump();
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        lifetime.Cancel();
        Pump();
    }
    private sealed record Work(RequestPriority Priority, RequestLane Lane, Func<Task> Run, Action Cancel, DateTimeOffset Deadline, CancellationToken Token);
}
