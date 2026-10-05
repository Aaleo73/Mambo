using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Mambo.Player.External;

namespace Mambo.ExternalSmoke;

internal static class Program
{
    private const string ExecutableHash = "b0bb2dc1928e6d86cc26d950815c80c977440081e814c6a46e93f6e9e99c276d";
    private const string CompilerHash = "4b074a3976399dc735484f5d43d04b519b7bdee8ac719d9ab8ed6bd4e6be0345";
    private static string stage = "检查隔离参数";
    private static string? reportFile;

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4)
        {
            Console.Error.WriteLine("参数：已核验 mpv.exe、样片、全新验收目录、仓库目录；仅支持 Windows。");
            return 2;
        }
        var evidence = new SmokeEvidence { StartedUtc = DateTimeOffset.UtcNow, ExecutableSha256 = ExecutableHash };
        try
        {
            await RunAsync(args, evidence).ConfigureAwait(false);
            evidence.Status = "Passed";
            evidence.Stage = "Complete";
            evidence.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteEvidenceAsync(evidence).ConfigureAwait(false);
            Console.WriteLine("PASS：真实 IPC 开播、杀掉本次进程后 Stopped 落盘/补报、文件替换撤销批准、再次明确批准恢复播放。");
            Console.WriteLine(reportFile);
            return 0;
        }
        catch (Exception error)
        {
            // Do not print native version output, transport addresses, headers or exception dumps.
            evidence.Status = "Failed";
            evidence.Stage = stage;
            evidence.ExceptionType = error.GetType().Name;
            evidence.HResult = error.HResult;
            evidence.CompletedUtc = DateTimeOffset.UtcNow;
            try { await WriteEvidenceAsync(evidence).ConfigureAwait(false); }
            catch (Exception reportError)
            {
                Console.Error.WriteLine($"安全验收报告写入失败：{reportError.GetType().Name}；{reportError.HResult}");
            }
            Console.Error.WriteLine($"外部进程验收失败：{stage}；{error.GetType().Name}；{error.HResult}");
            return 1;
        }
    }

    private static Task WriteEvidenceAsync(SmokeEvidence evidence) => reportFile is null
        ? Task.CompletedTask
        : File.WriteAllBytesAsync(reportFile, JsonSerializer.SerializeToUtf8Bytes(evidence, SmokeJsonContext.Default.SmokeEvidence), CancellationToken.None);

    private static async Task RunAsync(string[] args, SmokeEvidence evidence)
    {
        var runRoot = Path.GetFullPath(args[2]);
        var repository = Path.GetFullPath(args[3]);
        var allowedRoot = Path.Combine(repository, "artifacts", "p7-external-smoke") + Path.DirectorySeparatorChar;
        Require(runRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(runRoot), "验收目录必须是仓库内全新的独立目录。");
        Directory.CreateDirectory(runRoot);
        // Write failure evidence only after this invocation has created its validated, unique directory.
        reportFile = Path.Combine(runRoot, "result.json");
        var sourceExecutable = Path.GetFullPath(args[0]);
        var sample = Path.GetFullPath(args[1]);
        Require(File.Exists(sample), "本地样片不存在。");
        Require(string.Equals(Path.GetFileName(sourceExecutable), "mpv.exe", StringComparison.OrdinalIgnoreCase), "请使用锁定官方 MPV 文件。");
        await CheckHashAsync(sourceExecutable, ExecutableHash).ConfigureAwait(false);
        var sourceCompiler = Path.Combine(Path.GetDirectoryName(sourceExecutable)!, "d3dcompiler_43.dll");
        await CheckHashAsync(sourceCompiler, CompilerHash).ConfigureAwait(false);
        var runtime = Path.Combine(runRoot, "runtime");
        Directory.CreateDirectory(runtime);
        var executable = Path.Combine(runtime, "mpv.exe");
        File.Copy(sourceExecutable, executable, overwrite: false);
        File.Copy(sourceCompiler, Path.Combine(runtime, "d3dcompiler_43.dll"), overwrite: false);
        await CheckHashAsync(executable, ExecutableHash).ConfigureAwait(false);
        await CheckHashAsync(Path.Combine(runtime, "d3dcompiler_43.dll"), CompilerHash).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var scheduler = new Scheduler();
        var validator = new MpvExecutableApproval();
        var paths = new AppPaths(Path.Combine(runRoot, "state"));
        using var settings = new SettingsStore(paths, scheduler, validator);
        using var accounts = new AccountContext();
        // An .invalid address with generated disposable identity; the handler has no network fallback.
        accounts.Set(new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "隔离诊断", Guid.NewGuid().ToString("N"))));
        var handler = new ReportHandler();
        using var api = new EmbyApi(settings.Current.DeviceId, handler);
        await using var outbox = new StopOutbox(paths, api);
        var delivered = 0;
        outbox.Delivered += (_, _) => Interlocked.Increment(ref delivered);
        var messenger = new WeakReferenceMessenger();
        var recipient = new object();
        var notifications = 0;
        messenger.Register<PlaybackStopped>(recipient, (_, _) => Interlocked.Increment(ref notifications));
        var preparer = new LocalPreparer(sample);
        var engines = new ConcurrentQueue<ObservedEngine>();
        async Task<IPlayerEngine> CreateEngineAsync(CancellationToken cancellation)
        {
            var approval = await settings.GetApprovedExternalPlayerAsync(cancellation).ConfigureAwait(false);
            Require(approval is not null, "批准复验未通过，禁止启动外部文件。");
            var external = await ExternalMpvEngine.CreateAsync(approval!, validator, cancellationToken: cancellation).ConfigureAwait(false);
            try
            {
                // Production starts force-window=immediate; disable output before the first load.
                await external.SetAsync("force-window", new MpvValue.Text("no"), cancellation).ConfigureAwait(false);
                await external.SetAsync("vo", new MpvValue.Text("null"), cancellation).ConfigureAwait(false);
                await external.SetAsync("ao", new MpvValue.Text("null"), cancellation).ConfigureAwait(false);
                var observed = new ObservedEngine(external);
                engines.Enqueue(observed);
                return observed;
            }
            catch { await external.DisposeAsync().ConfigureAwait(false); throw; }
        }
        await using var coordinator = new PlaybackCoordinator(accounts, preparer, CreateEngineAsync,
            api, outbox, settings, scheduler, messenger, TimeProvider.System);
        stage = "显式批准并通过真实 IPC 播放";
        await settings.ValidateExternalPlayerAsync(executable, token).ConfigureAwait(false);
        Require(settings.ExternalPlayerStatus == ExternalPlayerStatus.Approved, "显式版本探测没有产生批准。");
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, token).ConfigureAwait(false);
        var firstApproval = settings.Current.ExternalMpvApproval!;
        var startedAfter = DateTime.UtcNow;
        var session = await coordinator.PlayAsync(new(preparer.Entry.ItemId), token).ConfigureAwait(false);
        await WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && session.Snapshot.PositionTicks > 0 &&
            handler.Count("Playing", successful: true) == 1, scheduler, token).ConfigureAwait(false);
        Require(session.Snapshot.EngineKind == EngineKind.External, "会话没有使用真实外部引擎。");
        await session.TogglePauseAsync(token).ConfigureAwait(false);
        await WaitAsync(() => session.Snapshot.IsPaused, scheduler, token).ConfigureAwait(false);
        evidence.PlayingConfirmed = true;
        evidence.PositionBeforeKillTicks = session.Snapshot.PositionTicks;
        Require(engines.TryPeek(out var firstEngine), "没有取得本次引擎事件桥。");
        using var process = FindOwnedProcess(executable, startedAfter);
        evidence.KilledProcessId = process.Id;
        evidence.KilledProcessStartedUtc = process.StartTime.ToUniversalTime();

        stage = "结束本次 PID 并保留 Stopped 到 Outbox";
        // The unique file path, launch time and opened process handle all bind this to our own launch.
        Require(!process.HasExited && SamePath(process.MainModule?.FileName, executable), "本次进程身份已改变。");
        process.Kill(entireProcessTree: false);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        evidence.OwnedProcessExited = process.HasExited;
        await WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && coordinator.Current is null && firstEngine!.TerminationCount > 0 &&
            handler.Count("Stopped", successful: false) >= 1 && outbox.Snapshot.Length == 1, scheduler, token).ConfigureAwait(false);
        await outbox.WaitForIdleAsync(token).ConfigureAwait(false);
        var stopped = outbox.Snapshot.Single();
        Require(stopped.ItemId == preparer.Entry.ItemId && stopped.PositionTicks == session.Snapshot.PositionTicks &&
            stopped.PositionTicks >= evidence.PositionBeforeKillTicks, "停止记录未保留本次条目和最终位置。");
        using (var disk = JsonDocument.Parse(await File.ReadAllBytesAsync(paths.Outbox, token).ConfigureAwait(false)))
        {
            Require(disk.RootElement.GetProperty("records").GetArrayLength() == 1, "停止记录没有实际落盘。");
            Require(disk.RootElement.GetProperty("records")[0].GetProperty("itemId").GetString() == preparer.Entry.ItemId,
                "落盘记录属于其他条目。");
        }
        await WaitAsync(() => Volatile.Read(ref notifications) == 1, scheduler, token).ConfigureAwait(false);
        evidence.EngineTerminationObserved = true;
        evidence.EngineFailureEventCount = firstEngine!.FailureCount;
        evidence.EngineShutdownEventCount = firstEngine.ShutdownCount;
        evidence.SessionClosed = true;
        evidence.StoppedPersistedWhileOffline = true;
        evidence.FinalPositionTicks = stopped.PositionTicks;

        stage = "恢复传输并确认一次停止送达";
        handler.RejectStopped = false;
        await outbox.FlushAsync(accounts.Current, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        Require(outbox.Snapshot.IsEmpty && handler.Count("Stopped", successful: true) == 1 && Volatile.Read(ref delivered) == 1,
            "Outbox 恢复没有完成一次停止送达。");
        await session.CloseAsync(token).ConfigureAwait(false);
        scheduler.Drain();
        Require(handler.Count("Stopped", successful: true) == 1 && Volatile.Read(ref notifications) == 1,
            "重复关闭已结束会话产生了额外停止通知或送达。");
        evidence.StoppedDeliveredAfterRecovery = true;
        evidence.CloseDidNotDuplicateStopped = true;

        stage = "替换文件字节并撤销批准";
        Require(firstEngine!.Disposed, "本次引擎关闭尚未完成，禁止替换文件。");
        evidence.EngineDisposedBeforeReplacement = true;
        // A signaled process handle is still owned until disposed. Release the diagnostic's handle too.
        process.Dispose();
        evidence.OwnedProcessHandleReleased = true;
        await using (var replacement = await OpenReplacementAsync(executable, evidence, token).ConfigureAwait(false))
        {
            await replacement.WriteAsync(new byte[] { 0x5a }, token).ConfigureAwait(false);
            await replacement.FlushAsync(token).ConfigureAwait(false);
        }
        evidence.ReplacedExecutableSha256 = await HashAsync(executable).ConfigureAwait(false);
        Require(!await validator.VerifyAsync(firstApproval, token).ConfigureAwait(false), "替换文件字节后旧批准仍有效。");
        Require(await settings.GetApprovedExternalPlayerAsync(token).ConfigureAwait(false) is null &&
            settings.Current.ExternalMpvApproval is null && settings.Current.PlaybackMode == PlaybackMode.Embedded &&
            settings.ExternalPlayerStatus == ExternalPlayerStatus.Invalid, "设置未撤销批准并切回内置。");
        evidence.ReplacementRevokedApproval = true;
        // Never run the deliberately modified executable. Restore known official bytes first.
        File.Copy(sourceExecutable, executable, overwrite: true);
        await CheckHashAsync(executable, ExecutableHash).ConfigureAwait(false);
        Require(await settings.GetApprovedExternalPlayerAsync(token).ConfigureAwait(false) is null, "恢复文件自动恢复了批准。");
        try
        {
            await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, token).ConfigureAwait(false);
            throw new SmokeFailure("没有再次批准却允许外部播放。");
        }
        catch (AppException error) when (error.Error.Code == ErrorCodes.ExternalApprovalRequired) { }
        evidence.RestoringFileDidNotRestoreApproval = true;

        stage = "再次明确批准后恢复真实播放";
        await settings.ValidateExternalPlayerAsync(executable, token).ConfigureAwait(false);
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, token).ConfigureAwait(false);
        Require(await settings.GetApprovedExternalPlayerAsync(token).ConfigureAwait(false) is not null, "再次明确批准仍未恢复。");
        var renewed = await coordinator.PlayAsync(new(preparer.Entry.ItemId), token).ConfigureAwait(false);
        await WaitAsync(() => renewed.Snapshot.Phase == PlayerPhase.Playing && handler.Count("Playing", successful: true) == 2,
            scheduler, token).ConfigureAwait(false);
        Require(renewed.Snapshot.EngineKind == EngineKind.External && engines.Count == 2, "再次批准没有创建新外部引擎。");
        await renewed.CloseAsync(token).ConfigureAwait(false);
        Require(outbox.Snapshot.IsEmpty && handler.Count("Stopped", successful: true) == 2, "第二次正常关闭没有停止上报。");
        evidence.ExplicitApprovalRestoredPlayback = true;
        evidence.SuccessfulPlayingReports = handler.Count("Playing", successful: true);
        evidence.SuccessfulStoppedReports = handler.Count("Stopped", successful: true);
        GC.KeepAlive(recipient);
    }

    private static async Task<FileStream> OpenReplacementAsync(string executable, SmokeEvidence evidence, CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(executable, FileMode.Append, FileAccess.Write, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && waiting.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Only transient sharing/lock release is retried, before any replacement byte is written.
                evidence.ReplacementLockRetryCount++;
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }

    private static Process FindOwnedProcess(string executable, DateTime startedAfter)
    {
        Process? owned = null;
        foreach (var candidate in Process.GetProcessesByName("mpv"))
        {
            var retain = false;
            try
            {
                if (candidate.HasExited || !SamePath(candidate.MainModule?.FileName, executable)) continue;
                _ = candidate.Handle;
                Require(candidate.StartTime.ToUniversalTime() >= startedAfter.AddSeconds(-1), "隔离路径进程的启动时间不属于本轮。");
                Require(owned is null, "同一隔离路径出现多个进程，拒绝结束任何进程。");
                owned = candidate;
                retain = true;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            finally { if (!retain) candidate.Dispose(); }
        }
        return owned ?? throw new SmokeFailure("未找到属于本轮唯一隔离路径的 MPV 进程。");
    }

    private static bool SamePath(string? value, string expected) => value is not null &&
        string.Equals(Path.GetFullPath(value), expected, StringComparison.OrdinalIgnoreCase);
    private static async Task WaitAsync(Func<bool> condition, Scheduler scheduler, CancellationToken token)
    {
        while (true)
        {
            scheduler.Drain();
            if (condition()) return;
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }
    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)).ToLowerInvariant();
    }
    private static async Task CheckHashAsync(string path, string expected)
        => Require(await HashAsync(path).ConfigureAwait(false) == expected, "官方运行文件 SHA-256 校验失败，拒绝执行。");
    private static void Require(bool condition, string message) { if (!condition) throw new SmokeFailure(message); }
    private sealed class SmokeFailure(string message) : Exception(message);

    private sealed class Scheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> pending = new();
        public bool TryEnqueue(Action callback) { pending.Enqueue(callback); return true; }
        public void Drain() { while (pending.TryDequeue(out var callback)) callback(); }
    }

    private sealed class LocalPreparer(string sample) : IEntryPreparer
    {
        public PlaybackEntry Entry { get; } = new(Guid.NewGuid().ToString("N"), "本地隔离样片");
        public Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new PreparedPlan([Entry], 0, 0));
        public Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken)
        {
            var source = new EmbyMediaSource { Id = Guid.NewGuid().ToString("N") };
            var candidate = new StreamCandidate(new Uri(sample), "DirectPlay", source, ImmutableDictionary<string, string>.Empty);
            return Task.FromResult(new PreparedEntry(entry,
                new EmbyPlaybackInfo { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSources = [source] }, [candidate], [], startTicks));
        }
        public Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken)
        {
            var candidate = entry.Candidates[candidateIndex];
            return Task.FromResult(new ResolvedCandidate(candidate, new(candidate.Address, candidate.RequiredHeaders, 0), []));
        }
        public Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry,
            ResolvedCandidate selectedCandidate, CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<ResolvedSubtitle>.Empty);
        public Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles) => Task.CompletedTask;
    }

    private sealed record Report(string Kind, bool Successful, string? ItemId, long PositionTicks);
    private sealed class ReportHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Report> reports = new();
        private int rejectStopped = 1;
        public bool RejectStopped { set => Volatile.Write(ref rejectStopped, value ? 1 : 0); }
        public int Count(string kind, bool successful) => reports.Count(report => report.Kind == kind && report.Successful == successful);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri?.Host.EndsWith(".invalid", StringComparison.Ordinal) == true, "诊断禁止真实 HTTP 服务器。");
            var path = request.RequestUri!.AbsolutePath;
            var kind = path.EndsWith("/Stopped", StringComparison.Ordinal) ? "Stopped" : path.EndsWith("/Progress", StringComparison.Ordinal) ? "Progress" : "Playing";
            Require(request.Method == HttpMethod.Post && path.Contains("/Sessions/Playing", StringComparison.Ordinal), "出现诊断范围外 HTTP 请求。");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            var successful = kind != "Stopped" || Volatile.Read(ref rejectStopped) == 0;
            reports.Enqueue(new(kind, successful, body.RootElement.GetProperty("ItemId").GetString(), body.RootElement.GetProperty("PositionTicks").GetInt64()));
            return new(successful ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class ObservedEngine : IPlayerEngine
    {
        private readonly ExternalMpvEngine engine;
        private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>(new() { SingleReader = true, SingleWriter = true });
        private int failures;
        private int shutdowns;
        private int disposed;
        private readonly Task forwarding;
        public ObservedEngine(ExternalMpvEngine engine)
        {
            this.engine = engine;
            forwarding = ForwardAsync(engine);
        }
        public int FailureCount => Volatile.Read(ref failures);
        public int ShutdownCount => Volatile.Read(ref shutdowns);
        public int TerminationCount => FailureCount + ShutdownCount;
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public EngineKind Kind => engine.Kind;
        public ChannelReader<EngineEvent> Events => events.Reader;
        public ValueTask<long> LoadAsync(string url, LoadMode mode, IReadOnlyList<KeyValuePair<string, string>> fileOptions, CancellationToken cancellationToken)
            => engine.LoadAsync(url, mode, fileOptions, cancellationToken);
        public ValueTask CommandAsync(ReadOnlyMemory<string> arguments, CancellationToken cancellationToken) => engine.CommandAsync(arguments, cancellationToken);
        public ValueTask SetAsync(string propertyName, MpvValue value, CancellationToken cancellationToken) => engine.SetAsync(propertyName, value, cancellationToken);
        private async Task ForwardAsync(ExternalMpvEngine source)
        {
            try
            {
                await foreach (var item in source.Events.ReadAllAsync().ConfigureAwait(false))
                {
                    if (item is EngineEvent.Failure) Interlocked.Increment(ref failures);
                    if (item is EngineEvent.Shutdown) Interlocked.Increment(ref shutdowns);
                    await events.Writer.WriteAsync(item).ConfigureAwait(false);
                }
            }
            finally { events.Writer.TryComplete(); }
        }
        public async ValueTask DisposeAsync()
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            await forwarding.ConfigureAwait(false);
            Volatile.Write(ref disposed, 1);
        }
    }
}

