using System.Collections.Immutable;
using System.Globalization;
using Mambo.Core.Contracts;

namespace Mambo.Core.BulletChat;

/// <summary>匹配结果；没有匹配到时 Episode 为 null、Comments 为空。</summary>
public sealed record BulletChatResolution(BulletChatEpisode? Episode, ImmutableArray<BulletChatComment> Comments)
{
    public static BulletChatResolution NotMatched { get; } = new(null, []);
}

/// <summary>弹幕来源。失败抛 AppException，取消抛 OperationCanceledException。</summary>
public interface IBulletChatProvider
{
    /// <summary>为播放条目自动匹配并加载弹幕。</summary>
    Task<BulletChatResolution> ResolveAsync(PlaybackEntry entry, CancellationToken cancellationToken);
    Task<ImmutableArray<BulletChatAnime>> SearchAsync(string keyword, CancellationToken cancellationToken);
    Task<ImmutableArray<BulletChatEpisode>> GetEpisodesAsync(string animeId, CancellationToken cancellationToken);
    /// <summary>按用户指定的剧集加载，并记住这次选择。</summary>
    Task<BulletChatResolution> SelectAsync(PlaybackEntry entry, BulletChatEpisode episode, CancellationToken cancellationToken);
}

/// <summary>
/// 弹弹play 兼容服务上的自动匹配：记忆 → 按剧名搜索并打分 → 按集号或位置取集（必要时顺延到后续条目）→ 文件名匹配兜底。
/// </summary>
public sealed class DandanplayBulletChatProvider(DandanplayClient client, BulletChatHistory history, Func<string?> scope) : IBulletChatProvider
{
    private const int MaximumCandidates = 2;
    private const int MaximumSequels = 3;

