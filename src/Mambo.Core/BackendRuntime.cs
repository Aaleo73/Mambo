using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Data;
using Mambo.Core.Diagnostics;
using Mambo.Core.Images;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;

namespace Mambo.Core;

/// <summary>真实平台层的显式组合根，前端只获取 Contracts 服务。外壳启动后调用一次 Session.RestoreAsync。</summary>
public sealed class BackendRuntime : IDisposable, IAsyncDisposable
{
    private Task? closing;
    private bool disposed;
    private IPlaybackSession? observedPlayback;
    private double lastVolume;
    private Task volumeWrite = Task.CompletedTask;
    private readonly object volumeGate = new();
    private readonly IUiScheduler scheduler;
    private readonly IMessenger messenger;
    public BackendRuntime(AppPaths paths, ISecretStore secrets, IUiScheduler scheduler, IMessenger messenger,
        TimeProvider? clock = null, HttpMessageHandler? apiHandler = null, HttpMessageHandler? imageHandler = null,
        Func<CancellationToken, Task<IPlayerEngine>>? engineFactory = null, HttpMessageHandler? playbackHandler = null,
        IExternalPlayerValidator? externalPlayerValidator = null)
    {
        this.scheduler = scheduler; this.messenger = messenger;
        clock ??= TimeProvider.System;
        Log = new(paths);
        Accounts = new();
        Settings = new(paths, scheduler, externalPlayerValidator);
        Requests = new(clock);
        Api = new(Settings.Current.DeviceId, apiHandler);
        QueryCache = new(scheduler, new(paths, clock), clock);
        ImageCache = new(paths, clock);
        Images = new(Accounts, ImageCache, Settings.Current.DeviceId, clock, imageHandler);
        Outbox = new(paths, Api, clock, error => Log.Error("停止记录死信", error));
        if (engineFactory is null) Playback = new DeferredPlaybackService(Accounts, scheduler, messenger, clock);
        else
        {
            playbackClient = playbackHandler is null ? StreamUrlResolver.CreateClient()
                : new HttpClient(playbackHandler) { Timeout = Timeout.InfiniteTimeSpan };
            var preparer = new EntryPreparer(Api, new StreamUrlResolver(playbackClient), Settings.Current.DeviceId, paths, Requests);
            Playback = new PlaybackCoordinator(Accounts, preparer, engineFactory, Api, Outbox, Settings, scheduler, messenger, clock,
                error => Log.Error("播放会话", error));
        }
        Session = new(Api, secrets, Accounts, scheduler, messenger, Playback, clock,
            flush: (account, token) => Outbox.FlushAsync(account, TimeSpan.FromSeconds(3), token),
            clear: async scope => { await QueryCache.ClearAsync(scope).ConfigureAwait(false); await ImageCache.ClearAsync().ConfigureAwait(false); });
        Images.AuthenticationExpired += Session.NotifyAuthenticationExpired;
        Library = new(Accounts, Api, Requests, QueryCache, scheduler, messenger);
        Preferences = new(Settings, Accounts, scheduler);
        Outbox.StartRetry(() => Accounts.Current);
        Outbox.Delivered += StopDelivered;
        Settings.CacheClearer = ClearCachesAsync;
        Playback.SessionStarted += ObservePlayback;
        Playback.SessionEnded += StopObservingPlayback;
    }
    public AccountContext Accounts { get; }
    public SettingsStore Settings { get; }
    public RequestScheduler Requests { get; }
    public EmbyApi Api { get; }
    public QueryCache QueryCache { get; }
    public ImageByteCache ImageCache { get; }
    public ImageFetcher Images { get; }
    private readonly HttpClient? playbackClient;
    public IPlaybackService Playback { get; }
    public StopOutbox Outbox { get; }
    public SessionManager Session { get; }
    public LibraryService Library { get; }
    public LibraryPreferences Preferences { get; }
    public AppLog Log { get; }
    private void StopDelivered(AccountSession account, StopReportRecord record) => scheduler.TryEnqueue(() =>
    {
        if (disposed || !ReferenceEquals(Accounts.Current, account)) return;
        // 旧离线停止记录没有剧/季标题数据，保守刷新当前账号的连播观察。
        QueryCache.Invalidate(key => key.Scope == account.Scope && key.Kind is "nextup" or "episodes");
        messenger.Send(new PlaybackStopped(record.ItemId));
    });
    private async Task ClearCachesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Accounts.Current is { } account) await QueryCache.ResetAsync(account.Scope, cancellationToken).ConfigureAwait(false);
            await ImageCache.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppException error) { Log.Error("清理缓存", error.Error); throw; }
    }
    private void ObservePlayback(object? sender, PlaybackSessionEventArgs args)
    {
        if (observedPlayback is not null) observedPlayback.SnapshotChanged -= PlaybackUpdated;
        observedPlayback = args.Session;
        lastVolume = Settings.Current.Volume;
        observedPlayback.SnapshotChanged += PlaybackUpdated;
        _ = SetInitialVolumeAsync(args.Session);
    }
    private async Task SetInitialVolumeAsync(IPlaybackSession session)
    {
        try { await session.SetVolumeAsync(lastVolume).ConfigureAwait(false); }
        catch (AppException) { /* P1 未接入真实引擎的准备会话不支持音量命令。 */ }
    }
    private void PlaybackUpdated(object? sender, EventArgs args)
    {
        if (disposed || sender != observedPlayback || observedPlayback is null) return;
        if (observedPlayback.Snapshot.Phase is PlayerPhase.Closed or PlayerPhase.Failed) return;
        var volume = observedPlayback.Snapshot.Volume;
        if (volume == lastVolume) return;
        lastVolume = volume;
        lock (volumeGate) volumeWrite = SaveVolumeAsync(volumeWrite, volume);
    }
    private async Task SaveVolumeAsync(Task previous, double volume)
    {
        try { await previous.ConfigureAwait(false); await Settings.UpdateAsync(value => value with { Volume = volume }).ConfigureAwait(false); }
        catch (AppException error) { Log.Error("保存音量", error.Error); }
    }
    private void StopObservingPlayback(object? sender, PlaybackSessionEventArgs args)
    {
        if (observedPlayback != args.Session) return;
        observedPlayback.SnapshotChanged -= PlaybackUpdated; observedPlayback = null;
    }
    public Task CloseAsync() => closing ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        if (Playback.Current is { } active) await active.CloseAsync(PlaybackEndReason.AppShutdown).ConfigureAwait(false);
        try { await Outbox.FlushAsync(Accounts.Current, TimeSpan.FromSeconds(1.5), budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (AppException error) { Log.Error("退出停止上报", error.Error); }
        try { await QueryCache.FlushAsync().ConfigureAwait(false); }
        catch (AppException error) { Log.Error("保存查询快照", error.Error); }
        Task pendingVolume;
        lock (volumeGate) pendingVolume = volumeWrite;
        await pendingVolume.ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Settings.CacheClearer = null;
        Playback.SessionStarted -= ObservePlayback; Playback.SessionEnded -= StopObservingPlayback;
        Outbox.Delivered -= StopDelivered;
        if (observedPlayback is not null) observedPlayback.SnapshotChanged -= PlaybackUpdated;
        Images.AuthenticationExpired -= Session.NotifyAuthenticationExpired;
        Preferences.Dispose(); Library.Dispose(); Session.Dispose(); (Playback as IDisposable)?.Dispose();
        Images.Dispose(); ImageCache.Dispose(); QueryCache.Dispose(); Outbox.Dispose(); Requests.Dispose();
        playbackClient?.Dispose(); Api.Dispose(); Accounts.Dispose(); Settings.Dispose(); Log.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync().ConfigureAwait(false); }
        finally
        {
            try
            {
                await Images.DisposeAsync().ConfigureAwait(false);
                await Outbox.DisposeAsync().ConfigureAwait(false);
                await QueryCache.DisposeAsync().ConfigureAwait(false);
                await ImageCache.DisposeAsync().ConfigureAwait(false);
            }
            finally { Dispose(); }
        }
    }
}
