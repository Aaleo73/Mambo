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
}

public sealed class PlaybackSessionEventArgs(IPlaybackSession session) : EventArgs
{
    public IPlaybackSession Session { get; } = session;
}

public sealed class PlaybackEntrySkippedEventArgs(PlaybackEntry entry, AppError error) : EventArgs
{
    public PlaybackEntry Entry { get; } = entry;
    public AppError Error { get; } = error;
}

/// <summary>全部通知经 IUiScheduler 触发；失败抛出 AppException，主动取消抛出 OperationCanceledException。</summary>
public interface IPlaybackService
{
    IPlaybackSession? Current { get; }
    bool IsStarting { get; }
    event EventHandler? Changed;
    event EventHandler<PlaybackSessionEventArgs>? SessionStarted;
    event EventHandler<PlaybackSessionEventArgs>? SessionEnded;
    event EventHandler<PlaybackEntrySkippedEventArgs>? EntrySkipped;
    Task<IPlaybackSession> PlayAsync(PlayRequest request, CancellationToken cancellationToken = default);
    /// <summary>无需登录即可启动演示，供设置页预览播放页使用。</summary>
    Task<IPlaybackSession> PreviewAsync(CancellationToken cancellationToken = default);
}
