using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Session;

namespace Mambo.Core.Persistence;

/// <summary>通过设置存储持久化当前账号各资料库的排序与筛选。</summary>
public sealed class LibraryPreferences : ILibraryPreferences, IDisposable
{
    private readonly SettingsStore settings;
    private readonly AccountContext accounts;
    private readonly IUiScheduler scheduler;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> observedLibraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> revisions = new(StringComparer.Ordinal);
    private bool disposed;
    private bool lifetimeCancelled;
    private int activeWrites;
    private long accountEpoch;

    public LibraryPreferences(SettingsStore settings, AccountContext accounts, IUiScheduler scheduler)
    {
        this.settings = settings;
        this.accounts = accounts;
        this.scheduler = scheduler;
        accounts.Changed += OnAccountChanged;
    }

    public event EventHandler<LibraryPreferenceChangedEventArgs>? Changed;

    public LibraryQuery Get(string libraryId)
    {
        ValidateId(libraryId);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            observedLibraries.Add(libraryId);
        }
        var account = RequireAccount();
        return settings.Preference(Key(account, libraryId));
    }

    public Task SetAsync(string libraryId, LibraryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        return SaveAsync(libraryId, query, cancellationToken);
    }

    public Task ResetAsync(string libraryId, CancellationToken cancellationToken = default) => SaveAsync(libraryId, null, cancellationToken);

    private async Task SaveAsync(string libraryId, LibraryQuery? query, CancellationToken token)
    {
        ValidateId(libraryId);
        var account = RequireAccount();
        CancellationTokenSource linked;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, account.Token);
            activeWrites++;
            observedLibraries.Add(libraryId);
        }
        try
        {
            await settings.SavePreferenceAsync(Key(account, libraryId), query, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            QueueChanged(libraryId, query ?? new LibraryQuery(), account);
        }
        finally
        {
            linked.Dispose();
            lock (gate)
            {
                activeWrites--;
                ReleaseLifetimeIfIdleLocked();
            }
        }
    }

    private void OnAccountChanged()
    {
        string[] libraries;
        lock (gate)
        {
            if (disposed) return;
            accountEpoch++;
            revisions.Clear();
            libraries = [.. observedLibraries];
        }
        var account = accounts.Current;
        foreach (var libraryId in libraries)
            QueueChanged(libraryId, account is null ? new LibraryQuery() : settings.Preference(Key(account, libraryId)), account);
    }

    private void QueueChanged(string libraryId, LibraryQuery query, AccountSession? account)
    {
        long version;
        long epoch;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(accounts.Current, account)) return;
            version = revisions.GetValueOrDefault(libraryId) + 1;
            revisions[libraryId] = version;
            epoch = accountEpoch;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (gate) { if (disposed) return; }
            scheduler.TryEnqueue(() =>
            {
                EventHandler<LibraryPreferenceChangedEventArgs>? handler;
                lock (gate)
                {
                    if (disposed || epoch != accountEpoch || revisions.GetValueOrDefault(libraryId) != version ||
                        !ReferenceEquals(accounts.Current, account)) return;
                    handler = Changed;
                }
                handler?.Invoke(this, new LibraryPreferenceChangedEventArgs(libraryId, query));
            });
        });
    }

    private AccountSession RequireAccount() => accounts.Current ?? throw new AppException(new(AppErrorKind.Auth,
        ErrorCodes.NotLoggedIn, "请先连接服务器。", false));

    private static string Key(AccountSession account, string libraryId) => account.Scope + "|" + libraryId;

    private static void ValidateId(string libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || libraryId is "." or ".." || Encoding.UTF8.GetByteCount(libraryId) > 256 || libraryId.Any(char.IsControl))
            throw Invalid("资料库标识无效。");
    }

    private static void ValidateQuery(LibraryQuery query)
    {
        if (!Enum.IsDefined(query.Sort) || !Enum.IsDefined(query.Direction) || query.Genres.IsDefault || query.Years.IsDefault || query.OfficialRatings.IsDefault ||
            query.Genres.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
            query.OfficialRatings.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
            query.Years.Any(year => year is < 1 or > 9999)) throw Invalid("资料库筛选无效。");
    }

    private static AppException Invalid(string text) => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, text, false));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Changed = null;
            observedLibraries.Clear();
            revisions.Clear();
        }
        accounts.Changed -= OnAccountChanged;
        try { lifetime.Cancel(); }
        finally
        {
            lock (gate)
            {
                lifetimeCancelled = true;
                ReleaseLifetimeIfIdleLocked();
            }
        }
    }

    private void ReleaseLifetimeIfIdleLocked()
    {
        if (disposed && lifetimeCancelled && activeWrites == 0) lifetime.Dispose();
    }
}
