using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

public sealed class FakeLibraryService : ILibraryService
{
    private readonly DemoCatalog catalog;
    private readonly IUiScheduler scheduler;
    private readonly FakeOperation operation;

    public FakeLibraryService(DemoCatalog catalog, IUiScheduler scheduler, FakeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(operation);
        this.catalog = catalog;
        this.scheduler = scheduler;
        this.operation = operation;
    }

    public IQuery<ImmutableArray<MediaLibrary>> ObserveLibraries(CancellationToken scopeToken = default) =>
        Observe(() => catalog.Libraries, scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveHero(CancellationToken scopeToken = default) =>
        Observe(() => catalog.AllItems.Where(item => item.Kind is MediaKind.Movie or MediaKind.Series)
            .Where(item => item.Kind == MediaKind.Series || int.Parse(item.Id.AsSpan("demo-movie-".Length), CultureInfo.InvariantCulture) <= 5)
            .ToImmutableArray(), scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveContinueWatching(CancellationToken scopeToken = default) =>
        Observe(() => catalog.AllItems.Where(item => item.UserData.PlaybackPositionTicks > 0 && !item.UserData.Played)
            .OrderByDescending(item => item.UserData.LastPlayedUtc)
            .DistinctBy(item => item.SeriesId ?? item.Id, StringComparer.Ordinal).Take(24).ToImmutableArray(), scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveLatest(string libraryId, CancellationToken scopeToken = default) =>
        Observe(() => LibraryItems(libraryId).OrderByDescending(item => item.PremiereDate)
            .ThenBy(item => item.Id, StringComparer.Ordinal).Take(16).ToImmutableArray(), scopeToken);
    public IPagedQuery<MediaItem> ObserveRecent(int pageSize = 120, CancellationToken scopeToken = default) =>
        Paged(() => catalog.AllItems.Where(item => item.Kind is MediaKind.Movie or MediaKind.Episode or MediaKind.Video)
            .Where(item => item.UserData.LastPlayedUtc is not null).OrderByDescending(item => item.UserData.LastPlayedUtc)
            .ToImmutableArray(), pageSize, scopeToken);

    public IPagedQuery<MediaItem> ObserveLibrary(string libraryId, LibraryQuery query, int pageSize = 60,
        CancellationToken scopeToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Paged(() => Sort(LibraryItems(libraryId).Where(item =>
            (query.Genres.IsDefaultOrEmpty || item.Genres.Any(genre => query.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))) &&
            (query.Years.IsDefaultOrEmpty || (item.ProductionYear is { } year && query.Years.Contains(year))) &&
            (query.OfficialRatings.IsDefaultOrEmpty || (item.OfficialRating is { } rating && query.OfficialRatings.Contains(rating, StringComparer.OrdinalIgnoreCase)))),
            query).ToImmutableArray(), pageSize, scopeToken);
    }

    public IQuery<FilterOptions> ObserveFilters(string libraryId, CancellationToken scopeToken = default) =>
        Observe(() =>
        {
            var items = LibraryItems(libraryId).ToImmutableArray();
            return new FilterOptions
            {
                Genres = items.SelectMany(item => item.Genres).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToImmutableArray(),
                Years = items.Where(item => item.ProductionYear.HasValue).Select(item => item.ProductionYear!.Value)
                    .Distinct().OrderDescending().ToImmutableArray(),
                OfficialRatings = items.Where(item => item.OfficialRating is not null).Select(item => item.OfficialRating!)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            };
        }, scopeToken);
    public IQuery<MediaItem> ObserveDetail(string itemId, CancellationToken scopeToken = default) =>
        Observe(() => RequireItem(itemId), scopeToken);
    public IQuery<MediaItem> ObserveNextUp(string seriesId, CancellationToken scopeToken = default) =>
        Observe(() =>
        {
            RequireKind(seriesId, MediaKind.Series);
            var episodes = catalog.AllItems.Where(item => item.SeriesId == seriesId && item.Kind == MediaKind.Episode)
                .OrderBy(item => item.ParentIndexNumber).ThenBy(item => item.IndexNumber).ToImmutableArray();
            return episodes.FirstOrDefault(item => item.UserData.PlaybackPositionTicks >= TimeSpan.FromSeconds(30).Ticks) ??
                episodes.FirstOrDefault(item => !item.UserData.Played) ?? episodes.FirstOrDefault()!;
        }, scopeToken);
    public IQuery<ImmutableArray<SeasonInfo>> ObserveSeasons(string seriesId, CancellationToken scopeToken = default) =>
        Observe(() =>
        {
            RequireKind(seriesId, MediaKind.Series);
            return catalog.AllItems.Where(item => item.Kind == MediaKind.Season && item.SeriesId == seriesId)
                .OrderBy(item => item.IndexNumber).Select(item => new SeasonInfo(item.Id, seriesId, item.Name, item.IndexNumber)
                { Image = item.Images.FirstOrDefault() }).ToImmutableArray();
        }, scopeToken);
    public IPagedQuery<MediaItem> ObserveEpisodes(string seasonId, int pageSize = 30, CancellationToken scopeToken = default) =>
        Paged(() =>
        {
            RequireKind(seasonId, MediaKind.Season);
            return catalog.AllItems.Where(item => item.Kind == MediaKind.Episode && item.SeasonId == seasonId)
                .OrderBy(item => item.IndexNumber).ToImmutableArray();
        }, pageSize, scopeToken);
    public IQuery<ImmutableArray<SearchGroup>> ObserveSearchGroups(CancellationToken scopeToken = default) =>
        Observe(() => catalog.Libraries.Select((library, index) => new SearchGroup(library.Id, library.Name, index))
            .ToImmutableArray(), scopeToken);

    // 合成目录已全部驻留内存，无需额外 I/O；与真实服务的悬停入口保持一致。
    public void PrefetchDetail(string itemId) { _ = catalog.Find(itemId); }

    public IPagedQuery<MediaItem> ObserveSearch(string libraryId, string searchText, int pageSize = 24,
        CancellationToken scopeToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchText);
        var normalized = NormalizeSearch(searchText);
        return Paged(() =>
        {
            RequireLibrary(libraryId);
            if (normalized.Length == 0) return [];
            return catalog.AllItems.Where(item => item.LibraryId == libraryId && item.Kind is
                    MediaKind.Movie or MediaKind.Series or MediaKind.Video or MediaKind.Episode)
                .Where(item => NormalizeSearch(item.Name).Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                    (item.SeriesName is { } seriesName && NormalizeSearch(seriesName).Contains(normalized, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Kind == MediaKind.Episode && item.SeriesId is { } seriesId ? RequireItem(seriesId) : item)
                .DistinctBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray();
        }, pageSize, scopeToken);
    }

    private FakeQuery<T> Observe<T>(Func<T> getValue, CancellationToken scopeToken)
    {
        var query = new FakeQuery<T>(scheduler, async cancellationToken =>
        {
            await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return getValue();
        }, scopeToken);
        _ = ObserveInitialAsync(query.RefreshAsync(CancellationToken.None));
        return query;
    }

    private FakePagedQuery<MediaItem> Paged(Func<ImmutableArray<MediaItem>> getItems, int pageSize,
        CancellationToken scopeToken)
    {
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize), "每页条数必须为 1 到 500。");
        var query = new FakePagedQuery<MediaItem>(scheduler, async (offset, count, cancellationToken) =>
        {
            await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var data = getItems();
            var page = data.Skip(offset).Take(count).ToImmutableArray();
            return new FakePage<MediaItem>(page, data.Length, offset + page.Length < data.Length);
        }, pageSize, scopeToken);
        _ = ObserveInitialAsync(query.LoadMoreAsync(CancellationToken.None));
        return query;
    }

    private static async Task ObserveInitialAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 页面作用域或 Dispose 已结束自动读取。 */ }
    }

    private IEnumerable<MediaItem> LibraryItems(string libraryId)
    {
        RequireLibrary(libraryId);
        return catalog.AllItems.Where(item => item.LibraryId == libraryId && item.Kind is
            MediaKind.Movie or MediaKind.Series or MediaKind.Video);
    }

    private void RequireLibrary(string libraryId)
    {
        if (!catalog.Libraries.Any(library => library.Id == libraryId))
            throw new AppException(new AppError(AppErrorKind.Contract, "demo.library_missing", "演示媒体库不存在。", false));
    }

    private MediaItem RequireItem(string itemId) => catalog.Find(itemId) ??
        throw new AppException(new AppError(AppErrorKind.Contract, ErrorCodes.ItemNotFound, "演示条目不存在。", false));
    private void RequireKind(string itemId, MediaKind kind)
    {
        if (RequireItem(itemId).Kind != kind)
            throw new AppException(new AppError(AppErrorKind.Contract, ErrorCodes.ItemNotPlayable, "演示条目类型不匹配。", false));
    }

    private static IEnumerable<MediaItem> Sort(IEnumerable<MediaItem> items, LibraryQuery query)
    {
        var descending = query.Direction == SortDirection.Descending;
        IOrderedEnumerable<MediaItem> sorted = query.Sort switch
        {
            LibrarySort.Name => descending ? items.OrderByDescending(item => item.SortName, StringComparer.Ordinal) : items.OrderBy(item => item.SortName, StringComparer.Ordinal),
            LibrarySort.CommunityRating => descending ? items.OrderByDescending(item => item.CommunityRating) : items.OrderBy(item => item.CommunityRating),
            LibrarySort.ProductionYear => descending ? items.OrderByDescending(item => item.ProductionYear) : items.OrderBy(item => item.ProductionYear),
            LibrarySort.Runtime => descending ? items.OrderByDescending(item => item.RunTimeTicks) : items.OrderBy(item => item.RunTimeTicks),
            _ => descending ? items.OrderByDescending(item => item.PremiereDate) : items.OrderBy(item => item.PremiereDate),
        };
        return sorted.ThenBy(item => item.Id, StringComparer.Ordinal);
    }

    private static string NormalizeSearch(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}
