using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Data;

/// <summary>按观察创建时的账号读取元数据，使用共享 SWR 与独立增量分页。</summary>
public sealed partial class LibraryService : ILibraryService, IDisposable
{
    private const string EpisodeFields = "SortName,RunTimeTicks,PremiereDate,ProductionYear,Overview,ParentLogoItemId,ParentLogoImageTag,ParentBackdropItemId,ParentBackdropImageTags,PrimaryImageItemId,PrimaryImageTag,BackdropImageTags";
    private readonly AccountContext accounts;
    private readonly EmbyApi api;
    private readonly RequestScheduler requests;
    private readonly QueryCache cache;
    private readonly IUiScheduler scheduler;
    private readonly IMessenger messenger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly HashSet<Observation> observations = [];
    private readonly Dictionary<(string Scope, string Library), FilterOptions> loaded = [];
    private readonly Dictionary<(string Scope, string Library, LibrarySort Sort, SortDirection Direction), LibraryScan> scans = [];
    private readonly HashSet<(string Scope, string Library)> ignoredFilters = [];
    private event Action<AccountSession, string>? FiltersChanged;
    private bool disposed;

    public LibraryService(AccountContext accounts, EmbyApi api, RequestScheduler requests, QueryCache cache,
        IUiScheduler scheduler, IMessenger messenger)
    {
        this.accounts = accounts; this.api = api; this.requests = requests;
        this.cache = cache; this.scheduler = scheduler; this.messenger = messenger;
        accounts.Changed += AccountChanged;
        messenger.Register<LibraryService, PlaybackStopped>(this, static (service, message) => service.Stopped(message));
    }

