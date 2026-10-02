using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>A pure managed preview player. It never creates mpv or opens a media URL.</summary>
public sealed class FakePlaybackService : IPlaybackService, IDisposable, IAsyncDisposable
{
    private readonly DemoCatalog catalog;
    private readonly FakeOperation operation;
    private readonly FakeOptions options;
    private readonly TimeProvider clock;
    private readonly IUiScheduler scheduler;
    private readonly IMessenger messenger;
    private readonly object gate = new();
    private FakePlaybackSession? current;
    private bool isStarting;
    private bool disposed;

    public FakePlaybackService(DemoCatalog catalog, FakeOperation operation, FakeOptions options,
        TimeProvider clock, IUiScheduler scheduler, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(messenger);
        this.catalog = catalog;
        this.operation = operation;
        this.options = options;
        this.clock = clock;
        this.scheduler = scheduler;
        this.messenger = messenger;
    }

    public IPlaybackSession? Current { get { lock (gate) return current is { IsClosed: false } ? current : null; } }
    public bool IsStarting { get { lock (gate) return isStarting; } }
    public event EventHandler? Changed;
    public event EventHandler<PlaybackSessionEventArgs>? SessionStarted;
    public event EventHandler<PlaybackSessionEventArgs>? SessionEnded;
    public event EventHandler<PlaybackEntrySkippedEventArgs>? EntrySkipped;

    public async Task<IPlaybackSession> PlayAsync(PlayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (entries, entryIndex, startTicks) = Resolve(request);
        FakePlaybackSession? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is { IsClosed: true }) current = null;
            if (current is not null && current.ItemId == entries[entryIndex].ItemId)
                return current;
            if (isStarting)
                throw Error("demo.playback.busy", "正在打开播放，请稍候。");
            if (current is not null && !request.ReplaceCurrent)
                throw Error("demo.playback.confirm", "正在播放其他项目，请先确认切换播放。");
            previous = current;
            isStarting = true;
        }
        NotifyChanged();
        try
        {
            if (previous is not null)
                await previous.CloseAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            FakePlaybackSession created;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                created = new FakePlaybackSession(entries, entryIndex, startTicks, operation, options,
                    clock, scheduler, messenger, OnClosed, OnEntrySkipped);
                current = created;
                isStarting = false;
            }
            scheduler.TryEnqueue(() =>
            {
                lock (gate) if (disposed || created.IsClosed || !ReferenceEquals(current, created)) return;
                SessionStarted?.Invoke(this, new PlaybackSessionEventArgs(created));
                Changed?.Invoke(this, EventArgs.Empty);
            });
            created.Start();
            return created;
        }
        catch
        {
            lock (gate) isStarting = false;
            NotifyChanged();
            throw;
        }
    }

    public Task<IPlaybackSession> PreviewAsync(CancellationToken cancellationToken = default) =>
        PlayAsync(new PlayRequest("demo-episode-001-01-01", 0, true), cancellationToken);

    private (ImmutableArray<PlaybackEntry> Entries, int Index, long Start) Resolve(PlayRequest request)
    {
        if (request.StartTicks < 0)
            throw Error("demo.playback.position", "播放起点不能小于零。");
        var item = catalog.Find(request.ItemId) ?? throw Error("demo.playback.not-found", "未找到演示媒体。");
        if (item.Kind is MediaKind.Series or MediaKind.Season)
        {
            var episodes = catalog.AllItems.Where(candidate => candidate.Kind == MediaKind.Episode &&
                (item.Kind == MediaKind.Series ? candidate.SeriesId == item.Id : candidate.SeasonId == item.Id))
                .OrderBy(candidate => candidate.ParentIndexNumber).ThenBy(candidate => candidate.IndexNumber)
                .ThenBy(candidate => candidate.SortName, StringComparer.Ordinal).ToArray();
            item = episodes.FirstOrDefault(candidate => candidate.UserData.PlaybackPositionTicks >= TimeSpan.FromSeconds(30).Ticks)
                ?? episodes.FirstOrDefault(candidate => !candidate.UserData.Played)
                ?? episodes.FirstOrDefault()
                ?? throw Error("demo.playback.empty", "这个演示节目没有可播放的剧集。");
        }
        if (item.Kind is not (MediaKind.Movie or MediaKind.Episode or MediaKind.Video))
            throw Error("demo.playback.kind", "这个项目暂时不能播放。");
        var plan = item.Kind == MediaKind.Episode && item.SeasonId is not null
            ? catalog.AllItems.Where(candidate => candidate.Kind == MediaKind.Episode && candidate.SeasonId == item.SeasonId)
                .OrderBy(candidate => candidate.ParentIndexNumber).ThenBy(candidate => candidate.IndexNumber)
                .ThenBy(candidate => candidate.SortName, StringComparer.Ordinal).Select(ToEntry).ToImmutableArray()
            : [ToEntry(item)];
        var index = -1;
        for (var i = 0; i < plan.Length; i++)
            if (plan[i].ItemId == item.Id) { index = i; break; }
        if (index < 0) { plan = [ToEntry(item)]; index = 0; }
        return (plan, index, request.StartTicks ?? item.UserData.PlaybackPositionTicks);
    }

    private static PlaybackEntry ToEntry(MediaItem item) => new(item.Id,
        item.Kind == MediaKind.Episode ? $"{item.SeriesName} · 第{item.IndexNumber}集 {item.Name}" : item.Name)
    {
        SeriesId = item.SeriesId,
        SeasonId = item.SeasonId,
        SeasonNumber = item.ParentIndexNumber,
        EpisodeNumber = item.IndexNumber,
        EpisodeLabel = item.Kind == MediaKind.Episode ? $"S{item.ParentIndexNumber:00}E{item.IndexNumber:00}" : null,
        DurationTicks = item.RunTimeTicks ?? TimeSpan.FromMinutes(24).Ticks,
    };

    private void OnClosed(FakePlaybackSession session)
    {
        lock (gate)
        {
            if (disposed) return;
            if (ReferenceEquals(current, session)) current = null;
        }
        SessionEnded?.Invoke(this, new PlaybackSessionEventArgs(session));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEntrySkipped(PlaybackEntry entry, AppError error) =>
        EntrySkipped?.Invoke(this, new PlaybackEntrySkippedEventArgs(entry, error));

    private void NotifyChanged() => scheduler.TryEnqueue(() =>
    {
        lock (gate) if (disposed) return;
        Changed?.Invoke(this, EventArgs.Empty);
    });

    private static AppException Error(string code, string message) =>
        new(new AppError(AppErrorKind.Player, code, message, false));

    public void Dispose()
    {
        FakePlaybackSession? session;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            session = current;
            current = null;
        }
        session?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        FakePlaybackSession? session;
        lock (gate) session = current;
        if (session is not null) await session.CloseAsync().ConfigureAwait(false);
        Dispose();
    }
}
