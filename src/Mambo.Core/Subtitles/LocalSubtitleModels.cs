using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;
using Mambo.Core.Session;

namespace Mambo.Core.Subtitles;

/// <summary>仅后端使用的受管字幕；实际路径不得进入 UI、日志或播放上报。</summary>
public sealed record LocalSubtitleFileInfo(string Id, string DisplayName, string Format, string ManagedPath)
{
    public override string ToString() => "LocalSubtitleFileInfo { <redacted> }";
}

public sealed record LocalSubtitleImportEntry(string ItemId, LocalSubtitleFileInfo File);
public sealed record LocalSubtitleImportResult(ImmutableArray<LocalSubtitleImportEntry> Items)
{
    public static LocalSubtitleImportResult Empty { get; } = new([]);
}

public sealed record LocalSubtitleItem(ImmutableArray<LocalSubtitleFileInfo> Files, string? AdoptedSubtitleId)
{
    public static LocalSubtitleItem Empty { get; } = new([], null);
}

/// <summary>BatchSize 是未筛选的原始批次长度，不能由可识别文件数量推导。</summary>
public sealed record SubtitleImportCandidate(int InputIndex, string FileName, int BatchSize)
{
    public override string ToString() => "SubtitleImportCandidate { <redacted> }";
}

public interface ILocalSubtitleTargetResolver
{
    Task<ImmutableDictionary<int, string>> ResolveAsync(AccountSession account, PlaybackEntry context,
        ImmutableArray<SubtitleImportCandidate> candidates, CancellationToken token);
}

public sealed record ParsedSubtitleFilename(string SeriesName, int? SeasonNumber, int? EpisodeNumber, bool IsAmbiguous)
{
    public bool IsUnnumbered => EpisodeNumber is null && !IsAmbiguous;
    public override string ToString() => "ParsedSubtitleFilename { <redacted> }";
}

internal sealed record LocalSubtitleStoredFile
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Format { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string RelativePath { get; init; } = "";
}

internal sealed record LocalSubtitleBinding
{
    public string[] SubtitleIds { get; init; } = [];
    public string? AdoptedSubtitleId { get; init; }
}

internal sealed record LocalSubtitleIndexDocument
{
    [JsonRequired]
    public int Version { get; init; } = 1;
    [JsonRequired]
    public Dictionary<string, LocalSubtitleStoredFile> Files { get; init; } = new(StringComparer.Ordinal);
    [JsonRequired]
    public Dictionary<string, LocalSubtitleBinding> Items { get; init; } = new(StringComparer.Ordinal);
}
