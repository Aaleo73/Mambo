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
using Mambo.App.Themes;
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
using NativeValue = Mambo.Player.LibMpv.MpvValue;

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
            AnimationsEnabled = Motion.AnimationsEnabled,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
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
            // Only in-memory diagnostic settings are changed. The generated PCM is already
            // very quiet, and the actual playback starts at a deliberately low volume.
            await services.GetRequiredService<ISettingsService>().UpdateAsync(value => value with { Volume = 15 }, token);
            report.AudioFixtureGenerated = fixture.AudioFixtureGenerated;
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
            report.ExpectedPixelWidth = (int)Math.Round(player.VideoViewportElement.ActualWidth * player.VideoSurface.DpiScale);
            report.ExpectedPixelHeight = (int)Math.Round(player.VideoViewportElement.ActualHeight * player.VideoSurface.DpiScale);
            report.ViewportMatched = ViewportMatched(player);
            report.TitleBound = player.ViewModel.Title == LocalPreparer.Title;
            report.ProductionEngineParameters = fixture.EngineCreateCount == 1;
            if (real.Engine is not LibMpvEngine engine) throw new InvalidOperationException("NativeAudioEngineRequired");
            report.Stage = "等待真实音轨和音频输出";
            // Playing alone also succeeds with an unusable AO. Require the real driver,
            // selected external PCM track and output format before testing any controls.
            await WaitAsync(() => ReadAudioState(engine.Core, session.Snapshot, report), token);
            await WaitAsync(() => fixture.Handler.Count("Playing") == 1, token);

            report.Stage = "正式音量和静音控件回写";
            player.ShowControlsForSmoke();
            await player.DispatchSmokeVolumeAsync(10);
            await WaitAsync(() => Math.Abs(session.Snapshot.Volume - 10) < .01
                && Math.Abs(player.ViewModel.Snapshot.Volume - 10) < .01
                && NativeNumber(engine.Core.GetProperty("volume")) is { } volume && Math.Abs(volume - 10) < .01, token);
            report.VolumeControl = true;
            report.NativeVolume = NativeNumber(engine.Core.GetProperty("volume")) ?? 0;
            InvokeButton(player, player.ViewModel.MuteAccessibleName);
            await WaitAsync(() => session.Snapshot.IsMuted && player.ViewModel.Snapshot.IsMuted
                && engine.Core.GetProperty("mute") is NativeValue.Flag { Value: true }, token);
            report.MuteButton = true;
            InvokeButton(player, player.ViewModel.MuteAccessibleName);
            await WaitAsync(() => !session.Snapshot.IsMuted && !player.ViewModel.Snapshot.IsMuted
                && engine.Core.GetProperty("mute") is NativeValue.Flag { Value: false }, token);
            report.UnmuteButton = true;
            report.NativeUnmuted = engine.Core.GetProperty("mute") is NativeValue.Flag { Value: false };

            report.Stage = "真实控件暂停和跳转";
            player.ShowControlsForSmoke();
            InvokeButton(player, player.ViewModel.PauseAccessibleName);
            await WaitAsync(() => session.Snapshot.IsPaused && player.ViewModel.IsPaused, token);
            report.PauseButton = true;
            await player.DispatchSmokeSeekAsync(2);
            await WaitAsync(() => session.Snapshot.PositionTicks >= TimeSpan.TicksPerSecond, token);
            report.SeekControl = true;
            await VerifyVideoQualityAsync(player, session, engine, fixture, report, token);
            InvokeButton(player, player.ViewModel.PauseAccessibleName);
            await WaitAsync(() => !session.Snapshot.IsPaused && !player.ViewModel.IsPaused, token);
            report.ResumeButton = true;
            report.Stage = "确认恢复后真实音频继续输出";
            var audioStart = NativeNumber(engine.Core.GetProperty("time-pos")) ?? 0;
            await WaitAsync(() => ReadAudioState(engine.Core, session.Snapshot, report)
                && engine.Core.GetProperty("mute") is NativeValue.Flag { Value: false }
                && NativeNumber(engine.Core.GetProperty("time-pos")) is { } position && position >= audioStart + .1, token);
            report.AudioPlaybackAdvanced = true;

            report.Stage = "全屏视口与真实交换链尺寸";
            player.ShowControlsForSmoke();
            InvokeButton(player, "切换全屏");
            await WaitAsync(() => services.GetRequiredService<WindowContext>().IsFullscreen &&
                player.VideoSurface.BufferSize.Height != report.BufferHeight && ViewportMatched(player), token);
            report.FullscreenViewportMatched = true;
            report.FullscreenPixelWidth = player.VideoSurface.BufferSize.Width;
            report.FullscreenPixelHeight = player.VideoSurface.BufferSize.Height;
            InvokeButton(player, "切换全屏");
            await WaitAsync(() => !services.GetRequiredService<WindowContext>().IsFullscreen &&
                ViewportMatched(player) && player.VideoSurface.BufferSize == (report.BufferWidth, report.BufferHeight), token);
            report.RestoredViewportMatched = true;

            report.Stage = "真实关闭按钮和交换链解绑";
            var surface = player.VideoSurface;
            void OnEnded(object? sender, PlaybackSessionEventArgs args)
            {
                if (!ReferenceEquals(args.Session, session)) return;
                report.NativeReleaseBeforeShellAwait = window.Shell.ActivePlayer is null && player.IsPresentationFrozen &&
                    surface.BufferSize == (0, 0) && VisualTreeHelper.GetParent(surface) is null;
            }
            fixture.Playback.SessionEnded += OnEnded;
            try
            {
                InvokeButton(player, "关闭播放");
                await WaitAsync(() => report.NativeReleaseBeforeShellAwait, token);
                await UiLabSmoke.AwaitNextRenderingAsync(token);
                report.FrozenFaceRenderingObserved = ReferenceEquals(window.Shell.RetiringPlayer, player) &&
                    player.Content is not null && !player.IsHitTestVisible;
                await WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Closed && fixture.Playback.Current is null
                    && window.Shell.ActivePlayer is null && window.Shell.RetiringPlayer is null && !window.Shell.IsTransitioning, token);
            }
            finally { fixture.Playback.SessionEnded -= OnEnded; }
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
            report.ErrorCode = error switch
            {
                AppException app => app.Error.Code,
                NativeOverlayCheckException check => check.Code,
                _ => "",
            };
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
                && report.ProductionEngineParameters && report.TitleBound && report.Playing && report.Bound && report.SizeMatched && report.ViewportMatched && report.FullscreenViewportMatched
                && report.RestoredViewportMatched && report.NativeReleaseBeforeShellAwait &&
                    (!report.AnimationsEnabled || report.FrozenFaceRenderingObserved)
                && report.AudioFixtureGenerated && report.AudioOutputAvailable && report.AudioTrackSelected && report.ExternalAudioTrackSelected
                && report.AudioOutputSampleRate > 0 && report.AudioOutputChannels > 0 && report.AudioPlaybackAdvanced
                && report.VolumeControl && report.MuteButton && report.UnmuteButton && report.NativeUnmuted && Math.Abs(report.NativeVolume - 10) < .01
                && report.VideoQualitySwitches && report.StandardRestores && report.VideoQualityPauseAndPositionPreserved && report.VideoQualityFileUnchanged
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
        // Measure the video rectangle independently of the episode panel and top gutter.
        var expected = ((int)Math.Round(player.VideoViewportElement.ActualWidth * player.VideoSurface.DpiScale),
            (int)Math.Round(player.VideoViewportElement.ActualHeight * player.VideoSurface.DpiScale));
        return expected.Item1 > 200 && expected.Item2 > 200 && player.VideoSurface.BufferSize == expected;
    }

    private static async Task VerifyVideoQualityAsync(PlayerOverlay player, IPlaybackSession session, LibMpvEngine engine,
        Fixture fixture, NativeOverlayReport report, CancellationToken token)
    {
        report.Stage = "正式画质菜单与真实着色器切换";
        await WaitAsync(() => !session.Snapshot.IsSeeking && player.ViewModel.CanChangeVideoQuality &&
            engine.Core.GetProperty("pause") is NativeValue.Flag { Value: true } &&
            NativeNumber(engine.Core.GetProperty("time-pos")) is >= 1, token);
        var position = NativeNumber(engine.Core.GetProperty("time-pos"))!.Value;
        var snapshotPosition = session.Snapshot.PositionTicks;
        var playlistEntry = NativeNumber(engine.Core.GetProperty("playlist/0/id"))
            ?? throw new NativeOverlayCheckException("quality.playlist-entry-unavailable");
        var shaderFailures = engine.Core.ShaderFailureVersion;
        var selected = VideoQualityMode.Standard;
        foreach (var mode in new[] { VideoQualityMode.Clear, VideoQualityMode.Anime, VideoQualityMode.Standard })
        {
            report.Stage = "正式画质切换：" + QualityLabel(mode);
            player.ShowControlsForSmoke();
            var panel = player.ShowVideoQualityMenuForSmoke();
            await WaitAsync(() => player.HasOpenMenu && panel.IsLoaded && panel.ChoiceCount == 3, token);
            if (panel.SelectedLabels != QualityLabel(selected))
                throw new NativeOverlayCheckException("quality.menu-current-selection");
            if (!panel.ChooseForSmoke(QualityLabel(mode)))
                throw new NativeOverlayCheckException("quality.menu-choice-missing");
            await WaitAsync(() => !player.HasOpenMenu && !player.ViewModel.IsVideoQualityCommandPending &&
                !session.Snapshot.IsVideoQualityChanging, token);
            if (session.Snapshot.VideoQualityMode != mode) throw new NativeOverlayCheckException("quality.session-mode");
            if (player.ViewModel.VideoQualityMode != mode) throw new NativeOverlayCheckException("quality.ui-mode");
            if (!player.ViewModel.CanChangeVideoQuality) throw new NativeOverlayCheckException("quality.ui-remains-disabled");
            AssertNativeQuality(engine.Core, mode);
            if (engine.Core.ShaderFailureVersion != shaderFailures) throw new NativeOverlayCheckException("quality.shader-rendering-failed");
            if (!session.Snapshot.IsPaused || !player.ViewModel.IsPaused ||
                engine.Core.GetProperty("pause") is not NativeValue.Flag { Value: true })
                throw new NativeOverlayCheckException("quality.pause-state-changed");
            if (NativeNumber(engine.Core.GetProperty("time-pos")) is not { } currentPosition ||
                Math.Abs(currentPosition - position) > .05 || Math.Abs(session.Snapshot.PositionTicks - snapshotPosition) > TimeSpan.TicksPerMillisecond * 100)
                throw new NativeOverlayCheckException("quality.position-changed");
            if (fixture.EngineCreateCount != 1 || NativeNumber(engine.Core.GetProperty("playlist/0/id")) != playlistEntry ||
                !ReferenceEquals(player.Session, session) || session.Snapshot.Phase != PlayerPhase.Playing ||
                session.Snapshot.Entry?.ItemId != LocalPreparer.ItemId)
                throw new NativeOverlayCheckException("quality.playback-reloaded");
            if (!ViewportMatched(player) || player.VideoSurface.IsDemoAttached)
                throw new NativeOverlayCheckException("quality.native-surface-changed");
            selected = mode;
            if (mode == VideoQualityMode.Anime) report.VideoQualitySwitches = true;
            if (mode == VideoQualityMode.Standard) report.StandardRestores = true;
        }
        player.ShowControlsForSmoke();
        var standardPanel = player.ShowVideoQualityMenuForSmoke();
        await WaitAsync(() => player.HasOpenMenu && standardPanel.SelectedLabels == "标准", token);
        await player.DispatchSmokeKeyAsync(Windows.System.VirtualKey.Escape);
        await WaitAsync(() => !player.HasOpenMenu && standardPanel.ChoiceCount == 0, token);
        report.VideoQualityPauseAndPositionPreserved = true;
        report.VideoQualityFileUnchanged = true;

    }

    private static string QualityLabel(VideoQualityMode mode) => mode switch
    {
        VideoQualityMode.Clear => "清晰",
        VideoQualityMode.Anime => "动画",
        _ => "标准",
    };

    private static void AssertNativeQuality(MpvCore core, VideoQualityMode mode)
    {
        if (core.GetProperty("glsl-shaders") is not NativeValue.Array shaders)
            throw new NativeOverlayCheckException("quality.native.shader-list-format");
        var standard = mode == VideoQualityMode.Standard;
        if (core.GetProperty("scale") is not NativeValue.Text scale || scale.Value != (standard ? "lanczos" : "ewa_lanczossharp"))
            throw new NativeOverlayCheckException("quality.native.scale");
        if (NativeNumber(core.GetProperty("scale-antiring")) is not { } antiring || Math.Abs(antiring - (standard ? 0 : .6)) > .00001)
            throw new NativeOverlayCheckException("quality.native.antiring");
        if (core.GetProperty("dscale") is not NativeValue.Text { Value: "hermite" })
            throw new NativeOverlayCheckException("quality.native.dscale");
        // SCALER_INHERIT 的字符串形式为空；固定 libmpv 在 NODE API 中将它返回为 INT64 0。
        if (core.GetProperty("cscale") is not (NativeValue.WholeNumber { Value: 0 } or NativeValue.Text { Value: "" }))
            throw new NativeOverlayCheckException("quality.native.cscale-inherit");
        if (standard)
        {
            if (shaders.Values.Count != 0) throw new NativeOverlayCheckException("quality.standard.shaders-not-cleared");
            if (core.GetProperty("glsl-shader-opts") is not NativeValue.Map { Values.Count: 0 })
                throw new NativeOverlayCheckException("quality.standard.shader-options-not-cleared");
        }
        else if (shaders.Values.Count != 1 || shaders.Values[0] is not NativeValue.Text shader ||
            Path.GetFileName(shader.Value) != (mode == VideoQualityMode.Clear ? "Mambo_Clear.glsl" : "Mambo_Anime.glsl"))
        {
            throw new NativeOverlayCheckException("quality.native.shader-path");
        }
    }

    private sealed class NativeOverlayCheckException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    private static bool ReadAudioState(MpvCore core, SessionSnapshot snapshot, NativeOverlayReport report)
    {
        report.AudioOutputDriver = core.GetProperty("current-ao") is NativeValue.Text text ? text.Value : "";
        report.AudioOutputAvailable = report.AudioOutputDriver.Length > 0
            && !report.AudioOutputDriver.Equals("null", StringComparison.OrdinalIgnoreCase);
        var aid = NativeNumber(core.GetProperty("aid"));
        report.SelectedAudioTrackId = aid is > 0 and <= int.MaxValue ? (int)aid.Value : 0;
        var id = report.SelectedAudioTrackId.ToString(CultureInfo.InvariantCulture);
        report.AudioTrackSelected = report.SelectedAudioTrackId > 0 && snapshot.SelectedAudioTrackId == id
            && snapshot.AudioTracks.Any(track => track.Id == id);
        report.ExternalAudioTrackSelected = core.GetProperty("track-list") is NativeValue.Array tracks
            && tracks.Values.OfType<NativeValue.Map>().Any(track =>
                track.Values.GetValueOrDefault("type") is NativeValue.Text { Value: "audio" }
                && NativeNumber(track.Values.GetValueOrDefault("id")) == report.SelectedAudioTrackId
                && track.Values.GetValueOrDefault("external") is NativeValue.Flag { Value: true }
                && track.Values.GetValueOrDefault("selected") is NativeValue.Flag { Value: true });
        var output = core.GetProperty("audio-out-params") as NativeValue.Map;
        report.AudioOutputSampleRate = (int)Math.Clamp(NativeNumber(output?.Values.GetValueOrDefault("samplerate")) ?? 0, 0, int.MaxValue);
        report.AudioOutputChannels = (int)Math.Clamp(NativeNumber(output?.Values.GetValueOrDefault("channel-count")) ?? 0, 0, int.MaxValue);
        return report.AudioOutputAvailable && report.AudioTrackSelected && report.ExternalAudioTrackSelected
            && report.AudioOutputSampleRate > 0 && report.AudioOutputChannels > 0;
    }

    private static double? NativeNumber(NativeValue? value) => value switch
    {
        NativeValue.WholeNumber number => number.Value,
        NativeValue.Number number when double.IsFinite(number.Value) => number.Value,
        NativeValue.Text text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) => number,
        _ => null,
    };

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
        public bool AudioFixtureGenerated { get; }

        public Fixture(Uri sample, string report, string run, ISettingsService settings, IUiScheduler scheduler, IMessenger messenger)
        {
            if (!sample.IsFile) throw new InvalidOperationException("LocalSampleRequired");
            ReportPath = report; RunId = run;
            var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(report)!, "native-overlay-state-" + run));
            Directory.CreateDirectory(paths.Root);
            var tone = Path.Combine(paths.Root, "quiet-tone.wav");
            GenerateQuietTone(tone);
            AudioFixtureGenerated = File.Exists(tone) && new FileInfo(tone).Length == 44 + 48000 * 30 * 4;
            accounts.Set(new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "本地播放验证", Guid.NewGuid().ToString("N"))));
            api = new(settings.Current.DeviceId, Handler);
            Outbox = new(paths, api);
            Playback = new(accounts, new LocalPreparer(sample, tone), async cancellation =>
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

        private static void GenerateQuietTone(string path)
        {
            // Fixed local PCM fixture; no encoder/download and no recording of user audio.
            const int rate = 48000, channels = 2, frames = rate * 30, dataSize = frames * channels * 2;
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write("RIFF"u8); writer.Write(36 + dataSize); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
            writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(dataSize);
            for (var index = 0; index < frames; index++)
            {
                var fade = Math.Min(1, Math.Min(index, frames - 1 - index) / (rate * .02));
                var sample = (short)(256 * fade * Math.Sin(2 * Math.PI * 440 * index / rate));
                writer.Write(sample); writer.Write(sample);
            }
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

    private sealed class LocalPreparer(Uri sample, string audioFixture) : IEntryPreparer
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
            // The singular audio-file alias is CLI-only. append accepts one complete
            // Windows path (including spaces/';'), verified against this libmpv build.
            options.Add(new("audio-files-append", audioFixture));
            options.Add(new("aid", "auto"));
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
    public bool AnimationsEnabled { get; set; }
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
    public bool FullscreenViewportMatched { get; set; }
    public bool RestoredViewportMatched { get; set; }
    public bool NativeReleaseBeforeShellAwait { get; set; }
    public bool FrozenFaceRenderingObserved { get; set; }
    public int FullscreenPixelWidth { get; set; }
    public int FullscreenPixelHeight { get; set; }
    public bool AudioOutputAvailable { get; set; }
    public bool AudioFixtureGenerated { get; set; }
    public string AudioOutputDriver { get; set; } = "";
    public bool AudioTrackSelected { get; set; }
    public bool ExternalAudioTrackSelected { get; set; }
    public int SelectedAudioTrackId { get; set; }
    public int AudioOutputSampleRate { get; set; }
    public int AudioOutputChannels { get; set; }
    public bool AudioPlaybackAdvanced { get; set; }
    public bool VolumeControl { get; set; }
    public double NativeVolume { get; set; }
    public bool MuteButton { get; set; }
    public bool UnmuteButton { get; set; }
    public bool NativeUnmuted { get; set; }
    public bool PauseButton { get; set; }
    public bool SeekControl { get; set; }
    public bool ResumeButton { get; set; }
    public bool VideoQualitySwitches { get; set; }
    public bool StandardRestores { get; set; }
    public bool VideoQualityPauseAndPositionPreserved { get; set; }
    public bool VideoQualityFileUnchanged { get; set; }
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
