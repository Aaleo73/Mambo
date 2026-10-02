using System.Collections.Immutable;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

public static class StreamCandidateBuilder
{
    public static ImmutableArray<StreamCandidate> Build(AccountSession account, string itemId, Guid deviceId, EmbyPlaybackInfo info)
    {
        if (EmbyMapper.Identity(itemId) is null) throw PlaybackTargetResolver.Invalid();
        var candidates = new List<(int Category, int SourceRank, StreamCandidate Candidate)>();
        var sources = MediaSourceSelector.Select(info.MediaSources);
        for (var rank = 0; rank < sources.Length; rank++)
        {
            var source = sources[rank];
            var headers = StreamUrlResolver.SanitizeHeaders(source.RequiredHttpHeaders);
            Add(source.DirectStreamUrl, "DirectStream", 1);
            if (MediaSourceSelector.IsHttpPath(source.Path)) Add(source.Path, "DirectPlay", 2);
            if ((source.SupportsDirectPlay == true || source.SupportsDirectStream == true) && EmbyMapper.Identity(source.Id) is { } sourceId)
            {
                var path = "Videos/" + EmbyApi.Escape(itemId) + "/stream";
                var query = "?DeviceId=" + deviceId.ToString("D") + "&MediaSourceId=" + EmbyApi.Escape(sourceId) + "&Static=true";
                if (!string.IsNullOrWhiteSpace(info.PlaySessionId)) query += "&PlaySessionId=" + EmbyApi.Escape(info.PlaySessionId);
                if (SafeFormat(source.Container) is { } container)
                    Add(account.Address.Endpoint(path + "." + container + query).AbsoluteUri, "DirectPlay", 3);
                Add(account.Address.Endpoint(path + query).AbsoluteUri, "DirectPlay", 4);
            }
            // PlaybackInfo 明确拒绝两种直连方式时，返回的转码地址具有最高类别优先级。
            // 类别仍先于源排名，故此类转码也会先于其它媒体源的直连候选尝试。
            Add(source.TranscodingUrl, "Transcode", source.SupportsDirectPlay == false && source.SupportsDirectStream == false ? 0 : 5);

            void Add(string? value, string method, int category)
            {
                if (ResolveAddress(account, value) is not { } address) return;
                if (method == "DirectStream" && !string.IsNullOrWhiteSpace(info.PlaySessionId) && !HasQuery(address, "PlaySessionId"))
                {
                    var builder = new UriBuilder(address);
                    builder.Query = builder.Query.TrimStart('?') + (builder.Query.Length > 1 ? "&" : "") + "PlaySessionId=" + EmbyApi.Escape(info.PlaySessionId);
                    address = builder.Uri;
                }
                if (StreamUrlResolver.SameServer(address, account.Address.Uri)) address = StreamUrlResolver.RemoveAuthenticationQuery(address);
                candidates.Add((category, rank, new(address, method, source, headers)));
            }
        }
        return candidates.OrderBy(value => value.Category).ThenBy(value => value.SourceRank)
            .DistinctBy(value => value.Candidate.Address.AbsoluteUri, StringComparer.Ordinal)
            .Select(value => value.Candidate).ToImmutableArray();
    }

    internal static Uri? ResolveAddress(AccountSession account, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(account.Address.Uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
            !Uri.TryCreate(root, value, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri;
    }

    internal static string? SafeFormat(string? format) => !string.IsNullOrWhiteSpace(format) && format.Length <= 20 && format.All(char.IsAsciiLetterOrDigit) ? format.ToLowerInvariant() : null;
    private static bool HasQuery(Uri address, string key) => address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Any(part => Uri.UnescapeDataString(part.Split('=')[0]).Equals(key, StringComparison.OrdinalIgnoreCase));
}