    public IQuery<ImmutableArray<MediaLibrary>> ObserveLibraries(CancellationToken scopeToken = default) =>
        Observe("libraries", "", FetchLibrariesAsync, scope: scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveHero(CancellationToken scopeToken = default) =>
        Observe("hero", "", async (account, token) =>
        {
            var response = await GetItemsAsync(account, RequestPriority.Foreground, token,
                ("Recursive", "true"), ("IncludeItemTypes", "Movie,Series,Video"), ("SortBy", "Random"),
                ("Limit", "32"), ("Fields", EmbyApi.ResumeFields + ",RunTimeTicks,CommunityRating,ProductionYear,OfficialRating"));
            var items = (await MapItemsAsync(account, response.Items ?? [], null, token)).Where(item => !item.Images.IsEmpty).ToArray();
            Random.Shared.Shuffle(items);
            return items.OrderBy(item => item.Images.Any(image => image.Kind == ImageKind.Backdrop) ? 0 :
                item.Images.Any(image => image.Kind == ImageKind.Thumb) ? 1 : 2).Take(8).ToImmutableArray();
        }, scope: scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveContinueWatching(CancellationToken scopeToken = default) =>
        Observe("continue", "", async (account, token) =>
        {
            var response = await GetItemsAsync(account, RequestPriority.Foreground, token, ResumeParameters(0, 16));
            return (await MapItemsAsync(account, response.Items ?? [], null, token)).Where(Resumable)
                .DistinctBy(item => item.SeriesId ?? item.Id, StringComparer.Ordinal).ToImmutableArray();
        }, scope: scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveLatest(string libraryId, CancellationToken scopeToken = default) =>
        Observe("latest", libraryId, async (account, token) =>
        {
            ValidateId(libraryId);
            var items = await requests.RunAsync(ct => api.LatestAsync(account, libraryId, ct),
                RequestPriority.Visible, scopeToken: token).ConfigureAwait(false);
            return await MapItemsAsync(account, items, libraryId, token).ConfigureAwait(false);
        }, scope: scopeToken);
    public IPagedQuery<MediaItem> ObserveRecent(int pageSize = 120, CancellationToken scopeToken = default) =>
        Paged("recent", "", pageSize, (account, offset, count, token) =>
            GetItemsAsync(account, RequestPriority.Foreground, token, ResumeParameters(offset, count)), scope: scopeToken);

    public IPagedQuery<MediaItem> ObserveLibrary(string libraryId, LibraryQuery query, int pageSize = 60,
        CancellationToken scopeToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filtered = !query.Genres.IsDefaultOrEmpty || !query.Years.IsDefaultOrEmpty || !query.OfficialRatings.IsDefaultOrEmpty;
        if (filtered) return ObserveFilteredLibrary(libraryId, query, pageSize, scopeToken);
        var defaultQuery = query.Sort == LibrarySort.DateCreated && query.Direction == SortDirection.Descending && !filtered;
        return Paged(defaultQuery ? "library-first" : "library", QueryArgs(libraryId, query), pageSize,
            (account, offset, count, token) => FetchLibraryItemsAsync(account, libraryId, query, offset, count, token),
            libraryId, scope: scopeToken);
    }

    public IQuery<FilterOptions> ObserveFilters(string libraryId, CancellationToken scopeToken = default)
    {
        var account = CaptureAccount();
        var query = Observe("filters", libraryId, (session, token) => FetchFiltersAsync(session, libraryId, token),
            TimeSpan.FromMinutes(5), scopeToken);
        return new FilterObservation(this, account, libraryId, query, scopeToken);
    }
    public IQuery<MediaItem> ObserveDetail(string itemId, CancellationToken scopeToken = default) =>
        Observe("detail", itemId, (account, token) => FetchDetailAsync(account, itemId, RequestPriority.Foreground, token), scope: scopeToken);
    public IQuery<MediaItem> ObserveNextUp(string seriesId, CancellationToken scopeToken = default) =>
        Observe("nextup", seriesId, (account, token) => FetchNextUpAsync(account, seriesId, token), scope: scopeToken);
    public IQuery<ImmutableArray<SeasonInfo>> ObserveSeasons(string seriesId, CancellationToken scopeToken = default) =>
        Observe("seasons", seriesId, async (account, token) =>
        {
            ValidateId(seriesId);
            var response = await GetItemsAsync(account, RequestPriority.Foreground, token, ("ParentId", seriesId),
                ("IncludeItemTypes", "Season"), ("SortBy", "IndexNumber"), ("SortOrder", "Ascending"),
                ("Limit", "100"), ("Fields", "PrimaryImageItemId,PrimaryImageTag"));
            return (response.Items ?? []).Where(item => EmbyMapper.Identity(item.Id) is not null && item.Type?.Equals("Season", StringComparison.OrdinalIgnoreCase) == true)
                .Select(item => new SeasonInfo(item.Id!, seriesId, item.Name ?? "未命名季", item.IndexNumber)
                { Image = EmbyMapper.Images(item).FirstOrDefault(image => image.Kind == ImageKind.Primary) }).ToImmutableArray();
        }, scope: scopeToken);
    public IPagedQuery<MediaItem> ObserveEpisodes(string seasonId, int pageSize = 30, CancellationToken scopeToken = default) =>
        Paged("episodes", seasonId, pageSize, (account, offset, count, token) =>
        {
            ValidateId(seasonId);
            return GetItemsAsync(account, RequestPriority.Foreground, token, ("ParentId", seasonId),
                ("IncludeItemTypes", "Episode"), ("Recursive", "true"), ("SortBy", "ParentIndexNumber,IndexNumber,SortName"),
                ("SortOrder", "Ascending"), ("Fields", EpisodeFields), ("StartIndex", Number(offset)), ("Limit", Number(count)));
        }, scope: scopeToken);
    public IQuery<ImmutableArray<SearchGroup>> ObserveSearchGroups(CancellationToken scopeToken = default) =>
        Observe("search-groups", "", async (account, token) =>
            (await cache.FetchAsync(new(account.Scope, "libraries"), account, ct => FetchLibrariesAsync(account, ct), token))
                .Select((library, index) => new SearchGroup(library.Id, library.Name, index)).ToImmutableArray(), scope: scopeToken);
    public IPagedQuery<MediaItem> ObserveSearch(string libraryId, string searchText, int pageSize = 24,
        CancellationToken scopeToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchText);
        if (searchText.Length > 256)
            throw new AppException(new AppError(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "搜索词不能超过 256 个字符。", false));
        var normalized = NormalizeSearch(searchText);
        var phrase = "\"" + normalized + "\"";
        return Paged("search", libraryId + "|" + EmbyApi.Escape(normalized), pageSize,
            (account, offset, count, token) =>
            {
                ValidateId(libraryId);
                return normalized.Length == 0 ? Task.FromResult(new EmbyItems { Items = [], TotalRecordCount = 0 }) :
                    GetItemsAsync(account, RequestPriority.Foreground, token, ("ParentId", libraryId), ("SearchTerm", phrase),
                        ("Recursive", "true"), ("IncludeItemTypes", "Movie,Series,Video"), ("Fields", EmbyApi.ItemFields),
                        ("StartIndex", Number(offset)), ("Limit", Number(count)));
            }, libraryId, foldEpisodes: true, scope: scopeToken);
    }

    public void PrefetchDetail(string itemId)
    {
        AccountSession? account;
        lock (gate) { if (disposed) return; account = accounts.Current; }
        if (account is null || EmbyMapper.Identity(itemId) is null) return;
        _ = PrefetchAsync(account, itemId);
    }
    private async Task PrefetchAsync(AccountSession account, string itemId)
    {
        try
        {
            using var query = cache.Observe(new(account.Scope, "detail", itemId), account,
                ct => FetchDetailAsync(account, itemId, RequestPriority.Background, ct), updateFetcher: false, scopeToken: account.Token);
            if (!query.IsInitialized || query.IsRefreshing) await query.RefreshAsync(account.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or AppException) { }
    }

    private ReadObservation<T> Observe<T>(string kind, string args, Func<AccountSession, CancellationToken, Task<T>> fetch,
        TimeSpan? staleAfter = null, CancellationToken scope = default)
    {
        var account = CaptureAccount();
        var cancellation = Scope(account, scope);
        IQuery<T> query = account is null ? new ObservableQuery<T>(scheduler, _ => Task.FromException<T>(NotLoggedIn()), cancellation.Token) :
            cache.Observe(new(account.Scope, kind, args), account, ct => fetch(account, ct), staleAfter: staleAfter, scopeToken: cancellation.Token);
        var observation = new ReadObservation<T>(this, account, kind, args, cancellation, query);
        Register(observation);
        if (account is null) Start(() => query.RefreshAsync(CancellationToken.None));
        return observation;
    }

    private PageObservation Paged(string kind, string args, int pageSize,
        Func<AccountSession, int, int, CancellationToken, Task<EmbyItems>> fetch,
        string? libraryId = null, bool foldEpisodes = false, CancellationToken scope = default)
    {
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize), "每页条数必须为 1 到 500。");
        var account = CaptureAccount();
        var cancellation = Scope(account, scope);
        IQuery<QueryPage<MediaItem>> first = account is null ? new ObservableQuery<QueryPage<MediaItem>>(scheduler,
            _ => Task.FromException<QueryPage<MediaItem>>(NotLoggedIn()), cancellation.Token) :
            cache.Observe(new(account.Scope, kind, args + "|size=" + Number(pageSize)), account,
                ct => PageAsync(account, fetch, 0, pageSize, libraryId, foldEpisodes, [], ct), scopeToken: cancellation.Token);
        var initial = first.IsInitialized ? first.Current : null;
        var observation = new PageObservation(this, account, kind, args, cancellation, first, scheduler, pageSize, initial,
            (offset, seen, token) => account is null ? Task.FromException<QueryPage<MediaItem>>(NotLoggedIn()) :
                PageAsync(account, fetch, offset, pageSize, libraryId, foldEpisodes, seen, token),
            libraryId is not null && kind is "library-first" or "library" ? () => InvalidateScans(account, libraryId) : null);
        Register(observation);
        if (initial is null) Start(() => observation.LoadMoreAsync(CancellationToken.None));
        return observation;
    }

    private async Task<QueryPage<MediaItem>> PageAsync(AccountSession account,
        Func<AccountSession, int, int, CancellationToken, Task<EmbyItems>> fetch, int offset, int pageSize,
        string? libraryId, bool foldEpisodes, ImmutableArray<string> seen, CancellationToken token)
    {
        var known = seen.ToHashSet(StringComparer.Ordinal);
        var totalUnknown = foldEpisodes;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var response = await fetch(account, offset, pageSize, token).ConfigureAwait(false);
            var raw = response.Items ?? [];
            if (raw.Length > pageSize || response.TotalRecordCount < 0) throw InvalidResponse();
            var next = checked(offset + raw.Length);
            var more = response.TotalRecordCount is { } total ? next < total : raw.Length == pageSize;
            if (raw.Length == 0 && more) throw InvalidResponse();
            var candidates = libraryId is not null && !foldEpisodes ? raw.Where(item =>
                EmbyMapper.Kind(item.Type) is MediaKind.Movie or MediaKind.Series or MediaKind.Video).ToArray() : raw;
            var mapped = await MapItemsAsync(account, candidates, libraryId, token).ConfigureAwait(false);
            if (foldEpisodes)
            {
                var folded = ImmutableArray.CreateBuilder<MediaItem>();
                foreach (var item in mapped)
                    folded.Add(item.Kind == MediaKind.Episode && item.SeriesId is { } seriesId ?
                        await CachedDetailAsync(account, seriesId, token).ConfigureAwait(false) : item);
                mapped = folded.Where(item => item.Kind is MediaKind.Movie or MediaKind.Series or MediaKind.Video).ToImmutableArray();
            }
            var distinct = mapped.Where(item => known.Add(item.Id)).ToImmutableArray();
            totalUnknown |= distinct.Length != raw.Length;
            if (!distinct.IsEmpty || !more)
                return new(distinct, totalUnknown ? null : response.TotalRecordCount, more) { NextOffset = next };
            offset = next;
        }
    }

