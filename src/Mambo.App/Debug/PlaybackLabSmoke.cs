using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.App.Composition;
using Mambo.App.Video;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Mambo.Player.LibMpv;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Debug;

/// <summary>仅使用本地片源和内存服务器的真实 composition 会话回归。</summary>
internal static class PlaybackLabSmoke
{
    private static readonly string[] QuitCommand = ["quit"];
    public static async Task RunAsync(VideoSurface surface, string samplePath, DispatcherQueue dispatcherQueue, string reportPath)
    {
        var report = new PlaybackLabReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "p3-playback-lab", Guid.NewGuid().ToString("N"));
        PlaybackCoordinator? coordinator = null;
        StopOutbox? outbox = null;
        SettingsStore? settings = null;
        EmbyApi? api = null;
        AccountContext? accounts = null;
        IPlaybackSession? session = null;
        var handler = new ReportHandler();
        try
        {
            report.Stage = "等待布局";
            await WaitAsync(async () => await OnUiAsync(dispatcherQueue, () => surface.ActualWidth > 0 && surface.ActualHeight > 0), token);
            if (!File.Exists(samplePath)) throw new InvalidOperationException("本地测试样片不存在。");
            var paths = new AppPaths(root);
            var scheduler = new UiScheduler(dispatcherQueue);
            settings = new(paths, scheduler);
            await settings.UpdateAsync(value => value with { Volume = 40 }, token);
            accounts = new();
            accounts.Set(new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "本地播放验证", Guid.NewGuid().ToString("N"))));
            api = new(settings.Current.DeviceId, handler);
            outbox = new(paths, api);
            var preparer = new LocalPreparer(new Uri(Path.GetFullPath(samplePath)));
            var pixels = await OnUiAsync(dispatcherQueue, () => surface.PixelSize);
            coordinator = new(accounts, preparer, async cancellation => await LibMpvEngine.CreateAsync(
                pixels.Width, pixels.Height, headless: false, enableAudio: false, cancellationToken: cancellation).ConfigureAwait(false),
                api, outbox, settings, scheduler, new WeakReferenceMessenger(), TimeProvider.System);

            report.Stage = "打开真实播放会话";
            session = await coordinator.PlayAsync(new(preparer.Entries[0].ItemId), token);
            await OnUiAsync(dispatcherQueue, () => { surface.Attach(session); return true; });
            await WaitPlayingAsync(session, 0, token);
            report.Playing = true;
            await WaitAsync(async () => await OnUiAsync(dispatcherQueue, () =>
                surface.BufferSize.Width > 0 && surface.BufferSize.Height > 0 && surface.BufferSize == surface.PixelSize), token);
            var buffer = await OnUiAsync(dispatcherQueue, () => surface.BufferSize);
            report.Bound = true; report.SizeMatched = true; report.BufferWidth = buffer.Width; report.BufferHeight = buffer.Height;
            await WaitAsync(() => Task.FromResult(handler.Snapshot().Any(value => value.Kind == "Playing")), token);

            report.Stage = "暂停倍速和跳转";
            await session.TogglePauseAsync(token);
            await WaitAsync(() => Task.FromResult(session.Snapshot.IsPaused), token);
            report.Paused = true;
            await session.SetRateAsync(1.5, token);
            await session.SeekAsync(TimeSpan.FromSeconds(2), token);
            await WaitAsync(() => Task.FromResult(handler.Snapshot().Any(value => value.Kind == "Progress" &&
                value.PlaybackRate == 1.5 && value.PositionTicks >= 2 * TimeSpan.TicksPerSecond)), token);
            report.Rate = session.Snapshot.PlaybackRate == 1.5;
            report.Seek = true;
            await session.TogglePauseAsync(token);
            await WaitAsync(() => Task.FromResult(!session.Snapshot.IsPaused), token);

            report.Stage = "真实连播切集";
            await session.NextAsync(token);
            await WaitPlayingAsync(session, 1, token);
            await WaitAsync(() => Task.FromResult(handler.Snapshot().Count(value => value.Kind == "Playing") == 2), token);
            report.Next = true;

            report.Stage = "引擎退出后重试画面";
            var exited = ((PlaybackSession)session).Engine as LibMpvEngine ?? throw new InvalidOperationException("未找到真实内置播放器。");
            // quit 的回复可能被 SHUTDOWN 中止；以下验证由真实会话 Failed 事件决定。
            try { await exited.Core.CommandAsync(QuitCommand.AsMemory(), token); }
            catch (InvalidOperationException) when (!token.IsCancellationRequested) { }
            catch (TimeoutException) when (!token.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            await WaitAsync(() => Task.FromResult(session.Snapshot.Phase == PlayerPhase.Failed), token);
            await session.RetryAsync(token);
            // 保持原有 Surface.Attach(session)，要求桥接自动绑定本会话的新引擎。
            await WaitPlayingAsync(session, 1, token);
            await WaitAsync(async () => await OnUiAsync(dispatcherQueue, () =>
                surface.BufferSize.Width > 0 && surface.BufferSize.Height > 0 && surface.BufferSize == surface.PixelSize), token);
            await WaitAsync(() => Task.FromResult(handler.Snapshot().Count(value => value.Kind == "Playing") == 3), token);
            report.Retried = !ReferenceEquals(((PlaybackSession)session).Engine, exited) && ReferenceEquals(coordinator.Current, session);

            report.Stage = "停止和画面解绑";
            await session.CloseAsync(token);
            await WaitAsync(async () => await OnUiAsync(dispatcherQueue, () => surface.BufferSize == (0, 0)), token);
            report.Closed = session.Snapshot.Phase == PlayerPhase.Closed;
            report.Detached = true;
            report.OutboxEmpty = outbox.Snapshot.IsEmpty;
            var received = handler.Snapshot();
            report.ReportSequence = received.Select(value => value.Kind).ToArray();
            report.StoppedRates = received.Where(value => value.Kind == "Stopped").Select(value => value.PlaybackRate).ToArray();
            report.ReportsOrdered = ValidateReports(received);
            report.Stopped = received.Count(value => value.Kind == "Stopped") == 3 &&
                received.Where(value => value.Kind == "Stopped").All(value => value.PlaybackRate == 1.5);
            report.Passed = report.Playing && report.Bound && report.SizeMatched && report.Paused && report.Rate && report.Seek &&
                report.Next && report.Retried && report.Closed && report.Detached && report.OutboxEmpty && report.ReportsOrdered && report.Stopped;
            if (!report.Passed) throw new InvalidOperationException("播放回归有检查未通过。");
            report.Stage = "完成";
        }
        catch (Exception error)
        {
            report.ErrorKind = error.GetType().Name;
            report.HResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
            report.Error = "真实播放验证未通过，请检查对应阶段。";
        }
        finally
        {
            try
            {
                if (session is not null && session.Snapshot.Phase != PlayerPhase.Closed)
                    await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(6));
                await OnUiAsync(dispatcherQueue, () => { surface.Detach(); return true; });
                if (coordinator is not null) await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                if (outbox is not null) await outbox.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                settings?.Dispose(); api?.Dispose(); accounts?.Dispose();
            }
            catch (Exception error)
            {
                report.Passed = false; report.Stage = "清理播放会话";
                report.ErrorKind = error.GetType().Name; report.HResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
                report.Error = "播放会话清理未通过。";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, PlaybackLabJsonContext.Default.PlaybackLabReport));
        }
    }

    private static bool ValidateReports(ReportObservation[] reports)
    {
        var active = false; var playing = 0; var stopped = 0;
        foreach (var item in reports)
        {
            if (item.Kind == "Playing") { if (active) return false; active = true; playing++; }
            else if (item.Kind == "Progress") { if (!active) return false; }
            else if (item.Kind == "Stopped") { if (!active) return false; active = false; stopped++; }
        }
        return !active && playing == 3 && stopped == 3;
    }

    private static Task WaitPlayingAsync(IPlaybackSession session, int index, CancellationToken token) => WaitAsync(() =>
    {
        if (session.Snapshot.Phase == PlayerPhase.Failed) throw new InvalidOperationException("真实播放会话失败。");
        return Task.FromResult(session.Snapshot.Phase == PlayerPhase.Playing && session.Snapshot.CurrentEntryIndex == index);
    }, token);

    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition().ConfigureAwait(false)) await Task.Delay(25, token).ConfigureAwait(false);
    }

    private static Task<T> OnUiAsync<T>(DispatcherQueue queue, Func<T> callback)
    {
        if (queue.HasThreadAccess) return Task.FromResult(callback());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try { completion.TrySetResult(callback()); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new InvalidOperationException("界面线程已关闭。"));
        return completion.Task;
    }

    private sealed class LocalPreparer(Uri sample) : IEntryPreparer
    {
        public ImmutableArray<PlaybackEntry> Entries { get; } = [new(Guid.NewGuid().ToString("N"), "本地样片第一集"), new(Guid.NewGuid().ToString("N"), "本地样片第二集")];
        public Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = -1;
            for (var index = 0; index < Entries.Length; index++)
                if (Entries[index].ItemId == request.ItemId) { selected = index; break; }
            if (selected < 0) throw new InvalidOperationException("本地验证播放目标无效。");
            return Task.FromResult(new PreparedPlan(Entries, selected, Math.Max(0, request.StartTicks ?? 0)));
        }
        public Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new EmbyMediaSource { Id = Guid.NewGuid().ToString("N"), Container = "mp4" };
            return Task.FromResult(new PreparedEntry(entry, new() { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSources = [source] },
                [new(sample, "DirectPlay", source, new Dictionary<string, string>())], [], startTicks));
        }
        public Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
            options.Add(new("http-header-fields", "")); options.Add(new("force-media-title", entry.Entry.Title));
            if (entry.StartTicks > 0) options.Add(new("start", (entry.StartTicks / (double)TimeSpan.TicksPerSecond).ToString("F3", CultureInfo.InvariantCulture)));
            return Task.FromResult(new ResolvedCandidate(entry.Candidates[candidateIndex], new(sample, new Dictionary<string, string>(), 0),
                options.ToImmutable()));
        }
        public Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry, ResolvedCandidate selectedCandidate, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ResolvedSubtitle>.Empty);
        public Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles) => Task.CompletedTask;
    }

    private sealed record ReportObservation(string Kind, double PlaybackRate, long PositionTicks);
    private sealed class ReportHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<ReportObservation> received = new();
        public ReportObservation[] Snapshot() => received.ToArray();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var kind = path.EndsWith("/Stopped", StringComparison.Ordinal) ? "Stopped" : path.EndsWith("/Progress", StringComparison.Ordinal) ? "Progress" : "Playing";
            if (!path.Contains("/Sessions/Playing", StringComparison.Ordinal)) throw new InvalidOperationException("出现非上报请求。");
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var body = JsonSerializer.Deserialize(bytes, EmbyJsonContext.Default.PlaybackReport) ?? throw new InvalidOperationException("上报载荷无效。");
            received.Enqueue(new(kind, body.PlaybackRate, body.PositionTicks));
            return new(HttpStatusCode.NoContent);
        }
    }
}

internal sealed class PlaybackLabReport
{
    public string ErrorStack { get; set; } = "";
    public bool Passed { get; set; }
    public bool Playing { get; set; }
    public bool Bound { get; set; }
    public bool SizeMatched { get; set; }
    public int BufferWidth { get; set; }
    public int BufferHeight { get; set; }
    public bool Paused { get; set; }
    public bool Rate { get; set; }
    public bool Seek { get; set; }
    public bool Next { get; set; }
    public bool Retried { get; set; }
    public bool Closed { get; set; }
    public bool Detached { get; set; }
    public bool OutboxEmpty { get; set; }
    public bool ReportsOrdered { get; set; }
    public bool Stopped { get; set; }
    public double[] StoppedRates { get; set; } = [];
    public string[] ReportSequence { get; set; } = [];
    public string Stage { get; set; } = "开始";
    public string Error { get; set; } = "";
    public string ErrorKind { get; set; } = "";
    public string HResult { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PlaybackLabReport))]
internal sealed partial class PlaybackLabJsonContext : JsonSerializerContext;
