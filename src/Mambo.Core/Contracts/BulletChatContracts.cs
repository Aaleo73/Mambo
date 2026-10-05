using System.Collections.Immutable;

namespace Mambo.Core.Contracts;

public enum BulletChatMode
{
    Scroll,
    Top,
    Bottom,
}

public enum BulletChatStatus
{
    /// <summary>已关闭、没有播放，或当前引擎不显示弹幕。</summary>
    Idle,
    Loading,
    Loaded,
    NotMatched,
    Failed,
}

/// <summary>一条弹幕；Rgb 为 0xRRGGBB，Text 不含换行。</summary>
public readonly record struct BulletChatComment(double TimeSeconds, BulletChatMode Mode, uint Rgb, string Text);

/// <summary>弹幕库里的一部作品；每一季通常是独立条目。</summary>
public sealed record BulletChatAnime(string Id, string Title)
{
    public string? TypeLabel { get; init; }
    public int? Year { get; init; }
    public int EpisodeCount { get; init; }
}

public sealed record BulletChatEpisode(string Id, string AnimeId, string AnimeTitle, string Title);

/// <summary>当前播放条目的弹幕；Comments 按时间升序，永不为 default。</summary>
public sealed record BulletChatState
{
    public BulletChatStatus Status { get; init; }
    public string? ItemId { get; init; }
    public BulletChatEpisode? Episode { get; init; }
    public ImmutableArray<BulletChatComment> Comments { get; init; } = [];
    public AppError? Error { get; init; }
}

/// <summary>
/// 自动跟随 IPlaybackService 的当前条目：切集即自动匹配并加载，无需调用方触发。
/// Changed 经 IUiScheduler 异步到达；查询与命令失败抛 AppException。
/// 弹幕关闭、外置播放或没有播放时保持 Idle，且不发出任何网络请求。
/// </summary>
public interface IBulletChatService
{
    BulletChatState Current { get; }
    event EventHandler? Changed;
    Task<ImmutableArray<BulletChatAnime>> SearchAsync(string keyword, CancellationToken cancellationToken = default);
    Task<ImmutableArray<BulletChatEpisode>> GetEpisodesAsync(string animeId, CancellationToken cancellationToken = default);
    /// <summary>为当前条目手动指定剧集；结果会被记住，同一季后续各集自动沿用。</summary>
    Task SelectAsync(BulletChatEpisode episode, CancellationToken cancellationToken = default);
    /// <summary>对当前条目重新执行自动匹配与加载。</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