    private static bool MatchesFilters(MediaItem item, LibraryQuery filters)
    {
        if (!filters.Years.IsEmpty && (item.ProductionYear is not { } year || !filters.Years.Contains(year))) return false;
        if (!filters.OfficialRatings.IsEmpty && (item.OfficialRating is not { } rating ||
            !filters.OfficialRatings.Contains(rating, StringComparer.OrdinalIgnoreCase))) return false;
        if (filters.Genres.IsEmpty) return true;
        foreach (var genre in filters.Genres)
            if (item.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task<ImmutableArray<MediaLibrary>> FetchLibrariesAsync(AccountSession account, CancellationToken token)
    {
        var response = await requests.RunAsync(ct => api.ItemsAsync(account, "Users/" + EmbyApi.Escape(account.Secret.UserId) + "/Views", ct),
            scopeToken: token).ConfigureAwait(false);
        return (response.Items ?? []).Select(EmbyMapper.Library).Where(item => item is not null).Select(item => item!)
            .DistinctBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray();
    }
    private Task<EmbyItems> GetItemsAsync(AccountSession account, RequestPriority priority, CancellationToken token,
        params (string Name, string Value)[] parameters) => requests.RunAsync(ct => api.ItemsAsync(account,
            "Users/" + EmbyApi.Escape(account.Secret.UserId) + "/Items?" + Query(parameters), ct), priority, scopeToken: token);
    private async Task<MediaItem> FetchDetailAsync(AccountSession account, string itemId, RequestPriority priority, CancellationToken token)
    {
        ValidateId(itemId);
        var dto = await requests.RunAsync(ct => api.DetailAsync(account, itemId, ct), priority, scopeToken: token).ConfigureAwait(false);
        MediaItem? series = null;
        if (dto.Type?.Equals("Episode", StringComparison.OrdinalIgnoreCase) == true && EmbyMapper.Identity(dto.SeriesId) is { } seriesId && seriesId != itemId)
            series = await SeriesImagesAsync(account, seriesId, token).ConfigureAwait(false);
        return EmbyMapper.Item(dto, series: series) ?? throw InvalidResponse();
    }
    private Task<MediaItem> CachedDetailAsync(AccountSession account, string itemId, CancellationToken token) =>
        cache.FetchAsync(new(account.Scope, "detail", itemId), account,
            ct => FetchDetailAsync(account, itemId, RequestPriority.Visible, ct), token, updateFetcher: false);
    private Task<MediaItem> SeriesImagesAsync(AccountSession account, string seriesId, CancellationToken token) =>
        cache.FetchAsync(new(account.Scope, "series-images", seriesId), account, async ct =>
        {
            var dto = await requests.RunAsync(requestToken => api.DetailAsync(account, seriesId, requestToken),
                RequestPriority.Visible, scopeToken: ct).ConfigureAwait(false);
            if (EmbyMapper.Kind(dto.Type) != MediaKind.Series) throw InvalidResponse();
            return EmbyMapper.Item(dto) ?? throw InvalidResponse();
        }, token);
    private async Task<ImmutableArray<MediaItem>> MapItemsAsync(AccountSession account, EmbyItem[] items, string? libraryId, CancellationToken token)
    {
        var mapped = await Task.WhenAll(items.Select(async dto =>
        {
            MediaItem? series = null;
            if (dto.Type?.Equals("Episode", StringComparison.OrdinalIgnoreCase) == true && EmbyMapper.Identity(dto.SeriesId) is { } seriesId && seriesId != dto.Id)
                series = await SeriesImagesAsync(account, seriesId, token).ConfigureAwait(false);
            return EmbyMapper.Item(dto, libraryId, series);
        })).ConfigureAwait(false);
        var result = mapped.Where(item => item is not null).Select(item => item!).ToImmutableArray();
        if (libraryId is not null) PublishFilters(account, libraryId, DeriveFilters(result));
        return result;
    }

    private void PublishFilters(AccountSession account, string libraryId, FilterOptions options)
    {
        lock (gate)
        {
            if (disposed || account.Token.IsCancellationRequested) return;
            var key = (account.Scope, libraryId);
            var previous = loaded.GetValueOrDefault(key) ?? new();
            var merged = MergeFilters(previous, options);
            if (previous.Genres.SequenceEqual(merged.Genres) && previous.Years.SequenceEqual(merged.Years) &&
                previous.OfficialRatings.SequenceEqual(merged.OfficialRatings)) return;
            loaded[key] = merged;
        }
        scheduler.TryEnqueue(() =>
        {
            if (!disposed && !account.Token.IsCancellationRequested) FiltersChanged?.Invoke(account, libraryId);
        });
    }

    private async Task<MediaItem> FetchNextUpAsync(AccountSession account, string seriesId, CancellationToken token)
    {
        ValidateId(seriesId);
        var next = await requests.RunAsync(ct => api.ItemsAsync(account, "Shows/NextUp?" + Query(
            ("UserId", account.Secret.UserId), ("SeriesId", seriesId), ("Limit", "1"), ("Fields", EpisodeFields)), ct), scopeToken: token).ConfigureAwait(false);
        var items = await MapItemsAsync(account, next.Items ?? [], null, token).ConfigureAwait(false);
        if (items.FirstOrDefault(item => item.Kind == MediaKind.Episode && (item.SeriesId is null || item.SeriesId == seriesId)) is { } target) return target;
        var all = await GetItemsAsync(account, RequestPriority.Foreground, token, ("ParentId", seriesId),
            ("Recursive", "true"), ("IncludeItemTypes", "Episode"), ("Limit", "500"),
            ("SortBy", "ParentIndexNumber,IndexNumber,SortName"), ("SortOrder", "Ascending"), ("Fields", EpisodeFields));
        var episodes = (await MapItemsAsync(account, all.Items ?? [], null, token)).Where(item => item.Kind == MediaKind.Episode &&
                (item.SeriesId is null || item.SeriesId == seriesId)).OrderBy(item => item.ParentIndexNumber).ThenBy(item => item.IndexNumber)
            .ThenBy(item => item.SortName, StringComparer.Ordinal).Take(500).ToArray();
        return episodes.FirstOrDefault(item => item.UserData.PlaybackPositionTicks >= TimeSpan.FromSeconds(30).Ticks) ??
            episodes.FirstOrDefault(item => !item.UserData.Played) ?? episodes.FirstOrDefault()!;
    }

    private async Task<FilterOptions> FetchFiltersAsync(AccountSession account, string libraryId, CancellationToken token)
    {
        ValidateId(libraryId);
        FilterOptions filters;
        try
        {
            var response = await requests.RunAsync(ct => api.FiltersAsync(account, libraryId, ct), RequestPriority.Visible, scopeToken: token).ConfigureAwait(false);
            filters = new() { Genres = EmbyMapper.Strings(response.Genres?.Select(item => item.Name)),
                Years = (response.Years ?? []).Distinct().OrderDescending().ToImmutableArray(),
                OfficialRatings = EmbyMapper.Strings(response.OfficialRatings?.Select(item => item.Name)) };
        }
        catch (AppException error) when (error.Error.Kind != AppErrorKind.Auth && !token.IsCancellationRequested)
        {
            // The fallback already visits the whole library. Retain the card metadata in the
            // shared scan so subsequent combinations do not repeat the same network walk.
            var scan = GetScan(account, libraryId, new LibraryQuery());
            filters = new();
            var offset = 0;
            while (true)
            {
                var page = await scan.ReadAsync(offset, token).ConfigureAwait(false);
                filters = MergeFilters(filters, DeriveFilters(page.Items));
                offset += page.Items.Length;
                if (!page.HasMore) break;
            }
        }
        FilterOptions cards;
        lock (gate) cards = loaded.GetValueOrDefault((account.Scope, libraryId)) ?? new();
        return MergeFilters(filters, cards);
    }

    private AccountSession? CaptureAccount() { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return accounts.Current; } }
    private CancellationTokenSource Scope(AccountSession? account, CancellationToken scope) =>
        CancellationTokenSource.CreateLinkedTokenSource(scope, account?.Token ?? CancellationToken.None, lifetime.Token);
    private void Register(Observation observation)
    {
        lock (gate) { if (disposed) { observation.Dispose(); throw new ObjectDisposedException(nameof(LibraryService)); } observations.Add(observation); }
    }
    private void Released(Observation observation) { lock (gate) observations.Remove(observation); }
    private void AccountChanged()
    {
        Observation[] old;
        lock (gate)
        {
            old = observations.Where(item => !ReferenceEquals(item.Account, accounts.Current)).ToArray();
            loaded.Clear(); scans.Clear(); ignoredFilters.Clear();
        }
        foreach (var item in old) item.Cancel();
    }
    private void Stopped(PlaybackStopped message)
    {
        var account = accounts.Current;
        if (account is null) return;
        lock (gate) scans.Clear();
        bool Related(string kind, string args) => kind is "continue" or "recent" or "library-first" or "library" ||
            kind == "detail" && args == message.ItemId || kind == "nextup" && args == message.SeriesId ||
            kind == "episodes" && message.SeasonId is not null && args.StartsWith(message.SeasonId + "|", StringComparison.Ordinal);
        cache.Invalidate(key => key.Scope == account.Scope && Related(key.Kind, key.Args));
        Observation[] active;
        lock (gate) active = observations.Where(item => ReferenceEquals(item.Account, account) &&
            item.IsPaged && Related(item.Kind, item.Args + "|")).ToArray();
        foreach (var item in active) item.Refresh();
    }
    public void Dispose()
    {
        Observation[] all;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; all = observations.ToArray(); observations.Clear(); loaded.Clear();
            scans.Clear(); ignoredFilters.Clear(); FiltersChanged = null;
        }
        accounts.Changed -= AccountChanged;
        messenger.UnregisterAll(this);
        lifetime.Cancel();
        foreach (var item in all) item.Dispose();
        lifetime.Dispose();
    }

