using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.App.Composition;
using Mambo.App.Shell;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Mambo.Player.LibMpv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Debug;

/// <summary>隔离账户的本地真实播放：使用正常 Shell/Overlay，绝不调用 Demo/Preview。</summary>
internal static class NativeOverlaySmoke
{
    internal const string Argument = "--native-overlay-smoke";

    internal static ServiceProvider CreateServices(DispatcherQueue queue)
    {
        var sample = Path.GetFullPath(Environment.GetEnvironmentVariable("MAMBO_NATIVE_OVERLAY_SAMPLE") ?? "");
        var report = Path.GetFullPath(Environment.GetEnvironmentVariable("MAMBO_NATIVE_OVERLAY_REPORT") ?? "");
        var run = Environment.GetEnvironmentVariable("MAMBO_NATIVE_OVERLAY_RUN") ?? "";
        if (!Guid.TryParseExact(run, "N", out _) || !File.Exists(sample))
            throw new InvalidOperationException("NativeOverlayDiagnosticInputInvalid");
        var scheduler = new UiScheduler(queue);
        var registrations = new ServiceCollection().AddBackendServices(true, scheduler,
            new FakeOptions { Delay = TimeSpan.FromMilliseconds(10), FailureRate = 0 });
        // Only browsing/account UI is synthetic. Replace the service before resolving anything:
        // no BackendRuntime, WindowsCredentialStore or default AppPaths is constructed.
        registrations.RemoveAll<IPlaybackService>();
        registrations.AddSingleton(p => new Fixture(new Uri(sample), report, run,
            p.GetRequiredService<ISettingsService>(), scheduler, p.GetRequiredService<IMessenger>()));
        registrations.AddSingleton<IPlaybackService>(p => p.GetRequiredService<Fixture>().Playback);
        return registrations.AddUiServices().BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    internal static async Task RunAsync(MainWindow window)
    {
        var fixture = window.Services.GetRequiredService<Fixture>();
        using var currentProcess = Process.GetCurrentProcess();
        var report = new NativeOverlayReport
        {
            RunId = fixture.RunId, ProcessId = currentProcess.Id,
            ProcessStartUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var token = deadline.Token;
        IPlaybackSession? session = null;
        try
        {
            var services = window.Services;
            report.IsolatedServicesVerified = ReferenceEquals(services.GetRequiredService<IPlaybackService>(), fixture.Playback)
                && services.GetRequiredService<ISessionService>() is FakeSessionService
                && services.GetRequiredService<ILibraryService>() is FakeLibraryService
                && services.GetRequiredService<ISettingsService>() is FakeSettingsService
                && services.GetRequiredService<IImageService>() is FakeImageService
                && services.GetRequiredService<ILibraryPreferences>() is FakeLibraryPreferences;
            if (!report.IsolatedServicesVerified) throw new InvalidOperationException("NativeOverlayIsolationRequired");
            report.Stage = "等待正式外壳";
            await WaitAsync(() => window.Shell.IsLoaded && window.Shell.ActualWidth > 0, token);
            report.Stage = "正式入口打开本地片源";
            await services.GetRequiredService<PlaybackLauncher>().PlayAsync(LocalPreparer.ItemId, 0);
            session = fixture.Playback.Current;
            if (session is not PlaybackSession real) throw new InvalidOperationException("RealPlaybackSessionRequired");
            await WaitAsync(() => window.Shell.ActivePlayer is { IsLoaded: true } player
                && ReferenceEquals(player.Session, session), token);
            var player = window.Shell.ActivePlayer!;
            report.FormalOverlayLoaded = true;
            report.Stage = "等待真实播放和交换链";
            await WaitAsync(() =>
            {
                if (session.Snapshot.Phase == PlayerPhase.Failed)
                    throw new AppException(session.Snapshot.Error ?? new(AppErrorKind.Player, ErrorCodes.PlaybackFailed, "本地真实播放失败。", false));
                return session.Snapshot.Phase == PlayerPhase.Playing && player.ViewModel.CanControl
                    && player.VideoSurface.IsLoaded && !player.VideoSurface.IsDemoAttached
                    && player.VideoSurface.BufferSize.Width > 0 && player.VideoSurface.BufferSize.Height > 0
                    && player.VideoSurface.BufferSize == player.VideoSurface.PixelSize && ViewportMatched(player);
            }, token);
            report.RealEmbeddedEngine = real.Engine is LibMpvEngine && session.Snapshot.EngineKind == EngineKind.Embedded;
            report.Playing = session.Snapshot.Phase == PlayerPhase.Playing;
            report.Bound = player.VideoSurface.BufferSize.Width > 0 && player.VideoSurface.BufferSize.Height > 0;
            report.SizeMatched = player.VideoSurface.BufferSize == player.VideoSurface.PixelSize;
            report.BufferWidth = player.VideoSurface.BufferSize.Width;
            report.BufferHeight = player.VideoSurface.BufferSize.Height;
            report.ExpectedPixelWidth = (int)Math.Round(player.ActualWidth * player.VideoSurface.DpiScale);
            report.ExpectedPixelHeight = (int)Math.Round(player.ActualHeight * player.VideoSurface.DpiScale);
            report.ViewportMatched = ViewportMatched(player);
            report.TitleBound = player.ViewModel.Title == LocalPreparer.Title;
            report.ProductionEngineParameters = fixture.EngineCreateCount == 1;
            if (real.Engine is LibMpvEngine engine)
                report.AudioOutputAvailable = engine.Core.GetProperty("current-ao") is Mambo.Player.LibMpv.MpvValue.Text { Value.Length: > 0 };
            await WaitAsync(() => fixture.Handler.Count("Playing") == 1, token);

            report.Stage = "真实控件暂停和跳转";
            player.ShowControlsForSmoke();
            InvokeButton(player, player.ViewModel.PauseAccessibleName);
            await WaitAsync(() => session.Snapshot.IsPaused && player.ViewModel.IsPaused, token);
            report.PauseButton = true;
            await player.DispatchSmokeSeekAsync(2);
            await WaitAsync(() => session.Snapshot.PositionTicks >= TimeSpan.TicksPerSecond, token);
            report.SeekControl = true;
            InvokeButton(player, player.ViewModel.PauseAccessibleName);
            await WaitAsync(() => !session.Snapshot.IsPaused && !player.ViewModel.IsPaused, token);
            report.ResumeButton = true;

            report.Stage = "真实关闭按钮和交换链解绑";
            var surface = player.VideoSurface;
            InvokeButton(player, "关闭播放");
            await WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && fixture.Playback.Current is null
                && window.Shell.ActivePlayer is null, token);
            report.Closed = true;
            report.Detached = surface.BufferSize == (0, 0);
            await WaitAsync(() => fixture.Handler.Count("Stopped") == 1 && fixture.Outbox.Snapshot.IsEmpty, token);
            report.Stopped = true;
            report.OutboxEmpty = true;
            report.ReportSequenceOrdered = fixture.Handler.SequenceIsOrdered;
        }
        catch (Exception error)
        {
            report.ErrorKind = error.GetType().Name;
            report.HResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
            report.ErrorCode = error is AppException app ? app.Error.Code : "";
        }
        finally
        {
            if (session is not null)
            {
                report.LastPlayerPhase = session.Snapshot.Phase.ToString();
                report.PlaybackErrorCode = session.Snapshot.Error?.Code ?? "";
            }
            try
            {
                if (session is { Snapshot.Phase: not PlayerPhase.Closed })
                    await session.CloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                await window.CloseForSmokeAsync().WaitAsync(TimeSpan.FromSeconds(12), CancellationToken.None);
                report.ShutdownCompleted = window.HasCompletedShutdownForSmoke;
            }
            catch (Exception error)
            {
                report.CleanupErrorKind = error.GetType().Name;
                report.CleanupHResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
            }
            report.Passed = report.ErrorKind.Length == 0 && report.CleanupErrorKind.Length == 0
                && report.IsolatedServicesVerified && report.FormalOverlayLoaded && report.RealEmbeddedEngine
                && report.ProductionEngineParameters && report.TitleBound && report.Playing && report.Bound && report.SizeMatched && report.ViewportMatched
                && report.PauseButton && report.SeekControl && report.ResumeButton && report.Closed && report.Detached
                && report.Stopped && report.OutboxEmpty && report.ReportSequenceOrdered && report.ShutdownCompleted;
            if (report.Passed) report.Stage = "完成";
            if (!report.Passed) Environment.ExitCode = 1;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.ReportPath)!);
                await File.WriteAllTextAsync(fixture.ReportPath, JsonSerializer.Serialize(report,
                    NativeOverlayJsonContext.Default.NativeOverlayReport), CancellationToken.None);
            }
            finally
            {
                // Close directly only after explicit shutdown; never rely on AppWindow.Closing.
                if (report.ShutdownCompleted) window.Close();
            }
        }
    }

    private static bool ViewportMatched(PlayerOverlay player)
    {
        // A stale initial 1x1 target must not pass merely because buffer == PixelSize.
        // The normal overlay's VideoHost fills its independently measured viewport.
        var expected = ((int)Math.Round(player.ActualWidth * player.VideoSurface.DpiScale),
            (int)Math.Round(player.ActualHeight * player.VideoSurface.DpiScale));
        return expected.Item1 > 200 && expected.Item2 > 200 && player.VideoSurface.BufferSize == expected;
    }

    private static void InvokeButton(DependencyObject root, string name)
    {
        Button? Find(DependencyObject node)
        {
            if (node is Button button && AutomationProperties.GetName(button) == name) return button;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                if (Find(VisualTreeHelper.GetChild(node, index)) is { } result) return result;
            return null;
        }
        var found = Find(root);
        if (found is not { IsEnabled: true }) throw new InvalidOperationException("NativeOverlayControlUnavailable");
        new ButtonAutomationPeer(found).Invoke();
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(25, token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AccountContext accounts = new();
        private readonly EmbyApi api;
        private bool disposed;
        private int engineCreateCount;
        public string ReportPath { get; }
        public string RunId { get; }
        public PlaybackCoordinator Playback { get; }
        public ReportHandler Handler { get; } = new();
        public StopOutbox Outbox { get; }
        public int EngineCreateCount => Volatile.Read(ref engineCreateCount);

        public Fixture(Uri sample, string report, string run, ISettingsService settings, IUiScheduler scheduler, IMessenger messenger)
        {
            if (!sample.IsFile) throw new InvalidOperationException("LocalSampleRequired");
            ReportPath = report; RunId = run;
            var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(report)!, "native-overlay-state-" + run));
            accounts.Set(new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "本地播放验证", Guid.NewGuid().ToString("N"))));
            api = new(settings.Current.DeviceId, Handler);
            Outbox = new(paths, api);
            Playback = new(accounts, new LocalPreparer(sample), async cancellation =>
            {
                var options = new Dictionary<string, string>
                {
                    ["hwdec"] = settings.Current.HardwareDecoding == HardwareDecodingMode.Off ? "no" : "d3d11va",
                };
                // Match BackendServices production creation, including its initially tiny surface and audio.
                var engine = await LibMpvEngine.CreateAsync(1, 1, headless: false, enableAudio: true,
                    optionOverrides: options, cancellationToken: cancellation).ConfigureAwait(false);
                Interlocked.Increment(ref engineCreateCount);
                return engine;
            }, api, Outbox, settings, scheduler, messenger, TimeProvider.System);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { await Playback.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { await Outbox.DisposeAsync().ConfigureAwait(false); }
                finally { api.Dispose(); accounts.Dispose(); }
            }
        }
    }

    private sealed class LocalPreparer(Uri sample) : IEntryPreparer
    {
        internal const string ItemId = "native-overlay-local";
        internal const string Title = "本地真实播放回归";
        private static readonly PlaybackEntry Entry = new(ItemId, Title);
        public Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ItemId != ItemId) throw new InvalidOperationException("NativeOverlayItemInvalid");
            return Task.FromResult(new PreparedPlan([Entry], 0, Math.Max(0, request.StartTicks ?? 0)));
        }
        public Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new EmbyMediaSource { Id = "native-overlay-source", Container = "mp4" };
            return Task.FromResult(new PreparedEntry(entry,
                new() { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSources = [source] },
                [new(sample, "DirectPlay", source, new Dictionary<string, string>())], [], startTicks));
        }
        public Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
            options.Add(new("http-header-fields", ""));
            options.Add(new("force-media-title", Title));
            if (entry.StartTicks > 0) options.Add(new("start", (entry.StartTicks / (double)TimeSpan.TicksPerSecond).ToString("F3", CultureInfo.InvariantCulture)));
            return Task.FromResult(new ResolvedCandidate(entry.Candidates[candidateIndex],
                new(sample, new Dictionary<string, string>(), 0), options.ToImmutable()));
        }
        public Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry, ResolvedCandidate selectedCandidate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ImmutableArray<ResolvedSubtitle>.Empty);
        }
        public Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles) => Task.CompletedTask;
    }

    private sealed class ReportHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> observations = new();
        public int Count(string kind) => observations.Count(value => value == kind);
        public bool SequenceIsOrdered
        {
            get
            {
                var sequence = observations.ToArray();
                return sequence.Length >= 3 && sequence[0] == "Playing" && sequence[^1] == "Stopped"
                    && sequence.Skip(1).SkipLast(1).All(value => value == "Progress");
            }
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No SocketsHttpHandler: only these synthetic reporting routes are accepted in memory.
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (!path.Contains("/Sessions/Playing", StringComparison.Ordinal))
                throw new InvalidOperationException("NativeOverlayUnexpectedHttpOperation");
            observations.Enqueue(path.EndsWith("/Stopped", StringComparison.Ordinal) ? "Stopped"
                : path.EndsWith("/Progress", StringComparison.Ordinal) ? "Progress" : "Playing");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}

