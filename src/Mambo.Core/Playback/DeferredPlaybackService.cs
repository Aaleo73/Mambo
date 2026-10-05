using CommunityToolkit.Mvvm.Messaging;
using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

/// <summary>P1 提供离线预览；真实播放引擎在 P3 接入，失败仍通过会话展示。</summary>
public sealed class DeferredPlaybackService : IPlaybackService, IDisposable, IAsyncDisposable
{
    private readonly AccountContext accounts;
    private readonly IUiScheduler scheduler;
    private readonly FakePlaybackService preview;
    private readonly object gate = new();
    private PendingSession? pending;
    private bool starting, disposed;
    public DeferredPlaybackService(AccountContext accounts, IUiScheduler scheduler, IMessenger messenger, TimeProvider clock)
    {
        this.accounts = accounts; this.scheduler = scheduler;
        var options = new FakeOptions();
        preview = new(new DemoCatalog(), new FakeOperation(options, clock), options, clock, scheduler, messenger);
        preview.SessionStarted += (_, args) => SessionStarted?.Invoke(this, args);
        preview.SessionEnded += (_, args) => SessionEnded?.Invoke(this, args);
        preview.EntrySkipped += (_, args) => EntrySkipped?.Invoke(this, args);
        preview.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
    public IPlaybackSession? Current { get { lock (gate) return pending is { IsClosed: false } ? pending : preview.Current; } }
    public bool IsStarting { get { lock (gate) return starting || preview.IsStarting; } }
    public event EventHandler? Changed;
    public event EventHandler<PlaybackSessionEventArgs>? SessionStarted;
    public event EventHandler<PlaybackSessionEventArgs>? SessionEnded;
    public event EventHandler<PlaybackEntrySkippedEventArgs>? EntrySkipped;
    public async Task<IPlaybackSession> PlayAsync(PlayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.ItemId) || Encoding.UTF8.GetByteCount(request.ItemId) > 256 || request.ItemId.Any(char.IsControl) || request.ItemId is "." or ".." || request.StartTicks < 0) throw Error(ErrorCodes.InvalidArgument, "播放请求无效。");
        if (accounts.Current is null) throw new AppException(new(AppErrorKind.Auth, ErrorCodes.NotLoggedIn, "请先连接服务器。", false));
        IPlaybackSession? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending is { IsClosed: false } existing && existing.Snapshot.Entry?.ItemId == request.ItemId) return existing;
            if (starting) throw Error(ErrorCodes.PlaybackBusy, "正在打开播放，请稍候。");
            previous = Current;
            if (previous is not null && !request.ReplaceCurrent) throw Error(ErrorCodes.ReplaceConfirmationRequired, "正在播放其他项目，请先确认切换播放。");
            starting = true;
        }
        Notify();
        try
        {
            if (previous is not null) await previous.CloseAsync(PlaybackEndReason.Replaced, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            PendingSession created;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                created = new PendingSession(request.ItemId, scheduler, Ended);
                pending = created;
                starting = false;
            }
            scheduler.TryEnqueue(() => { if (!created.IsClosed && !disposed) { SessionStarted?.Invoke(this, new(created)); Changed?.Invoke(this, EventArgs.Empty); created.Fail(); } });
            return created;
        }
        finally { lock (gate) starting = false; Notify(); }
    }
    public Task<IPlaybackSession> PreviewAsync(CancellationToken cancellationToken = default) => PreviewAsync(false, cancellationToken);
    public async Task<IPlaybackSession> PreviewAsync(bool replaceCurrent, CancellationToken cancellationToken = default)
    {
        IPlaybackSession? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (starting) throw Error(ErrorCodes.PlaybackBusy, "正在打开播放，请稍候。");
            previous = pending is { IsClosed: false } ? pending : null;
            if (previous is not null && !replaceCurrent) throw Error(ErrorCodes.ReplaceConfirmationRequired, "正在播放其他项目，请先确认切换播放。");
            starting = true;
        }
        try
        {
            if (previous is not null) await previous.CloseAsync(PlaybackEndReason.Replaced, cancellationToken).ConfigureAwait(false);
            return await preview.PreviewAsync(replaceCurrent, cancellationToken).ConfigureAwait(false);
        }
        finally { lock (gate) starting = false; Notify(); }
    }
    private void Ended(PendingSession session, PlaybackEndReason reason)
    {
        lock (gate) { if (disposed) return; if (ReferenceEquals(pending, session)) pending = null; }
        SessionEnded?.Invoke(this, new(session, reason)); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Notify() => scheduler.TryEnqueue(() => { if (!disposed) Changed?.Invoke(this, EventArgs.Empty); });
    private static AppException Error(string code, string text) => new(new(AppErrorKind.Player, code, text, false));
    public void Dispose() { if (disposed) return; disposed = true; pending?.Abandon(); preview.Dispose(); }
    public async ValueTask DisposeAsync() { if (Current is { } session) await session.CloseAsync(PlaybackEndReason.AppShutdown).ConfigureAwait(false); Dispose(); }
    private sealed class PendingSession(string id, IUiScheduler scheduler, Action<PendingSession, PlaybackEndReason> ended) : IPlaybackSession
    {
        private readonly object gate = new();
        private SessionSnapshot snapshot = new() { Phase = PlayerPhase.Preparing, Entry = new(id, "正在准备播放"), Entries = [new(id, "正在准备播放")] };
        private Task? close;
        public bool IsClosed => Snapshot.Phase == PlayerPhase.Closed;
        public SessionSnapshot Snapshot => Volatile.Read(ref snapshot);
        public event EventHandler? SnapshotChanged;
        public void Fail()
        {
            lock (gate)
            {
                if (IsClosed) return;
                Volatile.Write(ref snapshot, snapshot with { Phase = PlayerPhase.Failed, Error = new(AppErrorKind.Player, "playback.engine_pending", "真实播放引擎尚未接入，请使用播放页预览。", false) });
            }
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        private Task Unavailable(CancellationToken token)
        { token.ThrowIfCancellationRequested(); throw Error(IsClosed ? ErrorCodes.SessionClosed : "playback.engine_pending", IsClosed ? "播放已经结束。" : "真实播放引擎尚未接入。"); }
        public Task TogglePauseAsync(CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SetVideoQualityModeAsync(VideoQualityMode mode, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task PreviousAsync(CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task NextAsync(CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task SelectEntryAsync(string itemId, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task StepFrameAsync(FrameStepDirection direction, CancellationToken cancellationToken = default) => Unavailable(cancellationToken);
        public Task RetryAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); if (IsClosed) return Unavailable(cancellationToken); scheduler.TryEnqueue(Fail); return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) => CloseAsync(PlaybackEndReason.UserClosed, cancellationToken);
        public Task CloseAsync(PlaybackEndReason reason, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (close is null)
                {
                    Volatile.Write(ref snapshot, snapshot with { Phase = PlayerPhase.Closed });
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    close = completion.Task;
                    if (!scheduler.TryEnqueue(() => { try { SnapshotChanged?.Invoke(this, EventArgs.Empty); ended(this, reason); } finally { completion.TrySetResult(); } })) completion.TrySetResult();
                }
                return cancellationToken.CanBeCanceled ? close.WaitAsync(cancellationToken) : close;
            }
        }
        public void Abandon() { lock (gate) Volatile.Write(ref snapshot, snapshot with { Phase = PlayerPhase.Closed }); SnapshotChanged = null; }
    }
}