    private static void Start(Func<Task> operation) => _ = AwaitAsync(operation);
    private static async Task AwaitAsync(Func<Task> operation)
    { try { await operation().ConfigureAwait(false); } catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { } }
    private static string Query(params (string Name, string Value)[] values) => string.Join('&', values
        .Where(pair => pair.Value.Length > 0).Select(pair => EmbyApi.Escape(pair.Name) + "=" + EmbyApi.Escape(pair.Value)));
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static (string, string)[] ResumeParameters(int offset, int count) =>
    [("Recursive", "true"), ("SortBy", "DatePlayed"), ("SortOrder", "Descending"), ("Filters", "IsResumable"),
        ("IncludeItemTypes", "Movie,Episode,Video"), ("Fields", EmbyApi.ResumeFields + ",RunTimeTicks"), ("StartIndex", Number(offset)), ("Limit", Number(count))];
    private static bool Resumable(MediaItem item) => !item.UserData.Played && item.UserData.PlaybackPositionTicks > 0 &&
        (item.RunTimeTicks is not > 0 || item.UserData.PlaybackPositionTicks < item.RunTimeTicks);
    private static string QueryArgs(string library, LibraryQuery query)
    {
        var genres = query.Genres.IsDefault ? ImmutableArray<string>.Empty : query.Genres;
        var years = query.Years.IsDefault ? ImmutableArray<int>.Empty : query.Years;
        var ratings = query.OfficialRatings.IsDefault ? ImmutableArray<string>.Empty : query.OfficialRatings;
        return library + "|" + query.Sort + "|" + query.Direction + "|" +
            string.Join(',', genres.Order(StringComparer.Ordinal).Select(EmbyApi.Escape)) + "|" +
            string.Join(',', years.Order()) + "|" +
            string.Join(',', ratings.Order(StringComparer.Ordinal).Select(EmbyApi.Escape));
    }
    private static string NormalizeSearch(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        if (!normalized.EnumerateRunes().Any(Rune.IsLetterOrDigit)) return "";
        var result = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune) || Rune.IsWhiteSpace(rune)) result.Append(rune.ToString());
        return result.ToString().Trim();
    }
    private static void ValidateId(string id) { if (EmbyMapper.Identity(id) is null) throw Missing("条目"); }
    private static void ValidateQuery(LibraryQuery query)
    {
        if (!Enum.IsDefined(query.Sort) || !Enum.IsDefined(query.Direction) || query.Genres.IsDefault || query.Years.IsDefault || query.OfficialRatings.IsDefault)
            throw new AppException(new AppError(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "资料库筛选无效。", false));
    }
    private static AppException Missing(string noun) => new(new AppError(AppErrorKind.Contract, ErrorCodes.ItemNotFound, noun + "不存在。", false));
    private static AppException InvalidResponse() => new(ErrorText.InvalidResponse("加载媒体资料"));
    private static AppException NotLoggedIn() => new(new AppError(AppErrorKind.Auth, ErrorCodes.NotLoggedIn, "请先连接服务器。", false));
    private static FilterOptions DeriveFilters(IEnumerable<MediaItem> items)
    {
        var rows = items.ToArray();
        return new() { Genres = rows.SelectMany(item => item.Genres).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray(),
            Years = rows.Where(item => item.ProductionYear.HasValue).Select(item => item.ProductionYear!.Value).Distinct().OrderDescending().ToImmutableArray(),
            OfficialRatings = rows.Select(item => item.OfficialRating).Where(item => item is not null).Select(item => item!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray() };
    }
    private static FilterOptions MergeFilters(FilterOptions first, FilterOptions second) => new()
    {
        Genres = first.Genres.Concat(second.Genres).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray(),
        Years = first.Years.Concat(second.Years).Distinct().OrderDescending().ToImmutableArray(),
        OfficialRatings = first.OfficialRatings.Concat(second.OfficialRatings).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray(),
    };

    private abstract class Observation(LibraryService owner, AccountSession? account, string kind, string args,
        CancellationTokenSource cancellation) : IDisposable
    {
        private readonly object lifecycleGate = new();
        protected readonly CancellationTokenSource Lifetime = cancellation;
        protected bool Disposed;
        public AccountSession? Account { get; } = account;
        public string Kind { get; } = kind;
        public string Args { get; } = args;
        public virtual bool IsPaged => false;
        public void Cancel() { lock (lifecycleGate) { if (!Disposed) Lifetime.Cancel(); } }
        public abstract void Refresh();
        protected abstract void Release();
        public void Dispose()
        {
            lock (lifecycleGate)
            {
                if (Disposed) return; Disposed = true; Lifetime.Cancel(); Release(); Lifetime.Dispose();
            }
            owner.Released(this);
        }
    }
    private sealed class ReadObservation<T> : Observation, IQuery<T>
    {
        private readonly IQuery<T> query;
        public ReadObservation(LibraryService owner, AccountSession? account, string kind, string args,
            CancellationTokenSource cancellation, IQuery<T> query) : base(owner, account, kind, args, cancellation)
        { this.query = query; query.Updated += QueryUpdated; }
        private void QueryUpdated(object? sender, EventArgs args)
        { if (!Disposed && !Lifetime.IsCancellationRequested) Updated?.Invoke(this, args); }
        public T? Current => query.Current;
        public bool IsInitialized => query.IsInitialized;
        public AppError? Error => query.Error;
        public bool IsRefreshing => query.IsRefreshing;
        public event EventHandler? Updated;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => query.RefreshAsync(cancellationToken);
        public override void Refresh() { if (!Disposed && !Lifetime.IsCancellationRequested) Start(() => query.RefreshAsync(CancellationToken.None)); }
        protected override void Release() { query.Updated -= QueryUpdated; Updated = null; query.Dispose(); }
    }
    private sealed class PageObservation : Observation, IPagedQuery<MediaItem>
    {
        private readonly IQuery<QueryPage<MediaItem>> first;
        private readonly ObservablePagedQuery<MediaItem> query;
        private readonly Func<int, ImmutableArray<string>, CancellationToken, Task<QueryPage<MediaItem>>> next;
        private readonly Action? beforeRefresh;
        private readonly object sync = new();
        private QueryPage<MediaItem>? lastFirst;
        private AppError? lastError;
        private bool consumeUpdate;
        private int rawOffset;
        private long generation;

        public PageObservation(LibraryService owner, AccountSession? account, string kind, string args,
            CancellationTokenSource cancellation, IQuery<QueryPage<MediaItem>> first, IUiScheduler scheduler, int pageSize,
            QueryPage<MediaItem>? initial, Func<int, ImmutableArray<string>, CancellationToken, Task<QueryPage<MediaItem>>> next,
            Action? beforeRefresh)
            : base(owner, account, kind, args, cancellation)
        {
            this.first = first; this.next = next; this.beforeRefresh = beforeRefresh;
            lastFirst = initial; rawOffset = initial?.NextOffset ?? initial?.Items.Length ?? 0;
            query = new(scheduler, LoadAsync, pageSize, initial: initial, scopeToken: cancellation.Token);
            first.Updated += FirstUpdated;
            query.Updated += QueryUpdated;
        }
        private async Task<QueryPage<MediaItem>> LoadAsync(int offset, int count, CancellationToken token)
        {
            long request;
            int sourceOffset;
            bool useUpdate;
            lock (sync) { if (offset == 0) generation++; request = generation; sourceOffset = rawOffset; useUpdate = consumeUpdate; consumeUpdate = false; }
            QueryPage<MediaItem> page;
            if (offset == 0)
            {
                if (!useUpdate && (query.IsInitialized || !first.IsInitialized))
                    await first.RefreshAsync(token).ConfigureAwait(false);
                if (first.Error is { } error) throw new AppException(error);
                page = first.Current ?? throw InvalidResponse();
            }
            else page = await next(sourceOffset, query.Items.Select(item => item.Id).ToImmutableArray(), token).ConfigureAwait(false);
            if (offset > 0 && query.TotalCount is null) page = page with { TotalCount = null };
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (request == generation && !token.IsCancellationRequested)
                { rawOffset = page.NextOffset ?? (offset == 0 ? 0 : sourceOffset) + page.Items.Length; if (offset == 0) { lastFirst = page; lastError = null; } }
            }
            return page;
        }
        private void FirstUpdated(object? sender, EventArgs args)
        {
            if (Disposed || Lifetime.IsCancellationRequested || first.IsRefreshing || query.IsLoading) return;
            lock (sync)
            {
                if (ReferenceEquals(first.Current, lastFirst) && Equals(first.Error, lastError)) return;
                lastFirst = first.Current; lastError = first.Error; consumeUpdate = true;
            }
            Refresh();
        }
        private void QueryUpdated(object? sender, EventArgs args)
        {
            if (Disposed || Lifetime.IsCancellationRequested) return;
            FirstUpdated(first, EventArgs.Empty);
            Updated?.Invoke(this, args);
        }
        public override bool IsPaged => true;
        public ImmutableArray<MediaItem> Items => query.Items;
        public int? TotalCount => query.TotalCount;
        public bool IsInitialized => query.IsInitialized;
        public bool HasMore => query.HasMore;
        public bool IsLoading => query.IsLoading;
        public bool IsRefreshing => query.IsRefreshing;
        public AppError? Error => query.Error;
        public event EventHandler? Updated;
        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (query.IsInitialized) beforeRefresh?.Invoke();
            return query.RefreshAsync(cancellationToken);
        }
        public Task LoadMoreAsync(CancellationToken cancellationToken = default) => query.LoadMoreAsync(cancellationToken);
        public override void Refresh() { if (!Disposed && !Lifetime.IsCancellationRequested) Start(() => RefreshAsync(CancellationToken.None)); }
        protected override void Release() { first.Updated -= FirstUpdated; query.Updated -= QueryUpdated; Updated = null; query.Dispose(); first.Dispose(); }
    }
}