internal sealed class SmokeEvidence
{
    public string Status { get; set; } = "Failed";
    public string Stage { get; set; } = "";
    public string? ExceptionType { get; set; }
    public int? HResult { get; set; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string ExecutableSha256 { get; init; } = "";
    public string ReplacedExecutableSha256 { get; set; } = "";
    public int KilledProcessId { get; set; }
    public DateTime KilledProcessStartedUtc { get; set; }
    public bool OwnedProcessExited { get; set; }
    public bool OwnedProcessHandleReleased { get; set; }
    public bool EngineDisposedBeforeReplacement { get; set; }
    public int ReplacementLockRetryCount { get; set; }
    public long PositionBeforeKillTicks { get; set; }
    public long FinalPositionTicks { get; set; }
    public bool PlayingConfirmed { get; set; }
    public bool EngineTerminationObserved { get; set; }
    public int EngineFailureEventCount { get; set; }
    public int EngineShutdownEventCount { get; set; }
    public bool SessionClosed { get; set; }
    public bool StoppedPersistedWhileOffline { get; set; }
    public bool StoppedDeliveredAfterRecovery { get; set; }
    public bool CloseDidNotDuplicateStopped { get; set; }
    public bool ReplacementRevokedApproval { get; set; }
    public bool RestoringFileDidNotRestoreApproval { get; set; }
    public bool ExplicitApprovalRestoredPlayback { get; set; }
    public int SuccessfulPlayingReports { get; set; }
    public int SuccessfulStoppedReports { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SmokeEvidence))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
