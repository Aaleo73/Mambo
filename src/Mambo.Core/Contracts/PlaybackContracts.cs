namespace Mambo.Core.Contracts;

/// <summary>命令由后端串行处理，全部通知经 IUiScheduler 触发；命令失败抛出 AppException。</summary>
public interface IPlaybackSession
{
    SessionSnapshot Snapshot { get; }
    event EventHandler? SnapshotChanged;
    Task TogglePauseAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task SetRateAsync(double rate, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default);
    Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default);
    Task PreviousAsync(CancellationToken cancellationToken = default);
    Task NextAsync(CancellationToken cancellationToken = default);
    Task SelectEntryAsync(string itemId, CancellationToken cancellationToken = default);
    Task StepFrameAsync(FrameStepDirection direction, CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
    /// <summary>幂等地停止播放并完成清理；关闭应用时由拥有者调用。</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(PlaybackEndReason reason, CancellationToken cancellationToken = default);
}

public enum PlaybackEndReason { UserClosed, Replaced, SeasonEnded, Failed, Logout, AppShutdown }

public sealed class PlaybackSessionEventArgs(IPlaybackSession session, PlaybackEndReason? endReason = null) : EventArgs
{
    public IPlaybackSession Session { get; } = session;
    public PlaybackEndReason? EndReason { get; } = endReason;
}

public sealed class PlaybackEntrySkippedEventArgs(PlaybackEntry entry, AppError error) : EventArgs
{
    public PlaybackEntry Entry { get; } = entry;
    public AppError Error { get; } = error;
}

/// <summary>
/// 通知总是经 IUiScheduler 异步到达。请求拒绝（参数、认证、busy、替换未确认）抛 AppException；
/// 准备、解析和打开失败通过返回会话的 Failed/Error 呈现，可 RetryAsync。主动取消不留下新会话。
/// 同一项目返回当前会话并忽略 StartTicks。替换顺序为旧 SessionEnded(Replaced) → 新 SessionStarted；
/// 期间 IsStarting=true，Current 可短暂为 null。PlaybackStopped 仅在已确认开播并产生停止上报时发送。
/// </summary>
public interface IPlaybackService
{
    IPlaybackSession? Current { get; }
    bool IsStarting { get; }
    event EventHandler? Changed;
    event EventHandler<PlaybackSessionEventArgs>? SessionStarted;
    event EventHandler<PlaybackSessionEventArgs>? SessionEnded;
    event EventHandler<PlaybackEntrySkippedEventArgs>? EntrySkipped;
    Task<IPlaybackSession> PlayAsync(PlayRequest request, CancellationToken cancellationToken = default);
    /// <summary>无需登录即可启动演示；默认不替换现有播放，必须经用户确认后传 replaceCurrent=true。</summary>
    Task<IPlaybackSession> PreviewAsync(CancellationToken cancellationToken = default);
    Task<IPlaybackSession> PreviewAsync(bool replaceCurrent, CancellationToken cancellationToken = default);
}
