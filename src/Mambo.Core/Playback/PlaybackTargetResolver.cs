using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

/// <summary>目标解析不使用 UI 查询缓存，读取服务器的最新续播状态。</summary>
public sealed class PlaybackTargetResolver(EmbyApi api, RequestScheduler? scheduler = null)
{
    public const string EpisodeFields = "SortName,RunTimeTicks,PrimaryImageItemId,PrimaryImageTag,ParentBackdropItemId,ParentBackdropImageTags";

    public async Task<(EmbyItem Item, long StartTicks)> ResolveAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken)
    {
        if (EmbyMapper.Identity(request.ItemId) is null || request.StartTicks < 0)
            throw Invalid();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Token);
        var item = await FetchAsync(ct => api.DetailAsync(account, request.ItemId, ct), linked.Token).ConfigureAwait(false);
        if (EmbyMapper.Identity(item.Id) is null) throw new AppException(ErrorText.InvalidResponse("解析播放目标"));
        var kind = EmbyMapper.Kind(item.Type);
        if (kind is MediaKind.Series)
        {
            var next = await FetchAsync(ct => api.ItemsAsync(account, "Shows/NextUp?UserId=" + EmbyApi.Escape(account.Secret.UserId) +
                "&SeriesId=" + EmbyApi.Escape(item.Id!) + "&Limit=1&Fields=" + EmbyApi.Escape(EpisodeFields), ct), linked.Token).ConfigureAwait(false);
            var selected = next.Items?.FirstOrDefault(IsEpisode);
            if (selected is null)
                selected = SelectFallback(await EpisodesAsync(account, item.Id!, linked.Token).ConfigureAwait(false));
            item = selected ?? throw NotPlayable();
        }
        else if (kind is MediaKind.Season)
            item = SelectFallback(await EpisodesAsync(account, item.Id!, linked.Token).ConfigureAwait(false)) ?? throw NotPlayable();
        else if (kind is not (MediaKind.Movie or MediaKind.Episode or MediaKind.Video)) throw NotPlayable();
        return (item, request.StartTicks ?? Math.Max(0, item.UserData?.PlaybackPositionTicks ?? 0));
    }

    internal Task<EmbyItem[]> EpisodesAsync(AccountSession account, string parentId, CancellationToken token) =>
        new EmbyEpisodeReader(api, scheduler).ReadAllAsync(account, parentId, EpisodeFields, token);

    internal Task<T> FetchAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token) =>
        scheduler is null ? operation(token) : scheduler.RunAsync(operation, scopeToken: token);

    internal static bool IsEpisode(EmbyItem? item) => item is not null && EmbyMapper.Identity(item.Id) is not null &&
        item.Type?.Equals("Episode", StringComparison.OrdinalIgnoreCase) == true;

    internal static EmbyItem? SelectFallback(EmbyItem[] items)
    {
        var episodes = items.Where(IsEpisode).OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(item => item.IndexNumber ?? int.MaxValue).ThenBy(item => item.SortName ?? item.Name, StringComparer.Ordinal).ToArray();
        return episodes.FirstOrDefault(item => item.UserData?.PlaybackPositionTicks >= 30 * TimeSpan.TicksPerSecond) ??
            episodes.FirstOrDefault(item => item.UserData?.Played != true) ?? episodes.FirstOrDefault();
    }

    internal static AppException NotPlayable() => new(new(AppErrorKind.Contract, ErrorCodes.ItemNotPlayable, "此项目没有可播放的视频。", false, "解析播放目标"));
    internal static AppException Invalid() => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "播放请求参数无效。", false, "解析播放目标"));
}
