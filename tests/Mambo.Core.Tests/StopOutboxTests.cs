using System.Net;
using System.Text.Json;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class StopOutboxTests
{
    [Fact]
    public async Task QueuePersistsBeforeReturningDeduplicatesAndNeverStoresCredentials()
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        using var api = Api((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var outbox = new StopOutbox(sandbox.Paths, api);
        var record = Record(account);
        await outbox.QueueAsync(record, TestContext.Current.CancellationToken);
        await outbox.QueueAsync(record with { PositionTicks = 200 }, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(sandbox.Paths.Outbox, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("records").GetArrayLength());
        Assert.DoesNotContain(account.Secret.AccessToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(document.RootElement.GetProperty("records").EnumerateArray().SelectMany(row => row.EnumerateObject()),
            property => property.Name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("mediaTitle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("example.invalid", record.ToString(), StringComparison.Ordinal);
        using var restored = new StopOutbox(sandbox.Paths, api);
        Assert.Equal(record.IdempotencyId, Assert.Single(restored.Snapshot).IdempotencyId);
        Assert.Equal(record.PositionTicks, restored.Snapshot[0].PositionTicks);
    }

    [Fact]
    public async Task OfflineStopRetainsActualPlaybackStateAcrossReloadAndAuthenticatedReplay()
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        var record = Record(account) with
        {
            PositionTicks = 987654321, MediaSourceId = Guid.NewGuid().ToString("N"),
            PlaySessionId = Guid.NewGuid().ToString("N"), LiveStreamId = Guid.NewGuid().ToString("N"),
            PlaybackRate = 1.75, PlayMethod = "Transcode", CanSeek = false, IsPaused = true, IsMuted = true,
            VolumeLevel = 36, PlaylistIndex = 2, PlaylistLength = 5, AudioStreamIndex = 7, SubtitleStreamIndex = 11,
        };
        using (var offlineApi = Api((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        {
            await using var offline = new StopOutbox(sandbox.Paths, offlineApi);
            await offline.QueueAsync(record, TestContext.Current.CancellationToken);
            await offline.FlushAsync(account, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(1, Assert.Single(offline.Snapshot).AttemptCount);
        }
        using var authenticated = new AccountSession(account.Secret with { AccessToken = Guid.NewGuid().ToString("N") });
        var bodies = new List<JsonElement>();
        using var onlineApi = Api(async (request, token) =>
        {
            Assert.Equal(authenticated.Secret.AccessToken, Assert.Single(request.Headers.GetValues("X-Emby-Token")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            bodies.Add(body.RootElement.Clone());
            return new(HttpStatusCode.OK);
        });
        await using var restored = new StopOutbox(sandbox.Paths, onlineApi);
        var saved = Assert.Single(restored.Snapshot);
        Assert.Equal(record.IdempotencyId, saved.IdempotencyId);
        Assert.Equal(record.PlaybackRate, saved.PlaybackRate);
        Assert.Equal(record.PlayMethod, saved.PlayMethod);
        await restored.FlushAsync(authenticated, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var wire = Assert.Single(bodies);
        Assert.Equal(record.ItemId, wire.GetProperty("ItemId").GetString());
        Assert.Equal(record.MediaSourceId, wire.GetProperty("MediaSourceId").GetString());
        Assert.Equal(record.PlaySessionId, wire.GetProperty("PlaySessionId").GetString());
        Assert.Equal(record.LiveStreamId, wire.GetProperty("LiveStreamId").GetString());
        Assert.Equal(record.PlaybackStartTimeTicks, wire.GetProperty("PlaybackStartTimeTicks").GetInt64());
        Assert.Equal(record.PositionTicks, wire.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(record.PlaybackRate, wire.GetProperty("PlaybackRate").GetDouble());
        Assert.Equal(record.PlayMethod, wire.GetProperty("PlayMethod").GetString());
        Assert.Equal(record.CanSeek, wire.GetProperty("CanSeek").GetBoolean());
        Assert.Equal(record.IsPaused, wire.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(record.IsMuted, wire.GetProperty("IsMuted").GetBoolean());
        Assert.Equal(record.VolumeLevel, wire.GetProperty("VolumeLevel").GetDouble());
        Assert.Equal(record.PlaylistIndex, wire.GetProperty("PlaylistIndex").GetInt32());
        Assert.Equal(record.PlaylistLength, wire.GetProperty("PlaylistLength").GetInt32());
        Assert.Equal(record.AudioStreamIndex, wire.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(record.SubtitleStreamIndex, wire.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.False(wire.GetProperty("Failed").GetBoolean());
        Assert.Empty(restored.Snapshot);
        var finalJson = await File.ReadAllTextAsync(sandbox.Paths.Outbox, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(account.Secret.AccessToken, finalJson, StringComparison.Ordinal);
        Assert.DoesNotContain(authenticated.Secret.AccessToken, finalJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptNullRecordProducesSafePersistenceErrorAndPreservesOriginalFile()
    {
        using var sandbox = new Sandbox();
        using var api = Api((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        const string corrupt = "{\"version\":1,\"records\":[null]}";
        await File.WriteAllTextAsync(sandbox.Paths.Outbox, corrupt, TestContext.Current.CancellationToken);
        var failure = Assert.Throws<AppException>(() => new StopOutbox(sandbox.Paths, api));
        Assert.Equal(AppErrorKind.Persistence, failure.Error.Kind);
        Assert.DoesNotContain("example.invalid", failure.Message, StringComparison.Ordinal);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(sandbox.Paths.Outbox, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AtomicQueuePublicationLeavesOpenReaderOnItsOriginalSnapshot()
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        using var api = Api((_, _) => throw new InvalidOperationException("入队不应发送网络请求。"));
        using var outbox = new StopOutbox(sandbox.Paths, api);
        await outbox.QueueAsync(Record(account), TestContext.Current.CancellationToken);
        await using var oldReader = new FileStream(sandbox.Paths.Outbox, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        try { await outbox.QueueAsync(Record(account) with { ItemId = "second-item" }, TestContext.Current.CancellationToken); }
        catch (AppException failure) { Assert.Fail($"停止记录原子替换失败，安全 HRESULT：{failure.Error.DiagnosticId}"); }
        using var original = await JsonDocument.ParseAsync(oldReader, cancellationToken: TestContext.Current.CancellationToken);
        using var current = JsonDocument.Parse(await File.ReadAllTextAsync(sandbox.Paths.Outbox, TestContext.Current.CancellationToken));
        Assert.Equal(1, original.RootElement.GetProperty("records").GetArrayLength());
        Assert.Equal(2, current.RootElement.GetProperty("records").GetArrayLength());
        Assert.Equal(2, outbox.Snapshot.Length);
    }

    [Fact]
    public async Task FlushOnlySendsExactScopeAndPersistsAttemptBeforeHttp()
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        var sent = new List<string>();
        using var api = Api(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            var item = body.RootElement.GetProperty("ItemId").GetString()!;
            sent.Add(item);
            Assert.False(body.RootElement.GetProperty("Failed").GetBoolean());
            Assert.True(request.Headers.Contains("X-Emby-Token"));
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(sandbox.Paths.Outbox, token));
            var row = persisted.RootElement.GetProperty("records").EnumerateArray().Single(record => record.GetProperty("itemId").GetString() == item);
            Assert.Equal(1, row.GetProperty("attemptCount").GetInt32());
            Assert.Equal(JsonValueKind.String, row.GetProperty("lastAttemptAt").ValueKind);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var outbox = new StopOutbox(sandbox.Paths, api);
        var matching = Record(account) with { ItemId = "matching" };
        await outbox.QueueAsync(Record(account) with { ServerId = "other-server", ItemId = "wrong-server" }, TestContext.Current.CancellationToken);
        await outbox.QueueAsync(Record(account) with { UserId = "other-user", ItemId = "wrong-user" }, TestContext.Current.CancellationToken);
        await outbox.QueueAsync(Record(account) with { ServerBase = "https://other.example.invalid", ItemId = "wrong-address" }, TestContext.Current.CancellationToken);
        await outbox.QueueAsync(matching, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(null, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Empty(sent);
        await outbox.FlushAsync(account, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Collection(sent, item => Assert.Equal("matching", item));
        Assert.Equal(3, outbox.Snapshot.Length);
        Assert.All(outbox.Snapshot, record => Assert.Equal(0, record.AttemptCount));
    }

    [Fact]
    public async Task RoundAcknowledgesSuccessOnlyAtEndAndRetryableFailureStopsLaterRecords()
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        var calls = 0;
        using var api = Api(async (_, token) =>
        {
            calls++;
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(sandbox.Paths.Outbox, token));
            // Even the successful first report is recoverable while the second request is in flight.
            Assert.Equal(3, persisted.RootElement.GetProperty("records").GetArrayLength());
            return new HttpResponseMessage(calls == 1 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        });
        using var outbox = new StopOutbox(sandbox.Paths, api);
        var first = Record(account);
        var second = Record(account);
        var third = Record(account);
        foreach (var record in new[] { first, second, third }) await outbox.QueueAsync(record, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(account, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(new[] { second.IdempotencyId, third.IdempotencyId }, outbox.Snapshot.Select(record => record.IdempotencyId));
        Assert.Equal(1, outbox.Snapshot[0].AttemptCount);
        Assert.Equal(0, outbox.Snapshot[1].AttemptCount);
        using var restored = new StopOutbox(sandbox.Paths, api);
        Assert.Equal(2, restored.Snapshot.Length);
    }

    [Theory]
    [InlineData(401, true, "awaiting_reauth")]
    [InlineData(403, true, "awaiting_reauth")]
    [InlineData(408, true, "pending")]
    [InlineData(425, true, "pending")]
    [InlineData(429, true, "pending")]
    [InlineData(500, true, "pending")]
    [InlineData(400, false, null)]
    [InlineData(404, false, null)]
    public async Task FailureClassificationKeepsRetryableReportsAndDropsDeadLetters(int status, bool retained, string? state)
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        using var api = Api((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var dead = new List<AppError>();
        using var outbox = new StopOutbox(sandbox.Paths, api, deadLetterLog: dead.Add);
        await outbox.QueueAsync(Record(account), TestContext.Current.CancellationToken);
        await outbox.FlushAsync(account, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(retained ? 1 : 0, outbox.Snapshot.Length);
        if (retained) Assert.Equal(state, outbox.Snapshot[0].State);
        Assert.Equal(retained ? 0 : 1, dead.Count);
    }

    [Fact]
    public async Task AuthenticationFailureWaitsForNewSessionThenReplaysPendingReport()
    {
        using var sandbox = new Sandbox();
        using var oldAccount = Account();
        var calls = 0;
        using var api = Api((_, _) => Task.FromResult(new HttpResponseMessage(++calls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)));
        using var outbox = new StopOutbox(sandbox.Paths, api);
        await outbox.QueueAsync(Record(oldAccount), TestContext.Current.CancellationToken);
        await outbox.FlushAsync(oldAccount, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await outbox.FlushAsync(oldAccount, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal("awaiting_reauth", outbox.Snapshot[0].State);
        using var authenticated = new AccountSession(oldAccount.Secret with { AccessToken = Guid.NewGuid().ToString("N") });
        await outbox.FlushAsync(authenticated, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Empty(outbox.Snapshot);
    }

    [Fact]
    public async Task ConcurrentFlushIsIgnoredAndBudgetCancelsNetworkWithRecordRetained()
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var api = Api(async (_, token) =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var outbox = new StopOutbox(sandbox.Paths, api, clock);
        await outbox.QueueAsync(Record(account), TestContext.Current.CancellationToken);
        var active = outbox.FlushAsync(account, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await outbox.FlushAsync(account, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(3));
        await active;
        Assert.Equal(1, Assert.Single(outbox.Snapshot).AttemptCount);
    }

    [Fact]
    public async Task RetryTimerSelectsCurrentAccountOnlyAtFiveMinuteIntervals()
    {
        using var sandbox = new Sandbox();
        using var account = Account();
        var clock = new FakeTimeProvider();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var api = Api((_, _) =>
        {
            Interlocked.Increment(ref calls); sent.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        await using var outbox = new StopOutbox(sandbox.Paths, api, clock);
        await outbox.QueueAsync(Record(account), TestContext.Current.CancellationToken);
        outbox.StartRetry(() => account);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Timer delivery can still be completing its final disk acknowledgment when HTTP returns.
        await outbox.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    private static AccountSession Account() => new(new SessionSecret("https://media.example.invalid", "server", "user", "演示", Guid.NewGuid().ToString("N")));
    private static StopReportRecord Record(AccountSession account) => new(account.Secret.ServerId, account.Address.Uri.AbsoluteUri,
        account.Secret.UserId, Guid.NewGuid().ToString("N"), "item", 100, 1000);
    private static EmbyApi Api(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(Guid.NewGuid(), new Handler(send));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Sandbox : IDisposable
    {
        private readonly string root = Path.Combine(FindRoot(), "artifacts", "tests", "outbox-" + Guid.NewGuid().ToString("N"));
        public AppPaths Paths { get; }
        public Sandbox() => Paths = new AppPaths(root);
        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mambo.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? AppContext.BaseDirectory;
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.Combine(FindRoot(), "artifacts", "tests")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录超出 artifacts/tests。");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
