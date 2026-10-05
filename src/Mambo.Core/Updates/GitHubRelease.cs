using System.Text.Json.Serialization;

namespace Mambo.Core.Updates;

internal sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; init; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; init; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
    [JsonPropertyName("assets")] public GitHubAsset[] Assets { get; init; } = [];
}

internal sealed record GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("digest")] public string Digest { get; init; } = "";
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
