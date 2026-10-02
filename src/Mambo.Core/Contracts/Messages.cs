namespace Mambo.Core.Contracts;

/// <summary>在 UI 调度器上通过 IMessenger 发送，让依赖播放进度的读取失效。</summary>
public sealed record PlaybackStopped(string ItemId, string? SeriesId = null, string? SeasonId = null);

public sealed record SessionChanged(SessionState State, SessionInfo? Session);

public sealed record SessionExpired;
