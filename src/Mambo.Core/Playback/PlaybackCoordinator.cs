using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Reliability;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

public sealed class PlaybackCoordinator : IPlaybackService, IDisposable, IAsyncDisposable
{
    private readonly AccountContext accounts;
    private readonly IEntryPreparer preparer;
    private readonly Func<CancellationToken, Task<IPlayerEngine>> factory;
    private readonly EmbyApi api;
    private readonly StopOutbox outbox;
    private readonly ISettingsService settings;
    private readonly VideoQualityPreferences? videoQualityPreferences;
    private readonly IUiScheduler scheduler;
    private readonly IMessenger messenger;
    private readonly TimeProvider clock;
    private readonly Action<AppError>? log;
    private readonly FakePlaybackService preview;
    private readonly object gate = new();
    private PlaybackSession? current;
    private bool starting, disposed;

    public PlaybackCoordinator(AccountContext accounts, IEntryPreparer preparer,
        Func<CancellationToken, Task<IPlayerEngine>> factory, EmbyApi api, StopOutbox outbox,
        ISettingsService settings, IUiScheduler scheduler, IMessenger messenger, TimeProvider clock,
        Action<AppError>? log = null, VideoQualityPreferences? videoQualityPreferences = null)
    {
        this.accounts = accounts; this.preparer = preparer; this.factory = factory; this.api = api;
        this.outbox = outbox; this.settings = settings; this.scheduler = scheduler;
        this.videoQualityPreferences = videoQualityPreferences;
        this.messenger = messenger; this.clock = clock; this.log = log;
        var options = new FakeOptions();
        preview = new(new DemoCatalog(), new FakeOperation(options, clock), options, clock, scheduler, messenger);
        preview.SessionStarted += (_, args) => SessionStarted?.Invoke(this, args);
        preview.SessionEnded += (_, args) => SessionEnded?.Invoke(this, args);
        preview.EntrySkipped += (_, args) => EntrySkipped?.Invoke(this, args);
        preview.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        accounts.Changed += AccountChanged;
    }
    public IPlaybackSession? Current { get { lock (gate) return current ?? preview.Current; } }
    public bool IsStarting { get { lock (gate) return starting || preview.IsStarting; } }
    public event EventHandler? Changed;
    public event EventHandler<PlaybackSessionEventArgs>? SessionStarted;
    public event EventHandler<PlaybackSessionEventArgs>? SessionEnded;
    public event EventHandler<PlaybackEntrySkippedEventArgs>? EntrySkipped;

    public async Task<IPlaybackSession> PlayAsync(PlayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.ItemId) || Encoding.UTF8.GetByteCount(request.ItemId) > 256 ||
            request.ItemId.Any(char.IsControl) || request.ItemId is "." or ".." || request.StartTicks < 0)
            throw Reject(ErrorCodes.InvalidArgument, "播放请求无效。");
        var account = accounts.Current ?? throw new AppException(new(AppErrorKind.Auth, ErrorCodes.NotLoggedIn, "请先连接服务器。", false));
        IPlaybackSession? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is { } same && (same.RequestedItemId == request.ItemId || same.Snapshot.Entry?.ItemId == request.ItemId)) return same;
            if (starting) throw Reject(ErrorCodes.PlaybackBusy, "正在打开播放，请稍候。");
            previous = Current;
            if (previous is not null && !request.ReplaceCurrent) throw Reject(ErrorCodes.ReplaceConfirmationRequired, "正在播放其他项目，请先确认切换播放。");
            starting = true;
        }
        Notify();
        try
        {
            if (previous is not null) await previous.CloseAsync(PlaybackEndReason.Replaced, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            PlaybackSession session;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(accounts.Current, account)) throw new AppException(new(AppErrorKind.Auth, ErrorCodes.SessionChanged, "账号已改变，请重新播放。", false));
                var reporter = new PlaybackReporter(account, api, outbox, scheduler, messenger, clock, log,
                    () => ReferenceEquals(accounts.Current, account));
                session = new(request, account, preparer, factory, reporter, scheduler, clock, settings.Current.Volume,
                    Ended, (entry, error) => EntrySkipped?.Invoke(this, new(entry, error)), log,
                    settings.Current.PlaybackMode == PlaybackMode.External ? EngineKind.External : EngineKind.Embedded);
                session.Settings = settings;
                session.VideoQualityPreferences = videoQualityPreferences;
                current = session;
            }
            scheduler.TryEnqueue(() => { if (!disposed) { SessionStarted?.Invoke(this, new(session)); Changed?.Invoke(this, EventArgs.Empty); } });
            session.Start();
            return session;
        }
        finally { lock (gate) starting = false; Notify(); }
    }
    public Task<IPlaybackSession> PreviewAsync(CancellationToken cancellationToken = default) => PreviewAsync(false, cancellationToken);
    public async Task<IPlaybackSession> PreviewAsync(bool replaceCurrent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlaybackSession? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (starting) throw Reject(ErrorCodes.PlaybackBusy, "正在打开播放，请稍候。");
            previous = current;
            if (previous is not null && !replaceCurrent) throw Reject(ErrorCodes.ReplaceConfirmationRequired, "正在播放其他项目，请先确认切换播放。");
            starting = true;
        }
        try
        {
            if (previous is not null) await previous.CloseAsync(PlaybackEndReason.Replaced, cancellationToken).ConfigureAwait(false);
            return await preview.PreviewAsync(replaceCurrent, cancellationToken).ConfigureAwait(false);
        }
        finally { lock (gate) starting = false; Notify(); }
    }
    private void Ended(PlaybackSession session, PlaybackEndReason reason)
    {
        lock (gate) if (current == session) current = null;
        scheduler.TryEnqueue(() => { if (!disposed) { SessionEnded?.Invoke(this, new(session, reason)); Changed?.Invoke(this, EventArgs.Empty); } });
    }
    private void AccountChanged()
    {
        if (Current is PlaybackSession session) _ = CloseAfterAccountChangeAsync(session);
    }
    private async Task CloseAfterAccountChangeAsync(PlaybackSession session)
    {
        try { await session.CloseAsync(PlaybackEndReason.Logout).ConfigureAwait(false); }
        catch (AppException error) { log?.Invoke(error.Error); }
    }
    private void Notify() => scheduler.TryEnqueue(() => { if (!disposed) Changed?.Invoke(this, EventArgs.Empty); });
    private static AppException Reject(string code, string message) => new(new(AppErrorKind.Contract, code, message, false));
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        accounts.Changed -= AccountChanged;
        if (current is { } session) _ = CloseAfterAccountChangeAsync(session);
        preview.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        try { if (Current is { } active) await active.CloseAsync(PlaybackEndReason.AppShutdown).ConfigureAwait(false); }
        finally { Dispose(); }
    }
}