internal sealed class NativeOverlayReport
{
    public string RunId { get; set; } = "";
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public bool Passed { get; set; }
    public string Stage { get; set; } = "开始";
    public bool IsolatedServicesVerified { get; set; }
    public bool ProductionEngineParameters { get; set; }
    public int InitialEngineWidth { get; set; } = 1;
    public int InitialEngineHeight { get; set; } = 1;
    public bool EngineAudioEnabled { get; set; } = true;
    public bool FormalOverlayLoaded { get; set; }
    public bool RealEmbeddedEngine { get; set; }
    public bool TitleBound { get; set; }
    public bool Playing { get; set; }
    public bool Bound { get; set; }
    public bool SizeMatched { get; set; }
    public int BufferWidth { get; set; }
    public int BufferHeight { get; set; }
    public int ExpectedPixelWidth { get; set; }
    public int ExpectedPixelHeight { get; set; }
    public bool ViewportMatched { get; set; }
    public bool AudioOutputAvailable { get; set; }
    public bool PauseButton { get; set; }
    public bool SeekControl { get; set; }
    public bool ResumeButton { get; set; }
    public bool Closed { get; set; }
    public bool Detached { get; set; }
    public bool Stopped { get; set; }
    public bool OutboxEmpty { get; set; }
    public bool ReportSequenceOrdered { get; set; }
    public bool ShutdownCompleted { get; set; }
    public string LastPlayerPhase { get; set; } = "";
    public string PlaybackErrorCode { get; set; } = "";
    public string ErrorKind { get; set; } = "";
    public string HResult { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string CleanupErrorKind { get; set; } = "";
    public string CleanupHResult { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NativeOverlayReport))]
internal sealed partial class NativeOverlayJsonContext : JsonSerializerContext;
