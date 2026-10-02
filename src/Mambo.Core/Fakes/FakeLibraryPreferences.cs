using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>演示账号的内存偏好；释放后拒绝读取与写入，并丢弃排队通知。</summary>
public sealed class FakeLibraryPreferences(IUiScheduler scheduler) : ILibraryPreferences, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, LibraryQuery> preferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> revisions = new(StringComparer.Ordinal);
    private bool disposed;
    public event EventHandler<LibraryPreferenceChangedEventArgs>? Changed;

    public LibraryQuery Get(string libraryId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ValidateLibraryId(libraryId);
            return preferences.GetValueOrDefault(libraryId) ?? new LibraryQuery();
        }
    }

    public Task SetAsync(string libraryId, LibraryQuery query, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateLibraryId(libraryId);
            ArgumentNullException.ThrowIfNull(query);
            if (!Enum.IsDefined(query.Sort) || !Enum.IsDefined(query.Direction) || query.Genres.IsDefault || query.Years.IsDefault || query.OfficialRatings.IsDefault)
                throw new AppException(new AppError(AppErrorKind.Contract, "demo.preference", "资料库筛选无效。", false));
            preferences[libraryId] = query;
            PublishLocked(libraryId, query);
        }
        return Task.CompletedTask;
    }

    public Task ResetAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateLibraryId(libraryId);
            preferences.Remove(libraryId);
            PublishLocked(libraryId, new LibraryQuery());
        }
        return Task.CompletedTask;
    }

    private void PublishLocked(string libraryId, LibraryQuery query)
    {
        var version = revisions.GetValueOrDefault(libraryId) + 1;
        revisions[libraryId] = version;
        scheduler.TryEnqueue(() =>
        {
            lock (gate)
            {
                if (disposed || revisions.GetValueOrDefault(libraryId) != version) return;
                Changed?.Invoke(this, new LibraryPreferenceChangedEventArgs(libraryId, query));
            }
        });
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            preferences.Clear();
            revisions.Clear();
            Changed = null;
        }
    }

    private static void ValidateLibraryId(string libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || libraryId.Length > 256)
            throw new AppException(new AppError(AppErrorKind.Contract, "demo.library_id", "资料库标识无效。", false));
    }
}