    public async Task<BulletChatResolution> ResolveAsync(PlaybackEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (BulletChatTarget.From(entry) is not { } target) return BulletChatResolution.NotMatched;
        var account = scope() ?? "";

        if (history.Find(BulletChatHistory.ItemKey(account, entry.ItemId)) is { EpisodeId: { Length: > 0 } exact } pinned)
            return await LoadAsync(new(exact, pinned.AnimeId, pinned.AnimeTitle, pinned.EpisodeTitle ?? ""), cancellationToken).ConfigureAwait(false);

        var seasonKey = entry.SeriesId is { Length: > 0 } series && !target.IsMovie ? BulletChatHistory.SeasonKey(account, series, target.Season) : null;
        if (seasonKey is not null && history.Find(seasonKey) is { } memory)
        {
            var remembered = await client.BangumiAsync(memory.AnimeId, cancellationToken).ConfigureAwait(false);
            // 偏移超出该条目的集数时，这条记忆对本集不适用，继续走自动匹配。
            if (BulletChatMatcher.PickByOffset(target.Episode!.Value, memory.Offset, remembered.Bangumi?.Episodes) is { } episode)
                return await LoadAsync(Describe(memory.AnimeId, memory.AnimeTitle, episode), cancellationToken).ConfigureAwait(false);
        }

        if (await SearchAndPickAsync(target, cancellationToken).ConfigureAwait(false) is { } found)
        {
            await RememberAsync(account, entry, seasonKey, target, found.Anime, found.Pick, manual: false, cancellationToken).ConfigureAwait(false);
            return await LoadAsync(Describe(found.Anime.Id, found.Anime.AnimeTitle ?? "", found.Pick.Episode), cancellationToken).ConfigureAwait(false);
        }

        var fileName = target.IsMovie ? target.Title
            : string.Create(CultureInfo.InvariantCulture, $"{target.Title} S{target.Season ?? 1:00}E{target.Episode:00}");
        var guesses = await client.MatchAsync(fileName, cancellationToken).ConfigureAwait(false);
        if (BulletChatMatcher.BestMatch(target, guesses.Matches) is not { } guess) return BulletChatResolution.NotMatched;
        var animeId = guess.AnimeId.ToString(CultureInfo.InvariantCulture);
        var guessed = new DandanEpisode { EpisodeId = guess.EpisodeId, EpisodeTitle = guess.EpisodeTitle };
        if (seasonKey is not null)
        {
            // 文件名匹配只给出剧集编号；查一次剧集列表换算成位置偏移，后续各集才能沿用。
            var listing = BulletChatMatcher.Regular((await client.BangumiAsync(animeId, cancellationToken).ConfigureAwait(false)).Bangumi?.Episodes);
            for (var index = 0; index < listing.Count; index++)
            {
                if (listing[index].EpisodeId != guess.EpisodeId) continue;
                await history.RememberAsync(seasonKey, new(animeId, guess.AnimeTitle ?? "", index - (target.Episode!.Value - 1), false), cancellationToken).ConfigureAwait(false);
                break;
            }
        }
        else await RememberItemAsync(account, entry, animeId, guess.AnimeTitle ?? "", guessed, manual: false, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(Describe(animeId, guess.AnimeTitle ?? "", guessed), cancellationToken).ConfigureAwait(false);
    }

    private async Task<(DandanAnime Anime, BulletChatPick Pick)?> SearchAndPickAsync(BulletChatTarget target, CancellationToken token)
    {
        var search = await client.SearchAnimeAsync(target.Title, token).ConfigureAwait(false);
        var ranked = BulletChatMatcher.Rank(target, search.Animes);
        if (ranked.Count == 0 && Shorten(target.Title) is { } shorter)
        {
            // 带副标题的片名整体搜不到时，用主标题再搜一次；打分仍对照完整片名。
            search = await client.SearchAnimeAsync(shorter, token).ConfigureAwait(false);
            ranked = BulletChatMatcher.Rank(target, search.Animes);
        }
        foreach (var anime in ranked.Take(MaximumCandidates))
        {
            var episodes = (await client.BangumiAsync(anime.Id, token).ConfigureAwait(false)).Bangumi?.Episodes;
            if (BulletChatMatcher.PickEpisode(target, episodes) is { } pick) return (anime, pick);
            if (target.Episode is not { } wanted) continue;
            // 集号超出这个条目：分割放送或绝对集号，顺延到同一作品的后续条目。
            var remaining = wanted - BulletChatMatcher.Regular(episodes).Count;
            if (remaining <= 0) continue;
            foreach (var sequel in BulletChatMatcher.Sequels(anime, search.Animes).Take(MaximumSequels))
            {
                var regular = BulletChatMatcher.Regular((await client.BangumiAsync(sequel.Id, token).ConfigureAwait(false)).Bangumi?.Episodes);
                var index = BulletChatMatcher.IndexOfNumber(regular, wanted);
                if (index < 0 && remaining <= regular.Count) index = remaining - 1;
                if (index >= 0) return (sequel, new(regular[index], index - (wanted - 1)));
                remaining -= regular.Count;
                if (remaining <= 0) break;
            }
        }
        return null;
    }

    private static string? Shorten(string title)
    {
        var cut = title.IndexOfAny([':', '：', '~', '～', '-', '—', ' ', '　']);
        return cut >= 2 && title[..cut].Trim() is { Length: >= 2 } head && head != title ? head : null;
    }

    public async Task<ImmutableArray<BulletChatAnime>> SearchAsync(string keyword, CancellationToken cancellationToken)
    {
        var search = await client.SearchAnimeAsync(keyword, cancellationToken).ConfigureAwait(false);
        return [.. (search.Animes ?? []).Where(anime => anime is not null && !string.IsNullOrWhiteSpace(anime.AnimeTitle)).Take(50)
            .Select(anime => new BulletChatAnime(anime.Id, anime.AnimeTitle!.Trim()) { TypeLabel = anime.TypeDescription, Year = anime.Year, EpisodeCount = Math.Max(0, anime.EpisodeCount) })];
    }

    public async Task<ImmutableArray<BulletChatEpisode>> GetEpisodesAsync(string animeId, CancellationToken cancellationToken)
    {
        var bangumi = (await client.BangumiAsync(animeId, cancellationToken).ConfigureAwait(false)).Bangumi;
        return [.. (bangumi?.Episodes ?? []).Where(episode => episode is not null && episode.EpisodeId > 0).Take(2000)
            .Select(episode => Describe(animeId, bangumi?.AnimeTitle ?? "", episode))];
    }

    public async Task<BulletChatResolution> SelectAsync(PlaybackEntry entry, BulletChatEpisode episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(episode);
        var resolution = await LoadAsync(episode, cancellationToken).ConfigureAwait(false);
        var account = scope() ?? "";
        await RememberItemAsync(account, entry, episode.AnimeId, episode.AnimeTitle, episode.Id, episode.Title, manual: true, cancellationToken).ConfigureAwait(false);
        if (BulletChatTarget.From(entry) is { Episode: { } wanted } target && entry.SeriesId is { Length: > 0 } series &&
            long.TryParse(episode.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var chosen))
        {
            // 选的是正片时换算成位置偏移，让这一季后面的集自动跟上；选的是特别篇则只记这一集。
            var regular = BulletChatMatcher.Regular((await client.BangumiAsync(episode.AnimeId, cancellationToken).ConfigureAwait(false)).Bangumi?.Episodes);
            for (var index = 0; index < regular.Count; index++)
            {
                if (regular[index].EpisodeId != chosen) continue;
                await history.RememberAsync(BulletChatHistory.SeasonKey(account, series, target.Season),
                    new(episode.AnimeId, episode.AnimeTitle, index - (wanted - 1), true), cancellationToken).ConfigureAwait(false);
                break;
            }
        }
        return resolution;
    }

    private Task RememberAsync(string account, PlaybackEntry entry, string? seasonKey, BulletChatTarget target, DandanAnime anime, BulletChatPick pick, bool manual, CancellationToken token) =>
        seasonKey is not null && !target.IsMovie
            ? history.RememberAsync(seasonKey, new(anime.Id, anime.AnimeTitle ?? "", pick.Offset, manual), token)
            : RememberItemAsync(account, entry, anime.Id, anime.AnimeTitle ?? "", pick.Episode, manual, token);

    private Task RememberItemAsync(string account, PlaybackEntry entry, string animeId, string animeTitle, DandanEpisode episode, bool manual, CancellationToken token) =>
        RememberItemAsync(account, entry, animeId, animeTitle, episode.EpisodeId.ToString(CultureInfo.InvariantCulture), episode.EpisodeTitle, manual, token);

    private Task RememberItemAsync(string account, PlaybackEntry entry, string animeId, string animeTitle, string episodeId, string? episodeTitle, bool manual, CancellationToken token) =>
        history.RememberAsync(BulletChatHistory.ItemKey(account, entry.ItemId), new(animeId, animeTitle, 0, manual) { EpisodeId = episodeId, EpisodeTitle = episodeTitle }, token);

    private async Task<BulletChatResolution> LoadAsync(BulletChatEpisode episode, CancellationToken token)
    {
        var comments = await client.CommentsAsync(episode.Id, token).ConfigureAwait(false);
        return new(episode, BulletChatParser.Parse(comments.Comments));
    }

    private static BulletChatEpisode Describe(string animeId, string animeTitle, DandanEpisode episode) =>
        new(episode.EpisodeId.ToString(CultureInfo.InvariantCulture), animeId, animeTitle.Trim(), (episode.EpisodeTitle ?? "").Trim());
}
