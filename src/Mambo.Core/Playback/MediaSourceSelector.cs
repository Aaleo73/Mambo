using System.Collections.Immutable;
using Mambo.Core.Networking;

namespace Mambo.Core.Playback;

public static class MediaSourceSelector
{
    public static ImmutableArray<EmbyMediaSource> Select(IEnumerable<EmbyMediaSource>? sources) =>
        (sources ?? []).Select((source, index) => (Source: source, Index: index))
            .Where(item => item.Source is not null && IsEligible(item.Source))
            .OrderBy(item => Priority(item.Source))
            .ThenByDescending(item => MaximumHeight(item.Source))
            .ThenByDescending(item => Math.Max(0, item.Source.Bitrate ?? 0))
            .ThenBy(item => item.Index).Select(item => item.Source).ToImmutableArray();

    public static bool IsEligible(EmbyMediaSource source)
    {
        var explicitUrl = !string.IsNullOrWhiteSpace(source.DirectStreamUrl) || !string.IsNullOrWhiteSpace(source.TranscodingUrl) || IsHttpPath(source.Path);
        if (!explicitUrl && (source.MediaStreams?.Any(stream => stream is not null && stream.Type?.Equals("Video", StringComparison.OrdinalIgnoreCase) == true) != true ||
            source.Container?.ToLowerInvariant() is "iso" or "dvd" or "bluray" or "bdmv")) return false;
        return explicitUrl || source.SupportsDirectPlay == true || source.SupportsDirectStream == true;
    }

    public static int Priority(EmbyMediaSource source) => !string.IsNullOrWhiteSpace(source.DirectStreamUrl) ? 0 :
        IsHttpPath(source.Path) ? 1 : source.SupportsDirectStream == true || source.SupportsDirectPlay == true ? 2 : 3;

    internal static bool IsHttpPath(string? path) => Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static int MaximumHeight(EmbyMediaSource source) => source.MediaStreams?.Where(stream => stream is not null &&
        stream.Type?.Equals("Video", StringComparison.OrdinalIgnoreCase) == true).Select(stream => Math.Max(0, stream.Height ?? 0)).DefaultIfEmpty().Max() ?? 0;
}
