using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Mambo.Player.External;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Player.Tests;

public sealed class ExternalPlaybackSessionTests
{
    private static readonly string[] ExpectedLifecycle = ["Playing", "Stopped", "Playing", "Stopped"];

    [Fact]
    public async Task RealSessionUsesIpcForFallbackControlsAutomaticNextAndSeasonEndWithOrderedReports()
    {
        await using var host = await Host.CreateAsync();
        var nativeId = 0L;
        var activeId = 0L;
        var appendedId = 0L;
        var loads = 0;
        var removals = 0;
        host.Server.Handler = async request =>
        {
            var command = request.GetProperty("command");
            var name = command[0].GetString();
            if (name == "playlist-remove") Interlocked.Increment(ref removals);
            if (name == "loadfile")
            {
                var id = ++nativeId;
                await host.Server.ReplyAsync(request, "{\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
                if (command[2].GetString() == "append") { Interlocked.Exchange(ref appendedId, id); return true; }
                activeId = id;
                await StartAsync(host.Server, id, confirm: ++loads > 1);
                if (loads == 1) await host.Server.SendAsync("{\"event\":\"end-file\",\"reason\":\"error\",\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
                return true;
            }
            if (name == "playlist-next")
            {
                await host.Server.ReplyAsync(request);
                await EndAsync(host.Server, activeId);
                activeId = Interlocked.Read(ref appendedId);
                await StartAsync(host.Server, activeId);
                return true;
            }
            return false;
        };
        var session = await host.PlayAsync();
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && Interlocked.Read(ref appendedId) > 0);
        Assert.Equal(EngineKind.External, session.Snapshot.EngineKind);
        Assert.Equal(2, loads); // 初次候选失败，第二候选确认后才产生 Playing。
        await session.SetRateAsync(1.5, host.Token);
        await session.TogglePauseAsync(host.Token);
        await session.SeekAsync(TimeSpan.FromSeconds(7), host.Token);
        await host.WaitAsync(() => host.Reports.Any(report => report.Kind == "Progress" && report.Rate == 1.5 && report.Position >= 7 * TimeSpan.TicksPerSecond));
        await session.TogglePauseAsync(host.Token);
        await session.NextAsync(host.Token);
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && session.Snapshot.CurrentEntryIndex == 1);
        await EndAsync(host.Server, activeId);
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && host.EndReason is not null);
        Assert.Equal(PlaybackEndReason.SeasonEnded, host.EndReason);
        Assert.Equal(0, removals);
        Assert.Null(host.Coordinator.Current);
        Assert.Empty(host.Outbox.Snapshot);
        var lifecycle = host.Reports.Where(report => report.Kind != "Progress").Select(report => report.Kind).ToArray();
        Assert.Equal(ExpectedLifecycle, lifecycle);
        Assert.All(host.Reports.Where(report => report.Kind == "Stopped"), report => Assert.Equal(1.5, report.Rate));
    }

    [Fact]
    public async Task ExplicitSelectionReplacesPlaylistWithoutRemovingTheNewCurrentEntry()
    {
        await using var host = await Host.CreateAsync(3);
        var nativeId = 0L;
        var activeId = 0L;
        var appendLoads = 0;
        var removals = 0;
        host.Server.Handler = async request =>
        {
            var command = request.GetProperty("command");
            var name = command[0].GetString();
            if (name == "playlist-remove") Interlocked.Increment(ref removals);
            if (name != "loadfile") return false;
            var id = ++nativeId;
            await host.Server.ReplyAsync(request, "{\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
            if (command[2].GetString() == "append") { Interlocked.Increment(ref appendLoads); return true; }
            if (activeId > 0)
                await host.Server.SendAsync("{\"event\":\"end-file\",\"reason\":\"stop\",\"playlist_entry_id\":" + activeId.ToString(CultureInfo.InvariantCulture) + "}");
            activeId = id;
            await StartAsync(host.Server, id);
            return true;
        };
        var session = await host.PlayAsync();
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && Volatile.Read(ref appendLoads) == 2);
        await session.SelectEntryAsync(host.Preparer.Entries[2].ItemId, host.Token);
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && session.Snapshot.CurrentEntryIndex == 2 &&
            host.Reports.Count(report => report.Kind == "Playing") == 2);
        Assert.Equal(0, removals);
        await EndAsync(host.Server, activeId);
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && host.EndReason is not null);
        Assert.Equal(PlaybackEndReason.SeasonEnded, host.EndReason);
        Assert.Equal(ExpectedLifecycle, host.Reports.Where(report => report.Kind != "Progress").Select(report => report.Kind));
        Assert.DoesNotContain(host.Reports, report => report.Item == host.Preparer.Entries[1].ItemId);
    }

    [Fact]
    public async Task DisconnectedIpcStopsConfirmedEntryExactlyOnceAndUnplayedAppendNeverReports()
    {
        await using var host = await Host.CreateAsync();
        var nativeId = 0L;
        host.Server.Handler = async request =>
        {
            var command = request.GetProperty("command");
            if (command[0].GetString() != "loadfile") return false;
            var id = ++nativeId;
            await host.Server.ReplyAsync(request, "{\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
            if (command[2].GetString() == "replace") await StartAsync(host.Server, id);
            return true;
        };
        var session = await host.PlayAsync();
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && host.Reports.Any(report => report.Kind == "Playing"));
        await host.Server.SendAsync("{\"event\":\"property-change\",\"name\":\"time-pos\",\"data\":42.5}");
        await host.WaitAsync(() => session.Snapshot.PositionTicks == TimeSpan.FromSeconds(42.5).Ticks);
        await host.Server.DisconnectAsync();
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && host.EndReason is not null);
        Assert.Null(host.Coordinator.Current);
        await session.CloseAsync(host.Token);
        var stopped = Assert.Single(host.Reports, report => report.Kind == "Stopped");
        Assert.Equal(host.Preparer.Entries[0].ItemId, stopped.Item);
        Assert.Equal(TimeSpan.FromSeconds(42.5).Ticks, stopped.Position);
        Assert.Single(host.Reports, report => report.Kind == "Playing");
        Assert.Empty(host.Outbox.Snapshot);
    }

    [Fact]
    public async Task ExternalIntentIsPublishedBeforeEngineStartsAndStartupFailureClosesWithoutAPlayerPage()
    {
        await using var host = await Host.CreateAsync();
        var gate = new TaskCompletionSource<IPlayerEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.EngineFactoryOverride = _ => gate.Task;
        EngineKind? announcedKind = null;
        host.Coordinator.SessionStarted += (_, args) => announcedKind = args.Session.Snapshot.EngineKind;
        var session = await host.PlayAsync();
        await host.WaitAsync(() => announcedKind is not null);
        Assert.Equal(EngineKind.External, announcedKind);
        Assert.Equal(PlayerPhase.Preparing, session.Snapshot.Phase);
        gate.SetException(new IOException("测试启动失败"));
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && host.EndReason is not null);
        Assert.Equal(PlaybackEndReason.Failed, host.EndReason);
        Assert.NotNull(session.Snapshot.Error);
        Assert.Null(host.Coordinator.Current);
        Assert.Empty(host.Reports);
    }

    [Fact]
    public async Task NativeControlsAreObservedBeforeLoadAndNeverOverwrittenOnEpisodeChanges()
    {
        await using var host = await Host.CreateAsync();
        var nativeId = 0L;
        var controlWrites = new ConcurrentQueue<string>();
        host.Server.Handler = async request =>
        {
            var command = request.GetProperty("command");
            if (command[0].GetString() == "set_property" && command[1].GetString() is "volume" or "speed" or "mute" or "hwdec")
                controlWrites.Enqueue(command[1].GetString()!);
            if (command[0].GetString() != "loadfile") return false;
            var id = Interlocked.Increment(ref nativeId);
            await host.Server.ReplyAsync(request, "{\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
            if (command[2].GetString() == "replace")
            {
                await host.Server.SendAsync("{\"event\":\"property-change\",\"name\":\"volume\",\"data\":37}");
                await host.Server.SendAsync("{\"event\":\"property-change\",\"name\":\"speed\",\"data\":1.25}");
                await host.Server.SendAsync("{\"event\":\"property-change\",\"name\":\"mute\",\"data\":true}");
                await StartAsync(host.Server, id);
            }
            return true;
        };
        var session = await host.PlayAsync();
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && Volatile.Read(ref nativeId) == 2);
        Assert.Equal(37, session.Snapshot.Volume);
        Assert.Equal(1.25, session.Snapshot.PlaybackRate);
        Assert.True(session.Snapshot.IsMuted);
        await host.Server.SendAsync("{\"event\":\"property-change\",\"name\":\"pause\",\"data\":true}");
        await host.WaitAsync(() => host.Reports.Any(report => report.Kind == "Progress" && report.Paused));
        await EndAsync(host.Server, 1);
        await StartAsync(host.Server, 2);
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && session.Snapshot.CurrentEntryIndex == 1);
        Assert.Equal(37, session.Snapshot.Volume);
        Assert.Equal(1.25, session.Snapshot.PlaybackRate);
        Assert.True(session.Snapshot.IsMuted);
        Assert.Empty(controlWrites);
        await host.Server.SendAsync("{\"event\":\"shutdown\"}");
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && host.EndReason is not null);
        Assert.Equal(PlaybackEndReason.UserClosed, host.EndReason);
        Assert.Null(session.Snapshot.Error);
        Assert.Null(host.Coordinator.Current);
        Assert.Equal(ExpectedLifecycle, host.Reports.Where(report => report.Kind != "Progress").Select(report => report.Kind));
    }

    [Fact]
    public async Task NativePlaylistIncludesRemainingSeasonAndSupportsJumpingBackWithFreshReports()
    {
        await using var host = await Host.CreateAsync(4);
        var nativeId = 0L;
        var removals = 0;
        host.Server.Handler = async request =>
        {
            var command = request.GetProperty("command");
            if (command[0].GetString() == "playlist-remove") Interlocked.Increment(ref removals);
            if (command[0].GetString() != "loadfile") return false;
            var id = Interlocked.Increment(ref nativeId);
            await host.Server.ReplyAsync(request, "{\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
            if (command[2].GetString() == "replace") await StartAsync(host.Server, id);
            return true;
        };
        var session = await host.PlayAsync();
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && Volatile.Read(ref nativeId) == 4);
        await EndAsync(host.Server, 1);
        await StartAsync(host.Server, 3);
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && session.Snapshot.CurrentEntryIndex == 2);
        await EndAsync(host.Server, 3);
        await StartAsync(host.Server, 1);
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && session.Snapshot.CurrentEntryIndex == 0 &&
            host.Reports.Count(report => report.Kind == "Playing") == 3);
        Assert.Equal(4, nativeId);
        Assert.Equal(0, removals);
        await host.Server.SendAsync("{\"event\":\"shutdown\"}");
        await host.WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && host.EndReason is not null);
        Assert.Equal(3, host.Reports.Count(report => report.Kind == "Stopped"));
        Assert.Equal(2, host.Reports.Count(report => report.Kind == "Playing" && report.Item == host.Preparer.Entries[0].ItemId));
        Assert.DoesNotContain(host.Reports, report => report.Item == host.Preparer.Entries[1].ItemId || report.Item == host.Preparer.Entries[3].ItemId);
    }

    private static async Task StartAsync(FakeMpvIpcServer server, long id, bool confirm = true)
    {
        await server.SendAsync("{\"event\":\"start-file\",\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
        if (!confirm) return;
        await server.SendAsync("{\"event\":\"file-loaded\"}");
        await server.SendAsync("{\"event\":\"playback-restart\"}");
    }
    private static Task EndAsync(FakeMpvIpcServer server, long id) => server.SendAsync("{\"event\":\"end-file\",\"reason\":\"eof\",\"playlist_entry_id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");

    private sealed record Report(string Kind, string? Item, long Position, double Rate, bool Paused);
    private sealed class ReportHandler(ConcurrentQueue<Report> received) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var value = body.RootElement;
            var path = request.RequestUri!.AbsolutePath;
            var kind = path.EndsWith("/Stopped", StringComparison.Ordinal) ? "Stopped" : path.EndsWith("/Progress", StringComparison.Ordinal) ? "Progress" : "Playing";
            received.Enqueue(new(kind, value.GetProperty("ItemId").GetString(), value.GetProperty("PositionTicks").GetInt64(), value.GetProperty("PlaybackRate").GetDouble(), value.GetProperty("IsPaused").GetBoolean()));
            return new(HttpStatusCode.NoContent);
        }
    }
    private sealed class Scheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> actions = new();
        public bool TryEnqueue(Action callback) { actions.Enqueue(callback); return true; }
        public void Drain() { while (actions.TryDequeue(out var action)) action(); }
    }
    private sealed class Preparer(int count) : IEntryPreparer
    {
        public ImmutableArray<PlaybackEntry> Entries { get; } = Enumerable.Range(1, count)
            .Select(index => new PlaybackEntry(Guid.NewGuid().ToString("N"), "测试集 " + index.ToString(CultureInfo.InvariantCulture))).ToImmutableArray();
        public Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken) => Task.FromResult(new PreparedPlan(Entries, 0, 0));
        public Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken)
        {
            var source = new EmbyMediaSource { Id = Guid.NewGuid().ToString("N") };
            var candidates = Enumerable.Range(0, 2).Select(index => new StreamCandidate(account.Address.Endpoint("Videos/" + entry.ItemId + "/" + index), "DirectPlay", source, ImmutableDictionary<string, string>.Empty)).ToImmutableArray();
            return Task.FromResult(new PreparedEntry(entry, new() { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSources = [source] }, candidates, [], startTicks));
        }
        public Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken)
        {
            var candidate = entry.Candidates[candidateIndex];
            return Task.FromResult(new ResolvedCandidate(candidate, new(candidate.Address, candidate.RequiredHeaders, 0), [new("http-header-fields", "")]));
        }
        public Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry, ResolvedCandidate selectedCandidate, CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<ResolvedSubtitle>.Empty);
        public Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles) => Task.CompletedTask;
    }
    private sealed class Host : IAsyncDisposable
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "p7-playback-tests", Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly AccountContext accounts = new();
        private readonly Scheduler scheduler = new();
        // IPC 生命周期/报告顺序由事件驱动；并发测试的磁盘延迟不能消耗生产停止预算。
        // 预算取消及 pending 保留另由 PlaybackReporterTests/StopOutboxTests 显式推进时钟验证。
        private readonly FakeTimeProvider clock = new();
        private readonly SettingsStore settings;
        private readonly EmbyApi api;
        public FakeMpvIpcServer Server { get; }
        public Preparer Preparer { get; }
        public ConcurrentQueue<Report> Reports { get; } = new();
        public StopOutbox Outbox { get; }
        public PlaybackCoordinator Coordinator { get; }
        public PlaybackEndReason? EndReason { get; private set; }
        public CancellationToken Token => deadline.Token;
        public Func<CancellationToken, Task<IPlayerEngine>>? EngineFactoryOverride { get; set; }
        private Host(FakeMpvIpcServer server, int count)
        {
            Preparer = new(count);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            Server = server;
            var paths = new AppPaths(root);
            settings = new(paths, scheduler, new TestValidator());
            accounts.Set(new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "测试用户", Guid.NewGuid().ToString("N"))));
            api = new(settings.Current.DeviceId, new ReportHandler(Reports));
            Outbox = new(paths, api, clock);
            Coordinator = new(accounts, Preparer, async cancellation => EngineFactoryOverride is { } factory
                ? await factory(cancellation) : await ExternalMpvEngine.CreateForTestingAsync(server.Client, cancellationToken: cancellation),
                api, Outbox, settings, scheduler, new WeakReferenceMessenger(), clock);
            Coordinator.SessionEnded += (_, args) => EndReason = args.EndReason;
        }
        public static async Task<Host> CreateAsync(int count = 2)
        {
            var host = new Host(await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken), count);
            await host.settings.ValidateExternalPlayerAsync(Path.Combine(host.root, "mpv.exe"), host.Token);
            await host.settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, host.Token);
            return host;
        }
        public Task<IPlaybackSession> PlayAsync() => Coordinator.PlayAsync(new(Preparer.Entries[0].ItemId), Token);
        public async Task WaitAsync(Func<bool> condition)
        {
            while (true)
            {
                scheduler.Drain();
                if (condition()) return;
                await Task.Delay(10, Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            var disposing = Coordinator.DisposeAsync().AsTask();
            // 失败清理时 fake IPC 未必发送 end-file；推进关闭兜底定时器，仍由真实时间限制清理。
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!disposing.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(3));
                await Task.WhenAny(disposing, Task.Delay(10, cleanupDeadline.Token));
                cleanupDeadline.Token.ThrowIfCancellationRequested();
            }
            await disposing;
            await Outbox.DisposeAsync();
            await Server.DisposeAsync();
            settings.Dispose(); api.Dispose(); accounts.Dispose(); deadline.Dispose();
            var fullRoot = Path.GetFullPath(root);
            if (fullRoot.StartsWith(Path.GetFullPath(AppContext.BaseDirectory) + "p7-playback-tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(fullRoot, recursive: true);
        }
    }

    private sealed class TestValidator : IExternalPlayerValidator
    {
        public Task<ExternalMpvApproval> ValidateAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalMpvApproval(path, new string('a', 64), 1, DateTime.UtcNow.Ticks, "0.41.0"));
        public Task<bool> VerifyAsync(ExternalMpvApproval approval, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
