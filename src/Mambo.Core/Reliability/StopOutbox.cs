using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Session;

namespace Mambo.Core.Reliability;

/// <summary>Only delivery identifiers and account scope are persisted; no token or media title.</summary>
public sealed record StopReportRecord(string ServerId, string ServerBase, string UserId, string IdempotencyId,
    string ItemId, long PlaybackStartTimeTicks, long PositionTicks)
{
    public string? MediaSourceId { get; init; }
    public string? LiveStreamId { get; init; }
    public string? PlaySessionId { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public int AttemptCount { get; init; }
    public string State { get; init; } = "pending";
    public override string ToString() => "StopReportRecord { <redacted> }";
}

internal sealed record StopReportDocument(int Version, ImmutableArray<StopReportRecord> Records);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StopReportDocument))]
internal sealed partial class StopOutboxJsonContext : JsonSerializerContext;

/// <summary>Durable, at-least-once stopped delivery, serialized separately from metadata traffic.</summary>
public sealed class StopOutbox : IDisposable, IAsyncDisposable
{
    private readonly string path;
    private readonly EmbyApi api;
    private readonly TimeProvider clock;
    private readonly Action<AppError>? deadLetterLog;
    private readonly object gate = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly SemaphoreSlim flushes = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<string, WeakReference<AccountSession>> awaitingReauthentication = new(StringComparer.Ordinal);
    private ImmutableArray<StopReportRecord> records;
    private ITimer? retryTimer;
    private Task? cleanupTask;
    private bool disposed;

    public StopOutbox(AppPaths paths, EmbyApi api, TimeProvider? timeProvider = null, Action<AppError>? deadLetterLog = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(api);
        path = paths.Outbox;
        this.api = api;
        clock = timeProvider ?? TimeProvider.System;
        this.deadLetterLog = deadLetterLog;
        records = Read();
    }

    public ImmutableArray<StopReportRecord> Snapshot { get { lock (gate) return records; } }

