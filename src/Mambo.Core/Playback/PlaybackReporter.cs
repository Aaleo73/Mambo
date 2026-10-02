using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Reliability;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

/// <summary>会话的有序网络队列；停止记录立即落盘，退出不等待过时的进度请求。</summary>
public sealed class PlaybackReporter : IAsyncDisposable
{
    private readonly AccountSession account;
    private readonly EmbyApi api;
    private readonly StopOutbox outbox;
    private readonly IUiScheduler scheduler;
    private readonly IMessenger messenger;
    private readonly TimeProvider clock;
    private readonly Action<AppError>? log;
    private readonly Func<bool> isCurrentAccount;
    private readonly Channel<ReportWork> queue = Channel.CreateUnbounded<ReportWork>(new() { SingleReader = true });
    private readonly HashSet<string> playingSucceeded = new(StringComparer.Ordinal);
    private readonly HashSet<string> stopping = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private CancellationTokenSource? activeNetwork;
    private string? activeId;
    private DateTimeOffset? shutdownDeadline;
    private readonly Task worker;

    public PlaybackReporter(AccountSession account, EmbyApi api, StopOutbox outbox,
        IUiScheduler scheduler, IMessenger messenger, TimeProvider clock, Action<AppError>? log = null,
        Func<bool>? isCurrentAccount = null)
    {
        this.account = account; this.api = api; this.outbox = outbox; this.scheduler = scheduler;
        this.messenger = messenger; this.clock = clock; this.log = log;
        this.isCurrentAccount = isCurrentAccount ?? (() => !account.Token.IsCancellationRequested);
        worker = RunAsync();
    }
    public Task PlayingAsync(string id, PlaybackReport payload) => Enqueue(new(id, payload, "", clock.GetUtcNow()));
    public Task ProgressAsync(string id, PlaybackReport payload) => Enqueue(new(id, payload, "Progress", clock.GetUtcNow()));
    public Task CleanupAsync(string playSessionId, TimeSpan budget)
        => Enqueue(new(Guid.NewGuid().ToString("D"), new() { PlaySessionId = playSessionId }, "Cleanup", clock.GetUtcNow(), Transcode: true, Budget: budget));
    public void BeginShutdown(TimeSpan budget)
    {
        lock (gate) { shutdownDeadline ??= clock.GetUtcNow() + budget; activeNetwork?.Cancel(); }
    }

    public Task StoppedAsync(string id, PlaybackEntry entry, PlaybackReport payload, bool transcode, TimeSpan budget)
    {
        lock (gate)
        {
            stopping.Add(id);
            if (activeId == id) activeNetwork?.Cancel();
        }
        // 不在网络队列中排队等待，先保证最终位置可以在进程退出后恢复。
        var saved = outbox.QueueAsync(new(account.Secret.ServerId, account.Address.Uri.AbsoluteUri,
            account.Secret.UserId, id, entry.ItemId, payload.PlaybackStartTimeTicks, payload.PositionTicks)
        {
            MediaSourceId = payload.MediaSourceId, LiveStreamId = payload.LiveStreamId, PlaySessionId = payload.PlaySessionId,
            PlayMethod = payload.PlayMethod, PlaybackRate = payload.PlaybackRate, CanSeek = payload.CanSeek,
            IsPaused = payload.IsPaused, IsMuted = payload.IsMuted, VolumeLevel = payload.VolumeLevel,
            PlaylistIndex = payload.PlaylistIndex, PlaylistLength = payload.PlaylistLength,
            AudioStreamIndex = payload.AudioStreamIndex, SubtitleStreamIndex = payload.SubtitleStreamIndex,
        });
        // 立即占据顺序位置；下一集 Playing 不能越过这次异步磁盘写入。
        return Enqueue(new(id, payload, "Stopped", clock.GetUtcNow(), entry, transcode, budget, saved));
    }

