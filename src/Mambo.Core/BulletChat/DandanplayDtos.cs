using System.Text.Json.Serialization;

namespace Mambo.Core.BulletChat;

// 弹弹play 兼容协议的响应；只声明用到的字段。
public sealed record DandanSearchResponse { public List<DandanAnime>? Animes { get; init; } }

public sealed record DandanAnime
{
    public long AnimeId { get; init; }
    public string? BangumiId { get; init; }
    public string? AnimeTitle { get; init; }
    public string? Type { get; init; }
    public string? TypeDescription { get; init; }
    /// <summary>形如 2023-09-29T00:00:00，不带时区；只取年份。</summary>
    public string? StartDate { get; init; }
    public int EpisodeCount { get; init; }
    [JsonIgnore] public string Id => string.IsNullOrWhiteSpace(BangumiId) ? AnimeId.ToString(System.Globalization.CultureInfo.InvariantCulture) : BangumiId;
    [JsonIgnore] public int? Year => StartDate is { Length: >= 4 } text && int.TryParse(text.AsSpan(0, 4), System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2200 ? year : null;
}

public sealed record DandanBangumiResponse { public DandanBangumi? Bangumi { get; init; } }

public sealed record DandanBangumi
{
    public long AnimeId { get; init; }
    public string? AnimeTitle { get; init; }
    public string? Type { get; init; }
    public List<DandanEpisode>? Episodes { get; init; }
}

public sealed record DandanEpisode
{
    public long EpisodeId { get; init; }
    public string? EpisodeTitle { get; init; }
    /// <summary>正片为数字；特别篇、片头片尾为 S1、C1 这类带字母的编号。</summary>
    public string? EpisodeNumber { get; init; }
}

public sealed record DandanCommentResponse { public List<DandanComment>? Comments { get; init; } }

public sealed record DandanComment
{
    /// <summary>"时间秒,模式,十进制颜色,发送者"。</summary>
    public string? P { get; init; }
    public string? M { get; init; }
}

public sealed record DandanMatchRequest(string FileName, string FileHash, string MatchMode);

public sealed record DandanMatchResponse { public List<DandanMatch>? Matches { get; init; } }

public sealed record DandanMatch
{
    public long EpisodeId { get; init; }
    public long AnimeId { get; init; }
    public string? AnimeTitle { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Type { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(DandanSearchResponse))] [JsonSerializable(typeof(DandanBangumiResponse))]
[JsonSerializable(typeof(DandanCommentResponse))] [JsonSerializable(typeof(DandanMatchRequest))] [JsonSerializable(typeof(DandanMatchResponse))]
public sealed partial class DandanJsonContext : JsonSerializerContext;
