using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mambo.Core.Networking;

public sealed record EmbyItems { public EmbyItem[]? Items { get; init; } public int? TotalRecordCount { get; init; } }
public sealed record EmbyItem
{
    public string? Id { get; init; } public string? Name { get; init; } public string? Type { get; init; }
    public string? CollectionType { get; init; } public string? SortName { get; init; } public string? Overview { get; init; }
    public string? SeriesId { get; init; } public string? SeriesName { get; init; } public string? SeasonId { get; init; }
    public int? ParentIndexNumber { get; init; } public int? IndexNumber { get; init; } public int? ProductionYear { get; init; }
    public DateTimeOffset? PremiereDate { get; init; } public DateTimeOffset? DateCreated { get; init; }
    public double? CommunityRating { get; init; } public string? OfficialRating { get; init; } public long? RunTimeTicks { get; init; }
    public string[]? Genres { get; init; } public Dictionary<string, string>? ImageTags { get; init; } public string[]? BackdropImageTags { get; init; }
    public string? PrimaryImageItemId { get; init; } public string? PrimaryImageTag { get; init; }
    public string? ParentBackdropItemId { get; init; } public string[]? ParentBackdropImageTags { get; init; }
    public string? ParentLogoItemId { get; init; } public string? ParentLogoImageTag { get; init; }
    public EmbyUserData? UserData { get; init; } public EmbyPerson[]? People { get; init; } public EmbyMediaSource[]? MediaSources { get; init; }
}
public sealed record EmbyUserData { public long? PlaybackPositionTicks { get; init; } public bool? Played { get; init; } public int? PlayCount { get; init; } public DateTimeOffset? LastPlayedDate { get; init; } }
public sealed record EmbyPerson { public string? Id { get; init; } public string? Name { get; init; } public string? Type { get; init; } public string? Role { get; init; } public string? PrimaryImageTag { get; init; } }
public sealed record EmbyMediaSource
{
    public string? Id { get; init; } public string? Path { get; init; } public string? Container { get; init; } public string? Protocol { get; init; }
    public bool? SupportsDirectPlay { get; init; } public bool? SupportsDirectStream { get; init; } public string? DirectStreamUrl { get; init; } public string? TranscodingUrl { get; init; }
    public long? Bitrate { get; init; }
    public string? LiveStreamId { get; init; } public long? RunTimeTicks { get; init; }
    public Dictionary<string, string>? RequiredHttpHeaders { get; init; } public EmbyStreamInfo[]? MediaStreams { get; init; }
    public override string ToString() => "EmbyMediaSource { <redacted> }";
}
public sealed record EmbyStreamInfo
{
    public int? Index { get; init; } public string? Type { get; init; } public string? Codec { get; init; } public string? Language { get; init; }
    public string? DisplayTitle { get; init; } public string? Title { get; init; }
    public int? Height { get; init; } public int? Width { get; init; }
    public bool? IsExternal { get; init; } public bool? IsDefault { get; init; } public string? DeliveryUrl { get; init; }
    public override string ToString() => "EmbyMediaStream { <redacted> }";
}
public sealed record EmbyAuthentication { public string? AccessToken { get; init; } public string? ServerId { get; init; } public EmbyItem? User { get; init; } public override string ToString() => "EmbyAuthentication { <redacted> }"; }
public sealed record AuthenticationRequest(string Username, string Pw) { public override string ToString() => "AuthenticationRequest { <redacted> }"; }
public sealed record EmbyPlaybackInfo { public string? PlaySessionId { get; init; } public EmbyMediaSource[]? MediaSources { get; init; } public override string ToString() => "EmbyPlaybackInfo { <redacted> }"; }
public sealed record PlaybackInfoRequest(string UserId, long StartTimeTicks, DeviceProfile DeviceProfile)
{
    public bool IsPlayback { get; init; } = true;
    public bool AutoOpenLiveStream { get; init; } = true;
    public int MaxStreamingBitrate { get; init; } = 140000000;
}
public sealed record DeviceProfile
{
    private static readonly string[] SubtitleFormats = ["srt", "ass", "ssa", "sub", "vtt", "pgssub", "dvdsub", "dvbsub", "mov_text", "text", "ttml"];
    public string Name { get; init; } = "Mambo";
    public DirectPlayProfile[] DirectPlayProfiles { get; init; } = [new()];
    public TranscodingProfile[] TranscodingProfiles { get; init; } = [new("hls"), new("http")];
    public SubtitleProfile[] SubtitleProfiles { get; init; } = SubtitleFormats.SelectMany(format => new[] { new SubtitleProfile(format, "External"), new SubtitleProfile(format, "Embed") }).ToArray();
}
public sealed record DirectPlayProfile { public string Type { get; init; } = "Video"; public string Container { get; init; } = "mp4,mkv,avi,mov,wmv,m4v,ts,m2ts,webm,flv,ogm,ogv,mpg,mpeg,3gp"; }
public sealed record TranscodingProfile(string Protocol)
{
    public string Type { get; init; } = "Video"; public string Container { get; init; } = "ts";
    public string VideoCodec { get; init; } = "h264"; public string AudioCodec { get; init; } = "aac,mp3,ac3,eac3,flac,opus,vorbis";
    public string MaxAudioChannels { get; init; } = "6"; public string MinSegments { get; init; } = "1";
    public bool BreakOnNonKeyFrames { get; init; } = true; public string Context { get; init; } = "Streaming";
}
public sealed record SubtitleProfile(string Format, string Method);
public sealed record PlaybackReport
{
    public string? ItemId { get; init; } public string? MediaSourceId { get; init; } public string? PlaySessionId { get; init; }
    public string? LiveStreamId { get; init; } public long PositionTicks { get; init; } public long PlaybackStartTimeTicks { get; init; }
    public string PlayMethod { get; init; } = "DirectPlay"; public bool CanSeek { get; init; } = true;
    public bool IsPaused { get; init; } public bool IsMuted { get; init; } public double VolumeLevel { get; init; } = 100;
    public bool Failed { get; init; }
    public double PlaybackRate { get; init; } = 1; public string? EventName { get; init; }
    public int PlaylistIndex { get; init; } public int PlaylistLength { get; init; } = 1;
    public PlaybackQueueItem[] NowPlayingQueue { get; init; } = [];
    public int MaxStreamingBitrate { get; init; } = int.MaxValue;
    public string RepeatMode { get; init; } = "RepeatNone";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SubtitleOffset { get; init; }
    public bool Shuffle { get; init; }
    public int? AudioStreamIndex { get; init; } public int? SubtitleStreamIndex { get; init; }
    public override string ToString() => "PlaybackReport { <redacted> }";
}
public sealed record PlaybackQueueItem(string Id, string? PlaylistItemId = null);
public sealed record EmbyFilters { public NameOrString[]? Genres { get; init; } public int[]? Years { get; init; } public NameOrString[]? OfficialRatings { get; init; } }
[JsonConverter(typeof(NameOrStringConverter))]
public sealed record NameOrString(string Name);
public sealed class NameOrStringConverter : JsonConverter<NameOrString>
{
    public override NameOrString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new(reader.GetString() ?? "");
        using var item = JsonDocument.ParseValue(ref reader);
        foreach (var property in item.RootElement.EnumerateObject()) if (property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)) return new(property.Value.GetString() ?? "");
        return new("");
    }
    public override void Write(Utf8JsonWriter writer, NameOrString value, JsonSerializerOptions options) => writer.WriteStringValue(value.Name);
}
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(EmbyItems))] [JsonSerializable(typeof(EmbyItem))] [JsonSerializable(typeof(EmbyItem[]))]
[JsonSerializable(typeof(EmbyFilters))] [JsonSerializable(typeof(EmbyAuthentication))] [JsonSerializable(typeof(AuthenticationRequest))]
[JsonSerializable(typeof(EmbyPlaybackInfo))] [JsonSerializable(typeof(PlaybackInfoRequest))] [JsonSerializable(typeof(PlaybackReport))]
public sealed partial class EmbyJsonContext : JsonSerializerContext;
