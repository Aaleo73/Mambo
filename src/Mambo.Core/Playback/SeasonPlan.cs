using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

public sealed class SeasonPlan(PlaybackTargetResolver resolver)
{
    public async Task<PreparedPlan> BuildAsync(AccountSession account, EmbyItem selected, long startTicks, CancellationToken cancellationToken)
    {
        var single = new PreparedPlan([ToEntry(selected)], 0, startTicks);
        if (!PlaybackTargetResolver.IsEpisode(selected) || EmbyMapper.Identity(selected.SeasonId) is not { } seasonId) return single;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Token);
            var items = await resolver.EpisodesAsync(account, seasonId, linked.Token).ConfigureAwait(false);
            var episodes = items.Where(item => PlaybackTargetResolver.IsEpisode(item) &&
                (string.IsNullOrWhiteSpace(item.SeasonId) || item.SeasonId == seasonId))
                .OrderBy(item => item.ParentIndexNumber ?? int.MaxValue).ThenBy(item => item.IndexNumber ?? int.MaxValue)
                .ThenBy(item => item.SortName ?? item.Name, StringComparer.Ordinal)
                .DistinctBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray();
            var index = -1;
            for (var position = 0; position < episodes.Length; position++)
                if (episodes[position].Id == selected.Id) { index = position; break; }
            if (index < 0) return single;
            // 保留本次目标读取到的最新续播信息与标题。
            return new(episodes.SetItem(index, selected).Select(ToEntry).ToImmutableArray(), index, startTicks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || account.Token.IsCancellationRequested) { throw; }
        catch (AppException) { return single; }
        catch (HttpRequestException) { return single; }
    }

    public static PlaybackEntry ToEntry(EmbyItem item)
    {
        var mapped = EmbyMapper.Item(item) ?? throw PlaybackTargetResolver.NotPlayable();
        var episode = mapped.Kind == MediaKind.Episode;
        var season = Math.Max(0, mapped.ParentIndexNumber ?? 0);
        var number = Math.Max(0, mapped.IndexNumber ?? 0);
        var title = episode && !string.IsNullOrWhiteSpace(mapped.SeriesName) ?
            $"{mapped.SeriesName} S{season:00}E{number:00} - {mapped.Name}" : mapped.Name;
        return new(mapped.Id, title)
        {
            SeriesId = mapped.SeriesId, SeriesName = mapped.SeriesName, EpisodeName = episode ? mapped.Name : null,
            SeasonId = mapped.SeasonId, SeasonNumber = mapped.ParentIndexNumber, EpisodeNumber = mapped.IndexNumber,
            EpisodeLabel = episode && mapped.IndexNumber is not null ? $"第{number}集" : null,
            DurationTicks = mapped.RunTimeTicks, UserData = mapped.UserData,
            ProductionYear = mapped.ProductionYear ?? mapped.PremiereDate?.Year,
            Image = mapped.Images.FirstOrDefault(image => image.Kind == ImageKind.Backdrop) ?? mapped.Images.FirstOrDefault(),
        };
    }
}
