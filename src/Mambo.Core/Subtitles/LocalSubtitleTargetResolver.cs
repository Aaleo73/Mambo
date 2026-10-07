using System.Collections.Immutable;
using System.Globalization;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Playback;
using Mambo.Core.Session;

namespace Mambo.Core.Subtitles;

/// <summary>只接受真实元数据中的唯一目标，不把当前播放队列当作完整片库。</summary>
public sealed class LocalSubtitleTargetResolver(EmbyApi api, RequestScheduler? scheduler = null) : ILocalSubtitleTargetResolver
{
    public async Task<ImmutableDictionary<int, string>> ResolveAsync(AccountSession account, PlaybackEntry context,
        ImmutableArray<SubtitleImportCandidate> candidates, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, account.Token);
        var cancellation = linked.Token;
        var result = ImmutableDictionary.CreateBuilder<int, string>();
        var series = new Dictionary<string, string?>(StringComparer.Ordinal);
        var episodes = new Dictionary<string, EmbyItem[]?>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            if (candidate.InputIndex < 0 || candidate.BatchSize < 1 || candidate.InputIndex >= candidate.BatchSize) continue;
            try
            {
                var parsed = SubtitleFilenameParser.Parse(candidate.FileName);
                if (parsed.IsAmbiguous) continue;
                var prefix = SubtitleFilenameParser.NormalizeTitle(parsed.SeriesName);
                if (parsed.IsUnnumbered)
                {
                    // 未知文件名不是已确认的其它片名；单文件拖入本身表示用于当前项目。
                    if (candidate.BatchSize == 1 && ValidId(context.ItemId))
                        result[candidate.InputIndex] = context.ItemId;
                    continue;
                }

                var currentSeries = ValidId(context.SeriesId) && (prefix.Length == 0 || Matches(prefix, context.SeriesName));
                string? seriesId = currentSeries ? context.SeriesId : null;
                if (!currentSeries && prefix.Length > 0)
                {
                    if (!series.TryGetValue(prefix, out seriesId))
                    {
                        series[prefix] = null;
                        seriesId = await FindSeriesAsync(account, parsed.SeriesName, prefix, cancellation).ConfigureAwait(false);
                        series[prefix] = seriesId;
                    }
                }
                if (!ValidId(seriesId)) continue;
                var sameSeries = seriesId == context.SeriesId;
                var seasonNumber = parsed.SeasonNumber ?? (sameSeries ? context.SeasonNumber : null);
                var knownSeason = sameSeries && ValidId(context.SeasonId) &&
                    (parsed.SeasonNumber is null || parsed.SeasonNumber == context.SeasonNumber);
                if (!knownSeason && seasonNumber is null) continue;
                var parentId = knownSeason ? context.SeasonId! : seriesId!;
                if (!episodes.TryGetValue(parentId, out var rows))
                {
                    episodes[parentId] = null;
                    rows = await new EmbyEpisodeReader(api, scheduler).ReadAllAsync(account, parentId,
                        PlaybackTargetResolver.EpisodeFields, cancellation).ConfigureAwait(false);
                    episodes[parentId] = rows;
                }
                if (rows is null) continue;
                var matches = rows.Where(item => item is not null && ValidId(item.Id) &&
                    item.Type?.Equals("Episode", StringComparison.OrdinalIgnoreCase) == true &&
                    (string.IsNullOrWhiteSpace(item.SeriesId) || item.SeriesId == seriesId) &&
                    item.IndexNumber == parsed.EpisodeNumber &&
                    (knownSeason ? (string.IsNullOrWhiteSpace(item.SeasonId) || item.SeasonId == context.SeasonId) &&
                        (seasonNumber is null || item.ParentIndexNumber is null || item.ParentIndexNumber == seasonNumber)
                        : item.ParentIndexNumber == seasonNumber))
                    .Select(item => item.Id!).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                if (matches.Length == 1) result[candidate.InputIndex] = matches[0];
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is AppException or HttpRequestException or IOException or ArgumentException or InvalidOperationException)
            {
                // 此文件无法确认归属，不能退回“当前集”。其它已确认文件不受影响。
            }
        }
        return result.ToImmutable();
    }

    private async Task<string?> FindSeriesAsync(AccountSession account, string query, string exactName, CancellationToken token)
    {
        const int pageSize = 100;
        var offset = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matching = new HashSet<string>(StringComparer.Ordinal);
        var prefix = "Users/" + EmbyApi.Escape(account.Secret.UserId) + "/Items?Recursive=true&IncludeItemTypes=Series&SearchTerm=" +
            EmbyApi.Escape(query) + "&Limit=" + pageSize.ToString(CultureInfo.InvariantCulture) + "&StartIndex=";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var path = prefix + offset.ToString(CultureInfo.InvariantCulture);
            var page = await (scheduler is null ? api.ItemsAsync(account, path, token) :
                scheduler.RunAsync(ct => api.ItemsAsync(account, path, ct), RequestPriority.Background, scopeToken: token)).ConfigureAwait(false);
            var rows = page.Items ?? [];
            if (rows.Length > pageSize || page.TotalRecordCount < 0 || offset > int.MaxValue - rows.Length) return null;
            if (rows.Length == 0)
                return page.TotalRecordCount is { } count && offset < count ? null : matching.SingleOrDefault();
            var progressed = false;
            foreach (var item in rows)
            {
                if (item is null || !ValidId(item.Id)) continue;
                if (seen.Add(item.Id!)) progressed = true;
                if (item.Type?.Equals("Series", StringComparison.OrdinalIgnoreCase) == true && Matches(exactName, item.Name))
                    matching.Add(item.Id!);
            }
            if (matching.Count > 1 || !progressed) return null;
            offset += rows.Length;
            if (page.TotalRecordCount is { } total && offset >= total) return matching.SingleOrDefault();
        }
    }

    private static bool Matches(string normalized, string? text) => !string.IsNullOrWhiteSpace(text) &&
        normalized.Equals(SubtitleFilenameParser.NormalizeTitle(text), StringComparison.Ordinal);

    internal static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 &&
        id is not "." and not ".." && !id.Any(char.IsControl);
}
