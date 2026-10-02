using System.Collections.Immutable;

namespace Mambo.Core.Contracts;

public enum MediaKind
{
    Movie,
    Series,
    Season,
    Episode,
    Video,
}

public enum LibraryKind
{
    Movies,
    TvShows,
    Mixed,
}

public enum ImageKind
{
    Primary,
    Backdrop,
    Thumb,
    Logo,
}

/// <summary>当前账号作用域内的图片标识，不包含服务器 URL 或令牌。</summary>
public sealed record ImageRef(string ItemId, ImageKind Kind, string? Tag = null, int Index = 0);

public sealed record MediaLibrary(string Id, string Name, LibraryKind Kind);

public sealed record UserDataState(
    long PlaybackPositionTicks = 0,
    bool Played = false,
    int? PlayCount = null,
    DateTimeOffset? LastPlayedUtc = null);

public enum PersonKind
{
    Actor,
    Director,
    Writer,
    Producer,
    Crew,
}

public sealed record PersonInfo(string Id, string Name, PersonKind Kind)
{
    public string? Role { get; init; }
    public ImageRef? Image { get; init; }
}

/// <summary>卡片与详情使用的不可变元数据；播放地址与媒体源 DTO 仅保留在后端。</summary>
public sealed record MediaItem(string Id, string Name, MediaKind Kind)
{
    public string? LibraryId { get; init; }
    public string? SortName { get; init; }
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public string? SeasonId { get; init; }
    public int? ParentIndexNumber { get; init; }
    public int? IndexNumber { get; init; }
    public string? Overview { get; init; }
    public int? ProductionYear { get; init; }
    public DateTimeOffset? PremiereDate { get; init; }
    public double? CommunityRating { get; init; }
    public string? OfficialRating { get; init; }
    public long? RunTimeTicks { get; init; }
    public ImmutableArray<string> Genres { get; init; } = [];
    public ImmutableArray<ImageRef> Images { get; init; } = [];
    public ImmutableArray<PersonInfo> People { get; init; } = [];
    public UserDataState UserData { get; init; } = new();
}

public sealed record SeasonInfo(string Id, string SeriesId, string Name, int? IndexNumber = null)
{
    public ImageRef? Image { get; init; }
}
