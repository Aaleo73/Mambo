using System.Collections.Immutable;
using Mambo.Core.Contracts;

namespace Mambo.Core.BulletChat;

/// <summary>
/// 跟随当前播放条目自动匹配并加载弹幕。播放与设置的通知经 IUiScheduler 到达；
/// 加载在后台执行，结果按代次校验后再发布，切集或关闭后迟到的结果被丢弃。
/// </summary>
public sealed class BulletChatService : IBulletChatService, IDisposable
{
    private readonly IPlaybackService playback;
    private readonly ISettingsService settings;
    private readonly IBulletChatProvider provider;
    private readonly IUiScheduler scheduler;
    private readonly Func<EngineKind, bool> eligible;
    private readonly Action<AppError>? log;
    private readonly object gate = new();
    private IPlaybackSession? session;
    private CancellationTokenSource? loading;
    private PlaybackEntry? entry;
    private string? entryKey;
    private long generation;
    private BulletChatState current = new();
    private bool disposed;

    /// <param name="eligible">哪些引擎显示弹幕；真实服务只对内置引擎生效，外置 mpv 不介入。</param>
    public BulletChatService(IPlaybackService playback, ISettingsService settings, IBulletChatProvider provider, IUiScheduler scheduler,
        Func<EngineKind, bool> eligible, Action<AppError>? log = null)
    {
        this.playback = playback; this.settings = settings; this.provider = provider; this.scheduler = scheduler; this.eligible = eligible; this.log = log;
        playback.SessionStarted += OnSessionStarted;
        playback.SessionEnded += OnSessionEnded;
        settings.Changed += OnSettingsChanged;
        if (playback.Current is { } active) Observe(active);
    }

    public BulletChatState Current { get { lock (gate) return current; } }
    public event EventHandler? Changed;

