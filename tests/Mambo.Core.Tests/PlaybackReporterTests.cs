using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class PlaybackReporterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoppedPersistsWhilePlayingIsBlockedSkipsQueuedProgressAndHonorsExitBudget()
    {
        var firstPlaying = Signal();
        var secondPlaying = Signal();
        var playingCalls = 0;
        var progressCalls = 0;
        await using var harness = new Harness(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Playing", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref playingCalls) == 1) firstPlaying.TrySetResult();
                else secondPlaying.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/Progress", StringComparison.Ordinal))
                Interlocked.Increment(ref progressCalls);
            return Ok();
        });
        var playing = harness.Reporter.PlayingAsync(harness.ReportId, harness.Report);
        await AwaitAsync(firstPlaying.Task);
        var progress = Enumerable.Range(0, 3).Select(index => harness.Reporter.ProgressAsync(harness.ReportId,
            harness.Report with { PositionTicks = index + 1, EventName = "TimeUpdate" })).ToArray();
        var stopped = harness.Reporter.StoppedAsync(harness.ReportId, harness.Entry,
            harness.Report with { PositionTicks = 900 }, false, TimeSpan.FromSeconds(1.5));
        await AwaitAsync(secondPlaying.Task);
        Assert.Equal(900, Assert.Single(harness.Outbox.Snapshot).PositionTicks);
        Assert.True(File.Exists(harness.Paths.Outbox));
        Assert.False(stopped.IsCompleted);
        harness.Clock.Advance(TimeSpan.FromSeconds(1.5));
        await AwaitAsync(stopped);
        await AwaitAsync(Task.WhenAll(progress.Append(playing)));
        Assert.Equal(0, progressCalls);
        Assert.Equal(900, Assert.Single(harness.Outbox.Snapshot).PositionTicks);
    }

    [Fact]
    public async Task StoppedSendsActualPlaybackStateInsteadOfDefaultOutboxValues()
    {
        var stoppedBodies = new ConcurrentQueue<JsonElement>();
        await using var harness = new Harness(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Stopped", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
                stoppedBodies.Enqueue(body.RootElement.Clone());
            }
            return Ok();
        });
        await AwaitAsync(harness.Reporter.PlayingAsync(harness.ReportId, harness.Report));
        var final = harness.Report with
        {
            PositionTicks = 987654321, PlaybackRate = 1.75, PlayMethod = "Transcode", CanSeek = false,
            IsPaused = true, IsMuted = true, VolumeLevel = 36, PlaylistIndex = 2, PlaylistLength = 5,
            AudioStreamIndex = 7, SubtitleStreamIndex = 11, LiveStreamId = Guid.NewGuid().ToString("N"),
        };
        await AwaitAsync(harness.Reporter.StoppedAsync(harness.ReportId, harness.Entry,
            final, true, TimeSpan.FromSeconds(3)));
        var wire = Assert.Single(stoppedBodies);
        Assert.Equal(final.ItemId, wire.GetProperty("ItemId").GetString());
        Assert.Equal(final.MediaSourceId, wire.GetProperty("MediaSourceId").GetString());
        Assert.Equal(final.PlaySessionId, wire.GetProperty("PlaySessionId").GetString());
        Assert.Equal(final.LiveStreamId, wire.GetProperty("LiveStreamId").GetString());
        Assert.Equal(final.PlaybackStartTimeTicks, wire.GetProperty("PlaybackStartTimeTicks").GetInt64());
        Assert.Equal(final.PositionTicks, wire.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(final.PlaybackRate, wire.GetProperty("PlaybackRate").GetDouble());
        Assert.Equal(final.PlayMethod, wire.GetProperty("PlayMethod").GetString());
        Assert.Equal(final.CanSeek, wire.GetProperty("CanSeek").GetBoolean());
        Assert.Equal(final.IsPaused, wire.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(final.IsMuted, wire.GetProperty("IsMuted").GetBoolean());
        Assert.Equal(final.VolumeLevel, wire.GetProperty("VolumeLevel").GetDouble());
        Assert.Equal(final.PlaylistIndex, wire.GetProperty("PlaylistIndex").GetInt32());
        Assert.Equal(final.PlaylistLength, wire.GetProperty("PlaylistLength").GetInt32());
        Assert.Equal(final.AudioStreamIndex, wire.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(final.SubtitleStreamIndex, wire.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.False(wire.GetProperty("Failed").GetBoolean());
    }

    [Fact]
    public async Task DelayedStoppedNotificationDoesNotAffectNewAccount()
    {
        await using var harness = new Harness((_, _) => Task.FromResult(Ok()));
        await AwaitAsync(harness.Reporter.PlayingAsync(harness.ReportId, harness.Report));
        await AwaitAsync(harness.Reporter.StoppedAsync(harness.ReportId, harness.Entry,
            harness.Report, false, TimeSpan.FromSeconds(3)));
        Assert.Empty(harness.Outbox.Snapshot);
        harness.Accounts.Set(NewAccount());
        harness.Scheduler.Drain();
        Assert.Empty(harness.Messages);
    }

    [Fact]
    public async Task SuccessfulStoppedNotificationReadsUpdatedServerUserData()
    {
        var stopStarted = Signal();
        var releaseStop = Signal();
        var serverPosition = 10L;
        var observedPositions = new ConcurrentQueue<long>();
        await using var harness = new Harness(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Stopped", StringComparison.Ordinal))
            {
                stopStarted.TrySetResult();
                await releaseStop.Task.WaitAsync(token);
                Interlocked.Exchange(ref serverPosition, 900);
            }
            return Ok();
        });
        harness.OnStopped = _ => observedPositions.Enqueue(Interlocked.Read(ref serverPosition));
        await AwaitAsync(harness.Reporter.PlayingAsync(harness.ReportId, harness.Report));
        var stopped = harness.Reporter.StoppedAsync(harness.ReportId, harness.Entry,
            harness.Report with { PositionTicks = 900 }, false, TimeSpan.FromSeconds(3));
        await AwaitAsync(stopStarted.Task);
        harness.Scheduler.Drain();
        Assert.Empty(observedPositions);
        releaseStop.TrySetResult();
        await AwaitAsync(stopped);
        harness.Scheduler.Drain();
        Assert.Equal(900, Assert.Single(observedPositions));
        var message = Assert.Single(harness.Messages);
        Assert.Equal(harness.Entry.ItemId, message.ItemId);
        Assert.Equal(harness.Entry.SeriesId, message.SeriesId);
        Assert.Equal(harness.Entry.SeasonId, message.SeasonId);
    }

    [Fact]
    public async Task RetryableStoppedFailureStillAttemptsCleanupWithinRemainingExitBudget()
    {
        var stopStarted = Signal();
        var releaseStop = Signal();
        var cleanupStarted = Signal();
        var cleanupCalls = 0;
        await using var harness = new Harness(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Stopped", StringComparison.Ordinal))
            {
                stopStarted.TrySetResult();
                await releaseStop.Task.WaitAsync(token);
                return new(HttpStatusCode.ServiceUnavailable);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/ActiveEncodings", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Interlocked.Increment(ref cleanupCalls);
                cleanupStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Ok();
        });
        await AwaitAsync(harness.Reporter.PlayingAsync(harness.ReportId, harness.Report));
        var stopped = harness.Reporter.StoppedAsync(harness.ReportId, harness.Entry,
            harness.Report, true, TimeSpan.FromSeconds(1.5));
        await AwaitAsync(stopStarted.Task);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        releaseStop.TrySetResult();
        await AwaitAsync(cleanupStarted.Task);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await AwaitAsync(stopped);
        Assert.Equal(1, cleanupCalls);
        Assert.Single(harness.Outbox.Snapshot);
    }

    [Fact]
    public async Task UnconfirmedTranscodeCleanupDoesNotReportPlaybackOrCreateOutboxRecord()
    {
        var calls = new ConcurrentQueue<string>();
        await using var harness = new Harness((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            calls.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Ok());
        });
        await AwaitAsync(harness.Reporter.CleanupAsync(harness.Report.PlaySessionId!, TimeSpan.FromSeconds(1.5)));
        harness.Scheduler.Drain();
        Assert.EndsWith("/ActiveEncodings", Assert.Single(calls), StringComparison.Ordinal);
        Assert.Empty(harness.Outbox.Snapshot);
        Assert.False(File.Exists(harness.Paths.Outbox));
        Assert.Empty(harness.Messages);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task AwaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);
    private static AccountSession NewAccount() => new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid",
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "演示", Guid.NewGuid().ToString("N")));

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string root = Path.Combine(FindRoot(), "artifacts", "tests", "reporter-" + Guid.NewGuid().ToString("N"));
        private readonly EmbyApi api;
        public FakeTimeProvider Clock { get; } = new();
        public AccountContext Accounts { get; } = new();
        public AccountSession Account { get; } = NewAccount();
        public AppPaths Paths { get; }
        public StopOutbox Outbox { get; }
        public PlaybackReporter Reporter { get; }
        public QueuedScheduler Scheduler { get; } = new();
        public WeakReferenceMessenger Messenger { get; } = new();
        public ConcurrentQueue<PlaybackStopped> Messages { get; } = new();
        public Action<PlaybackStopped>? OnStopped { get; set; }
        public string ReportId { get; } = Guid.NewGuid().ToString("D");
        public PlaybackEntry Entry { get; } = new(Guid.NewGuid().ToString("N"), "演示单集")
        { SeriesId = Guid.NewGuid().ToString("N"), SeasonId = Guid.NewGuid().ToString("N") };
        public PlaybackReport Report { get; }

        public Harness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            Paths = new(root);
            Accounts.Set(Account);
            api = new(Guid.NewGuid(), new Handler(send));
            Outbox = new(Paths, api, Clock);
            Reporter = new(Account, api, Outbox, Scheduler, Messenger, Clock,
                isCurrentAccount: () => ReferenceEquals(Accounts.Current, Account));
            Report = new() { ItemId = Entry.ItemId, MediaSourceId = Guid.NewGuid().ToString("N"),
                PlaySessionId = Guid.NewGuid().ToString("N"), PlaybackStartTimeTicks = 100, PositionTicks = 200 };
            Messenger.Register<Harness, PlaybackStopped>(this, static (recipient, message) =>
            { recipient.Messages.Enqueue(message); recipient.OnStopped?.Invoke(message); });
        }

        public async ValueTask DisposeAsync()
        {
            Accounts.Dispose();
            await Reporter.DisposeAsync();
            await Outbox.DisposeAsync();
            api.Dispose();
            Messenger.UnregisterAll(this);
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.Combine(FindRoot(), "artifacts", "tests")) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录超出 artifacts/tests。");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }

        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mambo.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? AppContext.BaseDirectory;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class QueuedScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> callbacks = new();
        public bool TryEnqueue(Action action) { callbacks.Enqueue(action); return true; }
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }
}
