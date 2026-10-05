using System.Collections.Immutable;
using Mambo.Core.Contracts;

namespace Mambo.App.Debug;

internal sealed record MotionLibraryRequest(string LibraryId, LibraryQuery Specification, MotionPagedQuery<MediaItem> Query);
internal sealed record MotionEpisodeRequest(string SeasonId, MotionPagedQuery<MediaItem> Query);

/// <summary>Local, explicitly completed reads for real library and season presentation scenarios.</summary>
internal sealed class MotionLibraryFixture(ILibraryService fallback, IUiScheduler scheduler) : ILibraryService, IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<string> heldLibraries = new(StringComparer.Ordinal);
    private readonly HashSet<string> heldSeasons = new(StringComparer.Ordinal);
    private readonly List<MotionLibraryRequest> libraryRequests = [];
    private readonly List<MotionEpisodeRequest> episodeRequests = [];
    private bool disposed;

    public IReadOnlyList<MotionLibraryRequest> LibraryRequests => libraryRequests;
    public IReadOnlyList<MotionEpisodeRequest> EpisodeRequests => episodeRequests;
    internal IQuery<FilterOptions>? FilterOptionsOverride { get; set; }

    public void HoldLibrary(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            heldLibraries.Add(id);
        }
    }

    public void HoldEpisodes(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            heldSeasons.Add(id);
        }
    }

    public IPagedQuery<MediaItem> ObserveLibrary(string libraryId, LibraryQuery query, int pageSize = 60,
        CancellationToken scopeToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (heldLibraries.Contains(libraryId))
            {
                var controlled = new MotionPagedQuery<MediaItem>(scheduler, scopeToken);
                libraryRequests.Add(new MotionLibraryRequest(libraryId, query, controlled));
                return controlled;
            }
        }
        return fallback.ObserveLibrary(libraryId, query, pageSize, scopeToken);
    }

    public IPagedQuery<MediaItem> ObserveEpisodes(string seasonId, int pageSize = 30,
        CancellationToken scopeToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (heldSeasons.Contains(seasonId))
            {
                var controlled = new MotionPagedQuery<MediaItem>(scheduler, scopeToken);
                episodeRequests.Add(new MotionEpisodeRequest(seasonId, controlled));
                return controlled;
            }
        }
        return fallback.ObserveEpisodes(seasonId, pageSize, scopeToken);
    }

    public IQuery<ImmutableArray<MediaLibrary>> ObserveLibraries(CancellationToken scopeToken = default) =>
        fallback.ObserveLibraries(scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveHero(CancellationToken scopeToken = default) =>
        fallback.ObserveHero(scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveContinueWatching(CancellationToken scopeToken = default) =>
        fallback.ObserveContinueWatching(scopeToken);
    public IQuery<ImmutableArray<MediaItem>> ObserveLatest(string libraryId, CancellationToken scopeToken = default) =>
        fallback.ObserveLatest(libraryId, scopeToken);
    public IPagedQuery<MediaItem> ObserveRecent(int pageSize = 120, CancellationToken scopeToken = default) =>
        fallback.ObserveRecent(pageSize, scopeToken);
    public IQuery<FilterOptions> ObserveFilters(string libraryId, CancellationToken scopeToken = default) =>
        FilterOptionsOverride ?? fallback.ObserveFilters(libraryId, scopeToken);
    public IQuery<MediaItem> ObserveDetail(string itemId, CancellationToken scopeToken = default) =>
        fallback.ObserveDetail(itemId, scopeToken);
    public IQuery<MediaItem> ObserveNextUp(string seriesId, CancellationToken scopeToken = default) =>
        fallback.ObserveNextUp(seriesId, scopeToken);
    public IQuery<ImmutableArray<SeasonInfo>> ObserveSeasons(string seriesId, CancellationToken scopeToken = default) =>
        fallback.ObserveSeasons(seriesId, scopeToken);
    public IQuery<ImmutableArray<SearchGroup>> ObserveSearchGroups(CancellationToken scopeToken = default) =>
        fallback.ObserveSearchGroups(scopeToken);
    public IPagedQuery<MediaItem> ObserveSearch(string libraryId, string searchText, int pageSize = 24,
        CancellationToken scopeToken = default) => fallback.ObserveSearch(libraryId, searchText, pageSize, scopeToken);
    public void PrefetchDetail(string itemId) => fallback.PrefetchDetail(itemId);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var request in libraryRequests) request.Query.Dispose();
            foreach (var request in episodeRequests) request.Query.Dispose();
        }
    }
}