    public async Task QueueAsync(StopReportRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        var queued = record with { ServerBase = ServerAddress.Normalize(record.ServerBase).Uri.AbsoluteUri,
            QueuedAt = clock.GetUtcNow(), AttemptCount = 0, LastAttemptAt = null, State = "pending" };
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<StopReportRecord> updated;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (records.Any(existing => existing.IdempotencyId == queued.IdempotencyId)) return;
                updated = records.Add(queued);
            }
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            lock (gate) records = updated;
        }
        finally { mutations.Release(); }
    }

    public async Task FlushAsync(AccountSession? account, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (account is null) return;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        CancellationToken accountToken;
        try { accountToken = account.Token; }
        catch (ObjectDisposedException) { return; }
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        if (!await flushes.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        var acknowledged = new HashSet<string>(StringComparer.Ordinal);
        using var deadline = new CancellationTokenSource(budget, clock);
        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accountToken, shutdown.Token, deadline.Token);
        try
        {
            var pending = Snapshot.Where(record => Matches(record, account)).ToArray();
            foreach (var pendingRecord in pending)
            {
                round.Token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (awaitingReauthentication.TryGetValue(pendingRecord.IdempotencyId, out var previous) &&
                        previous.TryGetTarget(out var failedAccount) && ReferenceEquals(failedAccount, account)) break;
                }
                var record = await RecordAttemptAsync(pendingRecord.IdempotencyId, round.Token).ConfigureAwait(false);
                if (record is null) continue;
                try
                {
                    await api.ReportAsync(account, "Stopped", new PlaybackReport
                    {
                        ItemId = record.ItemId, MediaSourceId = record.MediaSourceId, LiveStreamId = record.LiveStreamId,
                        PlaySessionId = record.PlaySessionId, PlaybackStartTimeTicks = record.PlaybackStartTimeTicks,
                        PositionTicks = record.PositionTicks, Failed = false,
                    }, round.Token).ConfigureAwait(false);
                    acknowledged.Add(record.IdempotencyId);
                }
                catch (AppException error)
                {
                    if (error.Error.Status is 401 or 403 || error.Error.Kind == AppErrorKind.Auth)
                    {
                        await MarkAwaitingAsync(record.IdempotencyId, account, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    if (error.Error.Retryable) break;
                    acknowledged.Add(record.IdempotencyId);
                    deadLetterLog?.Invoke(error.Error);
                }
            }
        }
        catch (OperationCanceledException) when (round.IsCancellationRequested) { }
        finally
        {
            try
            {
                // Acknowledge only after the whole round; interruption before this write replays successes.
                if (acknowledged.Count != 0) await AcknowledgeAsync(acknowledged, CancellationToken.None).ConfigureAwait(false);
            }
            finally { flushes.Release(); }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>One low-frequency timer. Session restore/login/logout additionally invoke FlushAsync directly.</summary>
    public void StartRetry(Func<AccountSession?> currentAccount)
    {
        ArgumentNullException.ThrowIfNull(currentAccount);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            retryTimer?.Dispose();
            retryTimer = clock.CreateTimer(state => { _ = RetryAsync(currentAccount); }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
    }

    private async Task RetryAsync(Func<AccountSession?> currentAccount)
    {
        try { await FlushAsync(currentAccount(), TimeSpan.FromSeconds(3), shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
        catch (AppException error) { deadLetterLog?.Invoke(error.Error); }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        await flushes.WaitAsync(cancellationToken).ConfigureAwait(false);
        flushes.Release();
    }

    private async Task<StopReportRecord?> RecordAttemptAsync(string id, CancellationToken cancellationToken)
    {
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<StopReportRecord> updated;
            StopReportRecord attempted;
            lock (gate)
            {
                var index = IndexOf(id);
                if (index < 0) return null;
                attempted = records[index] with { LastAttemptAt = clock.GetUtcNow(),
                    AttemptCount = records[index].AttemptCount + 1, State = "pending" };
                updated = records.SetItem(index, attempted);
            }
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            lock (gate) records = updated;
            return attempted;
        }
        finally { mutations.Release(); }
    }

    private async Task MarkAwaitingAsync(string id, AccountSession account, CancellationToken cancellationToken)
    {
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<StopReportRecord> updated;
            lock (gate)
            {
                var index = IndexOf(id);
                if (index < 0) return;
                updated = records.SetItem(index, records[index] with { State = "awaiting_reauth" });
            }
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                records = updated;
                awaitingReauthentication[id] = new WeakReference<AccountSession>(account);
            }
        }
        finally { mutations.Release(); }
    }

    private async Task AcknowledgeAsync(HashSet<string> ids, CancellationToken cancellationToken)
    {
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<StopReportRecord> updated;
            lock (gate) updated = records.Where(record => !ids.Contains(record.IdempotencyId)).ToImmutableArray();
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                records = updated;
                foreach (var id in ids) awaitingReauthentication.Remove(id);
            }
        }
        finally { mutations.Release(); }
    }

    private int IndexOf(string id)
    {
        for (var index = 0; index < records.Length; index++) if (records[index].IdempotencyId == id) return index;
        return -1;
    }

    private Task SaveAsync(ImmutableArray<StopReportRecord> values, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new StopReportDocument(1, values),
            StopOutboxJsonContext.Default.StopReportDocument), cancellationToken: cancellationToken);

    private ImmutableArray<StopReportRecord> Read()
    {
        try
        {
            var bytes = AtomicFile.Read(path, 64 * 1024 * 1024);
            if (bytes is null) return [];
            var document = JsonSerializer.Deserialize(bytes, StopOutboxJsonContext.Default.StopReportDocument);
            if (document is not { Version: 1 } || document.Records.IsDefault)
                throw new JsonException();
            foreach (var record in document.Records)
            {
                if (record is null) throw new JsonException();
                Validate(record);
            }
            return document.Records.DistinctBy(record => record.IdempotencyId, StringComparer.Ordinal).ToImmutableArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or AppException)
        { throw new AppException(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed, "停止记录文件无法读取，已保留原文件，请检查本地数据。", true)); }
    }

    private static bool Matches(StopReportRecord record, AccountSession account) =>
        record.ServerId == account.Secret.ServerId && record.UserId == account.Secret.UserId &&
        record.ServerBase == account.Address.Uri.AbsoluteUri;

    private static void Validate(StopReportRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ServerId) || string.IsNullOrWhiteSpace(record.UserId) ||
            string.IsNullOrWhiteSpace(record.ItemId) || !Guid.TryParse(record.IdempotencyId, out _) ||
            record.PlaybackStartTimeTicks < 0 || record.PositionTicks < 0 || record.AttemptCount < 0 ||
            record.State is not ("pending" or "awaiting_reauth"))
            throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "停止记录字段无效。", false));
        _ = ServerAddress.Normalize(record.ServerBase);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            retryTimer?.Dispose();
            shutdown.Cancel();
            cleanupTask = CleanupAsync();
        }
    }

    private async Task CleanupAsync()
    {
        await flushes.WaitAsync().ConfigureAwait(false);
        await mutations.WaitAsync().ConfigureAwait(false);
        mutations.Release();
        flushes.Release();
        mutations.Dispose();
        flushes.Dispose();
        shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task cleanup;
        lock (gate) cleanup = cleanupTask!;
        await cleanup.ConfigureAwait(false);
    }
}