    public Task<ImmutableArray<BulletChatAnime>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var text = keyword?.Trim() ?? "";
        if (text.Length is 0 or > 100) throw Invalid("请输入要搜索的片名。");
        return provider.SearchAsync(text, cancellationToken);
    }

    public Task<ImmutableArray<BulletChatEpisode>> GetEpisodesAsync(string animeId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(animeId) || animeId.Length > 64) throw Invalid("弹幕条目无效。");
        return provider.GetEpisodesAsync(animeId, cancellationToken);
    }

    public async Task SelectAsync(BulletChatEpisode episode, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(episode);
        PlaybackEntry target;
        long mine;
        CancellationTokenSource cancellation;
        lock (gate)
        {
            if (entry is null || entryKey is null) throw Invalid("当前没有可以匹配弹幕的播放条目。");
            target = entry;
            (mine, cancellation) = BeginLocked(target);
        }
        Publish();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
        try
        {
            var resolution = await provider.SelectAsync(target, episode, linked.Token).ConfigureAwait(false);
            Complete(mine, Loaded(target, resolution));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { /* 已切集或关闭。 */ }
        catch (AppException failure)
        {
            Complete(mine, new() { Status = BulletChatStatus.Failed, ItemId = target.ItemId, Error = failure.Error });
            throw;
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) { entry = null; entryKey = null; }
        Evaluate();
        return Task.CompletedTask;
    }

    private void OnSessionStarted(object? sender, PlaybackSessionEventArgs args) => Observe(args.Session);
    private void OnSessionEnded(object? sender, PlaybackSessionEventArgs args)
    {
        lock (gate)
        {
            if (!ReferenceEquals(session, args.Session)) return;
            session.SnapshotChanged -= OnSnapshotChanged;
            session = null;
        }
        Evaluate();
    }
    private void OnSnapshotChanged(object? sender, EventArgs args) => Evaluate();
    private void OnSettingsChanged(object? sender, EventArgs args) => Evaluate();

    private void Observe(IPlaybackSession next)
    {
        lock (gate)
        {
            if (disposed || ReferenceEquals(session, next)) return;
            if (session is not null) session.SnapshotChanged -= OnSnapshotChanged;
            session = next;
            next.SnapshotChanged += OnSnapshotChanged;
            // 换了会话：上一个会话的弹幕不能留到新会话的准备阶段。
            ResetLocked();
        }
        Publish();
        Evaluate();
    }

    private void Evaluate()
    {
        PlaybackEntry? wanted = null;
        lock (gate)
        {
            if (disposed) return;
            var snapshot = session?.Snapshot;
            // 准备阶段的条目可能还是占位数据，等它确定后再匹配；期间保持现状。
            if (snapshot is { Phase: PlayerPhase.Preparing }) return;
            if (snapshot is { Entry: { } playing, Phase: not (PlayerPhase.Closing or PlayerPhase.Closed) } &&
                settings.Current.BulletChat.Enabled && eligible(snapshot.EngineKind)) wanted = playing;
            if (wanted is null)
            {
                if (!ResetLocked()) return;
            }
            else
            {
                // 快照以约 10Hz 到达，条目对象通常不变：先比引用，省掉拼键。
                if (entryKey is not null && ReferenceEquals(wanted, entry)) return;
                var key = KeyOf(wanted);
                if (key == entryKey) { entry = wanted; return; }
                entry = wanted; entryKey = key;
                var (mine, cancellation) = BeginLocked(wanted);
                _ = LoadAsync(wanted, mine, cancellation.Token);
            }
        }
        Publish();
    }

    // 调用方持有 gate：回到 Idle 并让在途的加载作废；本来就是 Idle 时返回 false。
    private bool ResetLocked()
    {
        if (entryKey is null && current.Status == BulletChatStatus.Idle) return false;
        loading?.Cancel();
        loading = null;
        entry = null; entryKey = null;
        generation++;
        current = new();
        return true;
    }

    // 调用方持有 gate：取消上一轮加载并进入 Loading。
    private (long Generation, CancellationTokenSource Cancellation) BeginLocked(PlaybackEntry target)
    {
        loading?.Cancel();
        var cancellation = loading = new CancellationTokenSource();
        current = new() { Status = BulletChatStatus.Loading, ItemId = target.ItemId };
        return (++generation, cancellation);
    }

    private async Task LoadAsync(PlaybackEntry target, long mine, CancellationToken token)
    {
        try
        {
            // 匹配与解析不占用发起通知的界面线程。
            var resolution = await Task.Run(() => provider.ResolveAsync(target, token), token).ConfigureAwait(false);
            Complete(mine, Loaded(target, resolution));
        }
        catch (OperationCanceledException) { }
        catch (AppException failure)
        {
            log?.Invoke(failure.Error);
            Complete(mine, new() { Status = BulletChatStatus.Failed, ItemId = target.ItemId, Error = failure.Error });
        }
    }

    private static BulletChatState Loaded(PlaybackEntry target, BulletChatResolution resolution) => resolution.Episode is null
        ? new() { Status = BulletChatStatus.NotMatched, ItemId = target.ItemId }
        : new() { Status = BulletChatStatus.Loaded, ItemId = target.ItemId, Episode = resolution.Episode, Comments = resolution.Comments.IsDefault ? [] : resolution.Comments };

    private void Complete(long mine, BulletChatState state)
    {
        lock (gate)
        {
            if (disposed || mine != generation) return;
            current = state;
        }
        Publish();
    }

    private void Publish() => scheduler.TryEnqueue(() => { if (!disposed) Changed?.Invoke(this, EventArgs.Empty); });

    // 同一条目的元数据补全后也要重新匹配，所以键里包含参与匹配的全部字段。
    private static string KeyOf(PlaybackEntry value) => string.Join('\u001f', value.ItemId, value.SeriesId, value.SeriesName, value.Title,
        value.SeasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture), value.EpisodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        value.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static AppException Invalid(string text) => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, text, false));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            loading?.Cancel();
            loading = null;
            if (session is not null) session.SnapshotChanged -= OnSnapshotChanged;
            session = null;
        }
        playback.SessionStarted -= OnSessionStarted;
        playback.SessionEnded -= OnSessionEnded;
        settings.Changed -= OnSettingsChanged;
        Changed = null;
    }
}