/// <summary>Controlled page deliveries; cancellation of a caller never completes the shared read.</summary>
internal sealed class MotionPagedQuery<T> : IPagedQuery<T>
{
    private readonly object gate = new();
    private readonly IUiScheduler scheduler;
    private readonly CancellationTokenRegistration scopeRegistration;
    private TaskCompletionSource? pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImmutableArray<T> items = [];
    private AppError? error;
    private bool initialized;
    private bool hasMore = true;
    private int? totalCount;
    private bool refreshing;
    private bool disposed;
    private bool notificationQueued;
    private int refreshCount;
    private int loadMoreCount;

    public MotionPagedQuery(IUiScheduler scheduler, CancellationToken scopeToken = default)
    {
        this.scheduler = scheduler;
        scopeRegistration = scopeToken.Register(static state => ((MotionPagedQuery<T>)state!).Dispose(), this);
        // Registration can invoke Dispose synchronously for an already cancelled scope.
        if (IsDisposed) scopeRegistration.Dispose();
        else Notify();
    }

    public ImmutableArray<T> Items { get { lock (gate) return items; } }
    public int? TotalCount { get { lock (gate) return totalCount; } }
    public bool IsInitialized { get { lock (gate) return initialized; } }
    public bool HasMore { get { lock (gate) return hasMore && !disposed; } }
    public bool IsLoading { get { lock (gate) return pending is not null; } }
    public bool IsRefreshing { get { lock (gate) return refreshing; } }
    public AppError? Error { get { lock (gate) return error; } }
    public bool IsDisposed { get { lock (gate) return disposed; } }
    public int RefreshCount { get { lock (gate) return refreshCount; } }
    public int LoadMoreCount { get { lock (gate) return loadMoreCount; } }
    public event EventHandler? Updated;

    public void Complete(ImmutableArray<T> page, bool hasMore = false, int? totalCount = null)
    {
        lock (gate)
        {
            if (disposed || pending is null) return;
            if (page.IsDefault) throw new ArgumentException("A completed query requires an initialized array.", nameof(page));
            items = initialized && !refreshing ? items.AddRange(page) : page;
            this.hasMore = hasMore;
            this.totalCount = totalCount ?? (hasMore ? null : items.Length);
            initialized = true;
            error = null;
            refreshing = false;
            pending.TrySetResult();
            pending = null;
        }
        Notify();
    }

    public void Fail(AppError error)
    {
        lock (gate)
        {
            if (disposed || pending is null) return;
            ArgumentNullException.ThrowIfNull(error);
            this.error = error;
            refreshing = false;
            // Service failures are observable state, not faulted command tasks.
            pending.TrySetResult();
            pending = null;
        }
        Notify();
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Begin(true, cancellationToken);
    public Task LoadMoreAsync(CancellationToken cancellationToken = default) => Begin(false, cancellationToken);

    private Task Begin(bool refresh, CancellationToken cancellationToken)
    {
        Task task;
        var started = false;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (pending is null)
            {
                if (!refresh && initialized && !hasMore) return Task.CompletedTask;
                pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                refreshing = refresh;
                if (refresh) refreshCount++;
                else loadMoreCount++;
                started = true;
            }
            task = pending.Task;
        }
        if (started) Notify();
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private void Notify()
    {
        lock (gate)
        {
            if (disposed || notificationQueued) return;
            notificationQueued = true;
        }
        if (!scheduler.TryEnqueue(() =>
        {
            EventHandler? updated;
            lock (gate)
            {
                notificationQueued = false;
                if (disposed) return;
                updated = Updated;
            }
            updated?.Invoke(this, EventArgs.Empty);
        }))
        {
            lock (gate) notificationQueued = false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            refreshing = false;
            pending?.TrySetCanceled();
            pending = null;
            Updated = null;
        }
        scopeRegistration.Dispose();
    }
}
