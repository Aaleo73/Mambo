using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Data;

public sealed partial class LibraryService
{
    private FilteredObservation ObserveFilteredLibrary(string libraryId, LibraryQuery specification,
        int pageSize, CancellationToken scope)
    {
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var account = CaptureAccount();
        var observation = new FilteredObservation(this, account, libraryId, specification,
            Scope(account, scope), pageSize);
        Register(observation);
        Start(() => observation.LoadMoreAsync(CancellationToken.None));
        return observation;
    }

    private LibraryScan GetScan(AccountSession account, string libraryId, LibraryQuery specification, bool refresh = false)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            account.Token.ThrowIfCancellationRequested();
            var key = (account.Scope, libraryId, specification.Sort, specification.Direction);
            if (!refresh && scans.TryGetValue(key, out var existing) &&
                DateTimeOffset.UtcNow - existing.CreatedAt < TimeSpan.FromMinutes(5)) return existing;
            // Only metadata is retained; image bytes and credentials are never part of a scan.
            if (scans.Count >= 8) scans.Remove(scans.MinBy(pair => pair.Value.CreatedAt).Key);
            var scan = new LibraryScan(this, account, libraryId, specification with { Genres = [], Years = [], OfficialRatings = [] });
            scans[key] = scan;
            return scan;
        }
    }

    private void InvalidateScans(AccountSession? account, string libraryId)
    {
        if (account is null) return;
        lock (gate)
            foreach (var key in scans.Keys.Where(key => key.Scope == account.Scope && key.Library == libraryId).ToArray()) scans.Remove(key);
    }

    private async Task<EmbyItems> FetchLibraryItemsAsync(AccountSession account, string libraryId,
        LibraryQuery specification, int offset, int count, CancellationToken token)
    {
        ValidateId(libraryId); ValidateQuery(specification);
        var libraries = await cache.FetchAsync(new(account.Scope, "libraries"), account,
            ct => FetchLibrariesAsync(account, ct), token).ConfigureAwait(false);
        var library = libraries.FirstOrDefault(item => item.Id == libraryId) ?? throw Missing("媒体库");
        return await GetItemsAsync(account, RequestPriority.Foreground, token,
            ("ParentId", libraryId), ("Recursive", "true"), ("IncludeItemTypes", library.Kind switch
            { LibraryKind.Movies => "Movie,Video", LibraryKind.TvShows => "Series", _ => "Movie,Series,Video" }),
            ("Fields", EmbyApi.ItemFields + ",ParentLogoItemId,ParentLogoImageTag"),
            ("SortBy", specification.Sort switch { LibrarySort.Name => "SortName", LibrarySort.CommunityRating => "CommunityRating",
                LibrarySort.ProductionYear => "ProductionYear", LibrarySort.Runtime => "Runtime", _ => "DateCreated" }),
            ("SortOrder", specification.Direction == SortDirection.Ascending ? "Ascending" : "Descending"),
            ("Genres", string.Join('|', specification.Genres)), ("Years", string.Join(',', specification.Years)),
            ("OfficialRatings", string.Join('|', specification.OfficialRatings)),
            ("StartIndex", Number(offset)), ("Limit", Number(count))).ConfigureAwait(false);
    }

    private sealed record ScanPage(ImmutableArray<MediaItem> Items, bool HasMore);

    /// <summary>共享已读取的无条件分页；查询取消只中止当前读取，完整页可供下次筛选复用。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification =
        "The shared async-only semaphore never creates a wait handle. Evicted scans can still have readers; they are collected together.")]
    private sealed class LibraryScan(LibraryService owner, AccountSession account, string libraryId, LibraryQuery specification)
    {
        private readonly SemaphoreSlim serial = new(1);
        private readonly List<MediaItem> items = [];
        private readonly HashSet<string> ids = new(StringComparer.Ordinal);
        private int rawOffset;
        private bool complete;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        public async Task<ScanPage> ReadAsync(int offset, CancellationToken token)
        {
            await serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (items.Count <= offset && !complete)
                {
                    token.ThrowIfCancellationRequested();
                    var response = await owner.FetchLibraryItemsAsync(account, libraryId, specification, rawOffset, 500, token).ConfigureAwait(false);
                    var raw = response.Items ?? [];
                    if (raw.Length > 500 || response.TotalRecordCount < 0) throw InvalidResponse();
                    var next = checked(rawOffset + raw.Length);
                    var more = response.TotalRecordCount is { } total ? next < total : raw.Length == 500;
                    if (raw.Length == 0 && more) throw InvalidResponse();
                    var mapped = await owner.MapItemsAsync(account, raw.Where(item =>
                        EmbyMapper.Kind(item.Type) is MediaKind.Movie or MediaKind.Series or MediaKind.Video).ToArray(), libraryId, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    items.AddRange(mapped.Where(item => ids.Add(item.Id)));
                    rawOffset = next;
                    complete = !more;
                }
                var page = items.Skip(offset).Take(500).ToImmutableArray();
                return new(page, offset + page.Length < items.Count || !complete);
            }
            finally { serial.Release(); }
        }
    }

    private sealed class FilteredObservation : Observation, IPagedQuery<MediaItem>
    {
        private readonly LibraryService owner;
        private readonly string libraryId;
        private readonly LibraryQuery specification;
        private readonly ObservablePagedQuery<MediaItem> query;
        private readonly object sync = new();
        private LibraryScan? scan;
        private int cursor;
        private long generation;

        public FilteredObservation(LibraryService owner, AccountSession? account, string libraryId,
            LibraryQuery specification, CancellationTokenSource cancellation, int pageSize)
            : base(owner, account, "library", QueryArgs(libraryId, specification), cancellation)
        {
            this.owner = owner; this.libraryId = libraryId; this.specification = specification;
            query = new(owner.scheduler, LoadAsync, pageSize, scopeToken: cancellation.Token);
            query.Updated += QueryUpdated;
        }

        private async Task<QueryPage<MediaItem>> LoadAsync(int offset, int count, CancellationToken token)
        {
            var account = Account ?? throw NotLoggedIn();
            ValidateQuery(specification); ValidateId(libraryId);
            long version;
            int position;
            LibraryScan? currentScan;
            lock (sync)
            {
                version = ++generation;
                position = offset == 0 ? 0 : cursor;
                currentScan = offset == 0 ? null : scan;
            }
            var fresh = offset == 0 && query.IsInitialized;
            bool useScan;
            lock (owner.gate) useScan = owner.ignoredFilters.Contains((account.Scope, libraryId));
            if (currentScan is null && useScan)
            {
                currentScan = owner.GetScan(account, libraryId, specification, fresh);
                // Another query may discover unsupported filters between our native pages.
                position = 0;
            }
            var known = (offset == 0 ? [] : query.Items).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ImmutableArray<MediaItem> candidates;
                bool more;
                int next;
                if (currentScan is not null)
                {
                    var page = await currentScan.ReadAsync(position, token).ConfigureAwait(false);
                    var matches = ImmutableArray.CreateBuilder<MediaItem>();
                    var consumed = 0;
                    foreach (var item in page.Items)
                    {
                        consumed++;
                        if (MatchesFilters(item, specification) && known.Add(item.Id)) matches.Add(item);
                        if (matches.Count == count) break;
                    }
                    candidates = matches.ToImmutable();
                    next = position + consumed;
                    more = consumed < page.Items.Length || page.HasMore;
                }
                else
                {
                    var response = await owner.FetchLibraryItemsAsync(account, libraryId, specification, position, count, token).ConfigureAwait(false);
                    var raw = response.Items ?? [];
                    if (raw.Length > count || response.TotalRecordCount < 0) throw InvalidResponse();
                    next = checked(position + raw.Length);
                    more = response.TotalRecordCount is { } total ? next < total : raw.Length == count;
                    if (raw.Length == 0 && more) throw InvalidResponse();
                    var mapped = await owner.MapItemsAsync(account, raw.Where(item =>
                        EmbyMapper.Kind(item.Type) is MediaKind.Movie or MediaKind.Series or MediaKind.Video).ToArray(), libraryId, token).ConfigureAwait(false);
                    if (mapped.Any(item => !MatchesFilters(item, specification)))
                    {
                        // Restart without server filters. A partially supported combination has
                        // a different cursor, so its offsets cannot be reused for the full scan.
                        token.ThrowIfCancellationRequested();
                        lock (owner.gate) owner.ignoredFilters.Add((account.Scope, libraryId));
                        currentScan = owner.GetScan(account, libraryId, specification, fresh);
                        position = 0;
                        continue;
                    }
                    candidates = mapped.Where(item => known.Add(item.Id)).ToImmutableArray();
                }
                if (!candidates.IsEmpty || !more)
                {
                    token.ThrowIfCancellationRequested();
                    lock (sync)
                        if (version == generation && !token.IsCancellationRequested) { cursor = next; scan = currentScan; }
                    return new(candidates, null, more);
                }
                position = next;
            }
        }

        private void QueryUpdated(object? sender, EventArgs args)
        { if (!Disposed && !Lifetime.IsCancellationRequested) Updated?.Invoke(this, args); }
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
            if (query.IsInitialized) owner.InvalidateScans(Account, libraryId);
            return query.RefreshAsync(cancellationToken);
        }
        public Task LoadMoreAsync(CancellationToken cancellationToken = default) => query.LoadMoreAsync(cancellationToken);
        public override void Refresh() { if (!Disposed && !Lifetime.IsCancellationRequested) Start(() => RefreshAsync(CancellationToken.None)); }
        protected override void Release() { query.Updated -= QueryUpdated; Updated = null; query.Dispose(); }
    }

    private sealed class FilterObservation : IQuery<FilterOptions>
    {
        private readonly LibraryService owner;
        private readonly AccountSession? account;
        private readonly string libraryId;
        private readonly IQuery<FilterOptions> query;
        private readonly CancellationToken scope;
        private bool disposed;
        public FilterObservation(LibraryService owner, AccountSession? account, string libraryId, IQuery<FilterOptions> query, CancellationToken scope)
        {
            this.owner = owner; this.account = account; this.libraryId = libraryId; this.query = query;
            this.scope = scope;
            query.Updated += QueryUpdated;
            owner.FiltersChanged += OptionsUpdated;
        }
        public FilterOptions? Current
        {
            get
            {
                lock (owner.gate)
                    return account is null ? query.Current : MergeFilters(query.Current ?? new(),
                        owner.loaded.GetValueOrDefault((account.Scope, libraryId)) ?? new());
            }
        }
        public bool IsInitialized => query.IsInitialized;
        public bool IsRefreshing => query.IsRefreshing;
        public AppError? Error => query.Error;
        public event EventHandler? Updated;
        private bool Active => !disposed && !owner.disposed && !scope.IsCancellationRequested && account?.Token.IsCancellationRequested != true;
        private void QueryUpdated(object? sender, EventArgs args)
        { if (Active) Updated?.Invoke(this, args); }
        private void OptionsUpdated(AccountSession session, string id)
        { if (Active && ReferenceEquals(session, account) && id == libraryId) Updated?.Invoke(this, EventArgs.Empty); }
        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (query.IsInitialized) owner.InvalidateScans(account, libraryId);
            return query.RefreshAsync(cancellationToken);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; owner.FiltersChanged -= OptionsUpdated; query.Updated -= QueryUpdated;
            Updated = null; query.Dispose();
        }
    }
}