    private Task Enqueue(ReportWork work)
    {
        if (!queue.Writer.TryWrite(work)) work.Completion.TrySetException(new ObjectDisposedException(nameof(PlaybackReporter)));
        return work.Completion.Task;
    }
    private async Task RunAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (work.Saved is { } saved) await saved.ConfigureAwait(false);
                var budget = work.Budget ?? TimeSpan.FromSeconds(3);
                CancellationTokenSource deadline;
                CancellationTokenSource network;
                lock (gate)
                {
                    if (work.Kind is "" or "Progress" && (shutdownDeadline is not null || stopping.Contains(work.Id)))
                    { work.Completion.TrySetResult(); continue; }
                    // 与 BeginShutdown 原子地计算预算并发布取消源，避免退出漏掉刚启动的请求。
                    var remaining = budget - (clock.GetUtcNow() - work.QueuedAt);
                    if (shutdownDeadline is { } end) remaining = remaining < end - clock.GetUtcNow() ? remaining : end - clock.GetUtcNow();
                    deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
                    if (remaining <= TimeSpan.Zero) deadline.Cancel();
                    network = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, account.Token);
                    activeNetwork = network; activeId = work.Id;
                }
                using var ownedDeadline = deadline;
                using var ownedNetwork = network;
                try
                {
                    if (work.Kind == "Cleanup") await CleanupCoreAsync(work, network.Token).ConfigureAwait(false);
                    else if (work.Entry is not null)
                    {
                        try
                        {
                            if (!playingSucceeded.Contains(work.Id)) await TryPlayingAsync(work, network.Token).ConfigureAwait(false);
                            await outbox.FlushAsync(account, budget, network.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            await CleanupCoreAsync(work, network.Token).ConfigureAwait(false);
                            NotifyStopped(work.Entry);
                        }
                    }
                    else if (work.Kind.Length == 0) await TryPlayingAsync(work, network.Token).ConfigureAwait(false);
                    else
                    {
                        if (!playingSucceeded.Contains(work.Id)) await TryPlayingAsync(work, network.Token).ConfigureAwait(false);
                        if (playingSucceeded.Contains(work.Id))
                            await api.ReportAsync(account, "Progress", work.Payload, network.Token).ConfigureAwait(false);
                    }
                }
                finally { lock (gate) { activeNetwork = null; activeId = null; } }
                work.Completion.TrySetResult();
            }
            catch (AppException error) { log?.Invoke(error.Error); work.Completion.TrySetException(error); }
            catch (OperationCanceledException) { work.Completion.TrySetResult(); }
            catch (Exception) { work.Completion.TrySetException(new AppException(new(AppErrorKind.Persistence,
                ErrorCodes.PersistenceFailed, "保存播放停止记录失败。", true))); }
        }
    }
    private async Task CleanupCoreAsync(ReportWork work, CancellationToken cancellationToken)
    {
        if (!work.Transcode || work.Payload.PlaySessionId is not { Length: > 0 } id) return;
        try { await api.CleanupAsync(account, id, cancellationToken).ConfigureAwait(false); }
        catch (AppException error) { log?.Invoke(error.Error); }
        catch (OperationCanceledException) { }
    }
    private void NotifyStopped(PlaybackEntry entry)
        => scheduler.TryEnqueue(() => { if (isCurrentAccount()) messenger.Send(new PlaybackStopped(entry.ItemId, entry.SeriesId, entry.SeasonId)); });
    private async Task TryPlayingAsync(ReportWork work, CancellationToken cancellationToken)
    {
        try
        {
            await api.ReportAsync(account, "", work.Payload with { EventName = null }, cancellationToken).ConfigureAwait(false);
            playingSucceeded.Add(work.Id);
        }
        catch (AppException error) { log?.Invoke(error.Error); }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await worker.ConfigureAwait(false); }
    private sealed record ReportWork(string Id, PlaybackReport Payload, string Kind, DateTimeOffset QueuedAt,
        PlaybackEntry? Entry = null, bool Transcode = false, TimeSpan? Budget = null, Task? Saved = null)
    { public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
}
