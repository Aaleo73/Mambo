using System.Collections.Immutable;

namespace Mambo.Core.Contracts;

public enum EngineKind
{
    Embedded,
    External,
    Demo,
}

public enum PlayerPhase
{
    Preparing,
    Opening,
    Playing,
    Interstitial,
    Failed,
    Closing,
    Closed,
}

public enum TrackKind
{
    Audio,
    Subtitle,
}

public enum FrameStepDirection
{
    Backward,
    Forward,
}

/// <summary>界面使用的连播数据，不包含片源 URL、认证头或服务器播放会话令牌。</summary>
public sealed record PlaybackEntry(string ItemId, string Title)
{
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public string? EpisodeName { get; init; }
    public UserDataState UserData { get; init; } = new();
    public ImageRef? Image { get; init; }
    public string? SeasonId { get; init; }
    public string? EpisodeLabel { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public long? DurationTicks { get; init; }
    /// <summary>该集或该片的首播年份，没有时为 null。</summary>
    public int? ProductionYear { get; init; }
}

/// <summary>Id 是不透明的命令标识；Label 最长 96 字符，不包含外部文件名。</summary>
public sealed record TrackInfo(string Id, TrackKind Kind, string Label)
{
    public string? Language { get; init; }
    public bool IsDefault { get; init; }
}

public sealed record BufferedRange(long StartTicks, long EndTicks);

/// <summary>不可变的界面状态；音轨与字幕菜单各最多包含 32 项。</summary>
public sealed record SessionSnapshot
{
    public PlayerPhase Phase { get; init; } = PlayerPhase.Preparing;
    public EngineKind EngineKind { get; init; } = EngineKind.Embedded;
    public PlaybackEntry? Entry { get; init; }
    public ImmutableArray<PlaybackEntry> Entries { get; init; } = [];
    public int CurrentEntryIndex { get; init; } = -1;
    public long PositionTicks { get; init; }
    public long DurationTicks { get; init; }
    public bool IsPaused { get; init; }
    public bool IsBuffering { get; init; }
    public bool IsSeeking { get; init; }
    public bool IsSlowOpening { get; init; }
    public double Volume { get; init; } = 100;
    public bool IsMuted { get; init; }
    public double PlaybackRate { get; init; } = 1;
    /// <summary>最近一次成功应用的画质模式；切换过程中保持旧值。</summary>
    public VideoQualityMode VideoQualityMode { get; init; } = VideoQualityMode.Standard;
    public bool IsVideoQualityChanging { get; init; }
    /// <summary>自动恢复画质失败的非致命提示；不影响播放阶段。手动命令失败通过 AppException 返回。</summary>
    public AppError? VideoQualityError { get; init; }
    public ImmutableArray<TrackInfo> AudioTracks { get; init; } = [];
    public ImmutableArray<TrackInfo> SubtitleTracks { get; init; } = [];
    public string? SelectedAudioTrackId { get; init; }
    public string? SelectedSubtitleTrackId { get; init; }
    public ImmutableArray<BufferedRange> BufferedRanges { get; init; } = [];
    public AppError? Error { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    /// <summary>Demo 会话纯色画面的 ARGB 值，不是原生对象句柄。</summary>
    public uint? DemoColorArgb { get; init; }

    public bool CanPrevious => CurrentEntryIndex > 0;
    public bool CanNext => CurrentEntryIndex >= 0 && CurrentEntryIndex + 1 < Entries.Length;
}

/// <summary>仅当前端的替换确认通过后才设置 ReplaceCurrent。</summary>
public sealed record PlayRequest(string ItemId, long? StartTicks = null, bool ReplaceCurrent = false);
