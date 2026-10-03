using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Session;

namespace Mambo.Core.Data;

public sealed record QueryKey(string Scope, string Kind, string Args = "")
{
    public string LocalKey => Kind + "|" + Args;
    public bool Persist => Kind is "libraries" or "hero" or "continue" or "latest" or "library-first";
}

/// <summary>账号隔离的共享 SWR；最后一个观察释放后取消请求，写入与删除按作用域串行。</summary>
public sealed class QueryCache(IUiScheduler scheduler, QueryPersistence persistence, TimeProvider? timeProvider = null,
    Func<string, Dictionary<string, QuerySnapshot>, CancellationToken, Task>? write = null) : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<string, Dictionary<string, QuerySnapshot>, CancellationToken, Task> writer = write ?? persistence.SaveAsync;
    private readonly Dictionary<QueryKey, IEntry> entries = new();
    private readonly Dictionary<string, ScopeState> scopes = new(StringComparer.Ordinal);
    private readonly HashSet<Task> writes = [];
    private bool disposed;
    private Task? disposal;
    private int restoredSnapshotCount;

    /// <summary>本进程从持久化快照成功初始化的查询数；诊断仅计数，不公开账号或数据。</summary>
    public int RestoredSnapshotCount { get { lock (gate) return restoredSnapshotCount; } }

    /// <summary>当前可见首页卡片中仍来自磁盘快照的对象数；仅按引用去重，不公开身份或内容。</summary>
    public int CountRestoredHomeItems(IEnumerable<MediaItem> visibleItems)
    {
        ArgumentNullException.ThrowIfNull(visibleItems);
        var visible = new HashSet<MediaItem>(visibleItems, ReferenceEqualityComparer.Instance);
        lock (gate)
        {
            if (disposed || visible.Count == 0) return 0;
            var restored = new HashSet<MediaItem>(ReferenceEqualityComparer.Instance);
            foreach (var pair in entries)
            {
                if (pair.Key.Kind is not ("continue" or "latest")) continue;
                foreach (var item in pair.Value.PersistedHomeItems)
                    if (visible.Contains(item)) restored.Add(item);
            }
            return restored.Count;
        }
    }

    public IQuery<T> Observe<T>(QueryKey key, AccountSession account, Func<CancellationToken, Task<T>> fetch,
        TimeSpan? staleAfter = null, bool updateFetcher = true, CancellationToken scopeToken = default)
    {
        Entry<T> entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            account.Token.ThrowIfCancellationRequested();
            var scope = GetScope(key.Scope);
            if (scope.Clearing || ReferenceEquals(scope.ClearedAccount, account)) throw new OperationCanceledException(account.Token);
            scope.Account = account;
            if (!entries.TryGetValue(key, out var found) || !ReferenceEquals(found.Account, account))
            {
                found?.Dispose();
                entry = new Entry<T>(account, fetch, clock, staleAfter ?? TimeSpan.FromSeconds(60),
                    (origin, generation, value) => SaveValue(key, origin, generation, value));
                var persistedOrigin = scope.PersistedKeys.Contains(key.LocalKey);
                if (scope.Values.TryGetValue(key.LocalKey, out var snapshot) && entry.Seed(snapshot, persistedOrigin) && persistedOrigin) restoredSnapshotCount++;
                entries[key] = entry;
            }
            else
            {
                entry = (Entry<T>)found;
                if (updateFetcher) entry.SetFetcher(fetch);
            }
        }
        return entry.Observe(scheduler, scopeToken);
    }

    public async Task<T> FetchAsync<T>(QueryKey key, AccountSession account, Func<CancellationToken, Task<T>> fetch,
        CancellationToken token, bool refresh = false, bool updateFetcher = true)
    {
        using var query = Observe(key, account, fetch, updateFetcher: updateFetcher, scopeToken: token);
        if (refresh || !query.IsInitialized) await query.RefreshAsync(token).ConfigureAwait(false);
        if (query.Error is { } error) throw new AppException(error);
        return query.Current!;
    }

    public void Invalidate(Func<QueryKey, bool> predicate)
    {
        IEntry[] targets;
        lock (gate) targets = entries.Where(pair => predicate(pair.Key)).Select(pair => pair.Value).ToArray();
        foreach (var entry in targets) entry.Invalidate();
    }

    // 兼容同步调用；应用注销和清缓存应 await 异步版本。
    public void Clear(string scope) => ClearAsync(scope).GetAwaiter().GetResult();
    public void Reset(string scope) => ResetAsync(scope).GetAwaiter().GetResult();
    public Task ClearAsync(string scope, CancellationToken cancellationToken = default) => Remove(scope, true, cancellationToken);
    public Task ResetAsync(string scope, CancellationToken cancellationToken = default) => Remove(scope, false, cancellationToken);

    private Task Remove(string name, bool clear, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var scope = GetScope(name);
            var generation = ++scope.Generation;
            scope.Values.Clear();
            scope.PersistedKeys.Clear();
            scope.Pending?.Cancel();
            scope.Pending = null;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scope.Barrier = barrier.Task;
            scope.Clearing = clear;
            var affected = entries.Where(pair => pair.Key.Scope == name).Select(pair => pair.Value).ToArray();
            if (clear)
            {
                scope.ClearedAccount = scope.Account;
                foreach (var key in entries.Keys.Where(key => key.Scope == name).ToArray()) entries.Remove(key);
                foreach (var entry in affected) entry.Dispose();
            }
            else foreach (var entry in affected) entry.Reset();
            var operation = RemoveCoreAsync(scope, generation, clear, barrier, affected);
            Track(operation);
            return operation;
        }
    }

    private async Task RemoveCoreAsync(ScopeState scope, long generation, bool clear, TaskCompletionSource barrier, IEntry[] affected)
    {
        try
        {
            // 接受删除后等待旧写入结束再删除，取消调用方不会留下旧文件。
            await scope.Io.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { lock (gate) if (scope.Generation == generation) persistence.Delete(scope.Name); }
            finally { scope.Io.Release(); }
        }
        finally
        {
            lock (gate) if (scope.Generation == generation) scope.Clearing = false;
            barrier.TrySetResult();
        }
        if (!clear)
        {
            bool current;
            lock (gate) current = !disposed && scope.Generation == generation;
            if (current) foreach (var entry in affected) entry.Invalidate();
        }
    }

    private ScopeState GetScope(string scope)
    {
        if (!scopes.TryGetValue(scope, out var state))
            scopes[scope] = state = new(scope, persistence.Load(scope));
        return state;
    }

    private void SaveValue<T>(QueryKey key, Entry<T> origin, long requestGeneration, T value)
    {
        if (!key.Persist) return;
        var snapshot = value switch
        {
            ImmutableArray<MediaLibrary> libraries => new QuerySnapshot { Kind = "libraries", Libraries = libraries, UpdatedAt = clock.GetUtcNow() },
            ImmutableArray<MediaItem> items => new QuerySnapshot { Kind = "items", Items = items, UpdatedAt = clock.GetUtcNow() },
            QueryPage<MediaItem> page => new QuerySnapshot { Kind = "page", Items = page.Items, TotalCount = page.TotalCount, HasMore = page.HasMore, NextOffset = page.NextOffset, UpdatedAt = clock.GetUtcNow() },
            _ => null,
        };
        if (snapshot is null) return;
        lock (gate)
        {
            if (disposed || !entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry, origin) || !origin.CanPublish(requestGeneration)) return;
            var scope = GetScope(key.Scope);
            scope.Values[key.LocalKey] = snapshot;
            scope.PersistedKeys.Remove(key.LocalKey);
            scope.Pending?.Cancel();
            var cancellation = new CancellationTokenSource();
            scope.Pending = cancellation;
            var token = cancellation.Token;
            Track(WriteLaterAsync(scope, scope.Generation, cancellation, token));
        }
    }

    private async Task WriteLaterAsync(ScopeState scope, long generation, CancellationTokenSource cancellation, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), clock, token).ConfigureAwait(false);
            await WriteScopeAsync(scope, generation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (AppException) { /* 落盘失败不改变已加载的读取状态。 */ }
        catch (IOException) { }
        finally
        {
            lock (gate) if (ReferenceEquals(scope.Pending, cancellation)) scope.Pending = null;
            cancellation.Dispose();
        }
    }

    private async Task WriteScopeAsync(ScopeState scope, long generation, CancellationToken token)
    {
        Task barrier;
        lock (gate)
        {
            if (disposed || scope.Generation != generation) return;
            barrier = scope.Barrier;
        }
        await barrier.WaitAsync(token).ConfigureAwait(false);
        await scope.Io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Dictionary<string, QuerySnapshot> values;
            lock (gate)
            {
                if (disposed || scope.Generation != generation) return;
                values = new(scope.Values, StringComparer.Ordinal);
            }
            await writer(scope.Name, values, token).ConfigureAwait(false);
        }
        finally { scope.Io.Release(); }
    }

    public Task FlushAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var saved = scopes.Values.Where(scope => scope.Values.Count != 0).Select(scope => (scope, scope.Generation)).ToArray();
            var operation = FlushCoreAsync(saved, token);
            Track(operation);
            return operation;
        }
    }
    private async Task FlushCoreAsync((ScopeState Scope, long Generation)[] saved, CancellationToken token)
    {
        foreach (var item in saved) await WriteScopeAsync(item.Scope, item.Generation, token).ConfigureAwait(false);
    }
    private void Track(Task task) { writes.Add(task); _ = ForgetAsync(task); }
    private async Task ForgetAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or AppException or IOException or UnauthorizedAccessException) { }
        finally { lock (gate) writes.Remove(task); }
    }

    public void Dispose()
    {
        IEntry[] all;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; all = entries.Values.ToArray(); entries.Clear();
            foreach (var scope in scopes.Values) { scope.Pending?.Cancel(); scope.Pending = null; }
        }
        foreach (var entry in all) entry.Dispose();
    }
    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (gate)
        {
            disposal ??= DrainAsync(writes.ToArray(), scopes.Values.ToArray());
            return new(disposal);
        }
    }
    private static async Task DrainAsync(Task[] pending, ScopeState[] ownedScopes)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or AppException or IOException or UnauthorizedAccessException) { }
        finally { foreach (var scope in ownedScopes) scope.Dispose(); }
    }

    private sealed class ScopeState(string name, Dictionary<string, QuerySnapshot> values) : IDisposable
    {
        public string Name { get; } = name;
        public Dictionary<string, QuerySnapshot> Values { get; } = values;
        public HashSet<string> PersistedKeys { get; } = new(values.Keys, StringComparer.Ordinal);
        public SemaphoreSlim Io { get; } = new(1, 1);
        public long Generation { get; set; }
        public CancellationTokenSource? Pending { get; set; }
        public Task Barrier { get; set; } = Task.CompletedTask;
        public AccountSession? Account { get; set; }
        public AccountSession? ClearedAccount { get; set; }
        public bool Clearing { get; set; }
        public void Dispose() => Io.Dispose();
    }

    private interface IEntry : IDisposable
    {
        AccountSession Account { get; }
        ImmutableArray<MediaItem> PersistedHomeItems { get; }
        void Invalidate();
        void Reset();
    }
    private sealed class Entry<T>(AccountSession account, Func<CancellationToken, Task<T>> fetch, TimeProvider clock,
        TimeSpan staleAfter, Action<Entry<T>, long, T> save) : IEntry
    {
        private readonly object sync = new();
        private readonly List<Observation> observers = [];
        private readonly CancellationToken accountToken = account.Token;
        private T? value;
        private AppError? error;
        private bool initialized, ended, persistedOrigin;
        private DateTimeOffset updated;
        private Flight? active;
        private long generation;
        private Func<CancellationToken, Task<T>> loader = fetch;
        public AccountSession Account => account;
        public ImmutableArray<MediaItem> PersistedHomeItems
        {
            get
            {
                lock (sync)
                    return !ended && !accountToken.IsCancellationRequested && persistedOrigin && value is ImmutableArray<MediaItem> items
                        ? items : [];
            }
        }
        public void SetFetcher(Func<CancellationToken, Task<T>> next) { lock (sync) loader = next; }
        public bool CanPublish(long expected)
        { lock (sync) return !ended && active is { } operation && operation.Generation == expected && !operation.Token.IsCancellationRequested; }
        public bool Seed(QuerySnapshot snapshot, bool persisted)
        {
            object? seed = snapshot.Kind switch { "libraries" => snapshot.Libraries, "items" => snapshot.Items, "page" => new QueryPage<MediaItem>(snapshot.Items, snapshot.TotalCount, snapshot.HasMore) { NextOffset = snapshot.NextOffset }, _ => null };
            if (seed is not T typed) return false;
            lock (sync) { value = typed; initialized = true; updated = snapshot.UpdatedAt; persistedOrigin = persisted; }
            return true;
        }
        public IQuery<T> Observe(IUiScheduler scheduler, CancellationToken scope)
        {
            Observation query;
            bool refresh;
            lock (sync)
            {
                if (ended) throw new OperationCanceledException(accountToken);
                query = new Observation(this, scheduler, scope, accountToken);
                observers.Add(query);
                refresh = !initialized || clock.GetUtcNow() - updated >= staleAfter;
            }
            query.Activate();
            if (refresh) _ = Start(query);
            return query;
        }
        private static async Task Start(Observation query)
        { try { await query.RefreshAsync().ConfigureAwait(false); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } }
        public Task RefreshAsync(CancellationToken token)
        {
            Flight operation;
            var start = false;
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (ended || accountToken.IsCancellationRequested) throw new OperationCanceledException(accountToken);
                if (active is null || active.Token.IsCancellationRequested)
                {
                    StopLocked();
                    active = new(++generation, loader, accountToken);
                    start = true;
                }
                operation = active;
            }
            if (start) { Notify(); _ = LoadAsync(operation); }
            return operation.Completion.Task.WaitAsync(token);
        }
        private async Task LoadAsync(Flight operation)
        {
            var cancelled = false;
            try
            {
                var result = await operation.Loader(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (ended || !ReferenceEquals(active, operation) || operation.Token.IsCancellationRequested)
                        throw new OperationCanceledException(operation.Token);
                    value = result; initialized = true; error = null; updated = clock.GetUtcNow(); persistedOrigin = false;
                }
                save(this, operation.Generation, result);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested || accountToken.IsCancellationRequested)
            { cancelled = true; }
            catch (Exception exception)
            {
                lock (sync)
                    if (!ended && ReferenceEquals(active, operation) && !operation.Token.IsCancellationRequested)
                        error = exception is AppException app ? app.Error : new(AppErrorKind.Network, ErrorCodes.NetworkUnavailable, "数据暂时无法加载，请重试。", true);
                cancelled = operation.Token.IsCancellationRequested;
            }
            finally
            {
                bool current;
                lock (sync) { current = ReferenceEquals(active, operation); if (current) active = null; }
                if (current) Notify();
                if (cancelled) operation.Completion.TrySetCanceled(operation.Token); else operation.Completion.TrySetResult();
                operation.Dispose();
            }
        }
        private void StopLocked()
        {
            if (active is not { } operation) return;
            active = null;
            operation.Cancellation.Cancel();
            operation.Completion.TrySetCanceled(operation.Token);
        }
        private void Notify()
        {
            Observation[] all;
            lock (sync) all = observers.ToArray();
            foreach (var observer in all) observer.Notify();
        }
        private void Release(Observation query)
        { lock (sync) { observers.Remove(query); if (observers.Count == 0) StopLocked(); } }
        public void Invalidate()
        {
            Observation[] all;
            lock (sync) { if (ended) return; updated = DateTimeOffset.MinValue; all = observers.ToArray(); }
            foreach (var observer in all) _ = Start(observer);
        }
        public void Reset()
        {
            lock (sync) { StopLocked(); value = default; initialized = false; error = null; updated = DateTimeOffset.MinValue; persistedOrigin = false; }
            Notify();
        }
        public void Dispose()
        {
            Observation[] all;
            lock (sync) { if (ended) return; ended = true; persistedOrigin = false; StopLocked(); all = observers.ToArray(); }
            foreach (var observer in all) observer.End();
        }
        private sealed class Flight : IDisposable
        {
            public long Generation { get; }
            public CancellationTokenSource Cancellation { get; }
            public CancellationToken Token { get; }
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Func<CancellationToken, Task<T>> Loader { get; }
            public Flight(long generation, Func<CancellationToken, Task<T>> loader, CancellationToken account)
            { Generation = generation; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(account); Token = Cancellation.Token; Loader = loader; }
            public void Dispose() => Cancellation.Dispose();
        }
        private sealed class Observation : IQuery<T>
        {
            private readonly Entry<T> entry;
            private readonly IUiScheduler scheduler;
            private readonly CancellationTokenSource lifetime;
            private readonly CancellationToken token;
            private CancellationTokenRegistration registration;
            private int disposed;
            private int notificationQueued;
            public Observation(Entry<T> entry, IUiScheduler scheduler, CancellationToken scope, CancellationToken account)
            { this.entry = entry; this.scheduler = scheduler; lifetime = CancellationTokenSource.CreateLinkedTokenSource(scope, account); token = lifetime.Token; }
            public void Activate() => registration = token.Register(() => entry.Release(this));
            public T? Current { get { lock (entry.sync) return entry.value; } }
            public bool IsInitialized { get { lock (entry.sync) return entry.initialized; } }
            public AppError? Error { get { lock (entry.sync) return entry.error; } }
            public bool IsRefreshing { get { lock (entry.sync) return entry.active is not null; } }
            public event EventHandler? Updated;
            public Task RefreshAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                token.ThrowIfCancellationRequested();
                var work = entry.RefreshAsync(token);
                return cancellationToken.CanBeCanceled ? work.WaitAsync(cancellationToken) : work;
            }
            public void Notify()
            {
                if (Volatile.Read(ref disposed) != 0 || token.IsCancellationRequested || Interlocked.Exchange(ref notificationQueued, 1) != 0) return;
                if (!scheduler.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref notificationQueued, 0);
                    if (Volatile.Read(ref disposed) == 0 && !token.IsCancellationRequested) Updated?.Invoke(this, EventArgs.Empty);
                })) Interlocked.Exchange(ref notificationQueued, 0);
            }
            public void End() { if (Volatile.Read(ref disposed) == 0) { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } } }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                Updated = null; lifetime.Cancel(); registration.Dispose(); entry.Release(this); lifetime.Dispose();
            }
        }
    }
}
