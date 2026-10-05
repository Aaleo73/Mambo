using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.App.Shell;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Mambo.App.Debug;

/// <summary>专用入口必须使用演示服务；检查真实 XAML，不加载视频着色器或读取账号。</summary>
internal static class VideoQualityUiSmoke
{
    internal const string Argument = "--video-quality-ui-smoke";

    internal static async Task RunAsync(MainWindow window, string reportPath)
    {
        var report = new VideoQualityUiReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        var services = window.Services;
        var settings = services.GetRequiredService<ISettingsService>();
        var playback = services.GetRequiredService<IPlaybackService>();
        var toasts = services.GetRequiredService<ToastService>();
        IPlaybackSession? session = null;
        PlayerOverlay? probe = null;
        Grid? host = null;
        try
        {
            Require(report, "DedicatedEntry", Program.Arguments.Contains(Argument, StringComparer.Ordinal));
            await WaitAsync(() => window.Shell.IsLoaded && playback.Current is null, token);
            report.Stage = "画质菜单";
            session = await playback.PreviewAsync(token);
            await WaitAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, session) && active.ViewModel.CanControl, token);
            var player = window.Shell.ActivePlayer!;
            Require(report, "DemoSession", session.Snapshot.EngineKind == EngineKind.Demo);
            player.ShowControlsForSmoke();
            var menu = player.ShowVideoQualityMenuForSmoke();
            await WaitAsync(() => player.HasOpenMenu && menu.ChoiceCount == 3, token);
            Require(report, "StandardChecked", menu.SelectedLabels == "标准");
            Require(report, "ClearChoice", menu.ChooseForSmoke("清晰"));
            await WaitAsync(() => player.ViewModel.VideoQualityMode == VideoQualityMode.Clear && !player.HasOpenMenu && menu.ChoiceCount == 0, token);
            Require(report, "ChoiceAppliesAndReleasesRows", true);
            menu = player.ShowVideoQualityMenuForSmoke();
            await WaitAsync(() => player.HasOpenMenu && menu.SelectedLabels == "清晰", token);
            Require(report, "AnimeChoice", menu.ChooseForSmoke("动画"));
            await WaitAsync(() => player.ViewModel.VideoQualityMode == VideoQualityMode.Anime && !player.HasOpenMenu, token);
            menu = player.ShowVideoQualityMenuForSmoke();
            await WaitAsync(() => player.HasOpenMenu && menu.SelectedLabels == "动画", token);
            Require(report, "StandardChoice", menu.ChooseForSmoke("标准"));
            await WaitAsync(() => player.ViewModel.VideoQualityMode == VideoQualityMode.Standard && !player.HasOpenMenu, token);
            player.ShowVideoQualityMenuForSmoke();
            await WaitAsync(() => player.HasOpenMenu, token);
            var rateMenu = player.ShowMenuForSmoke(tracks: false);
            await WaitAsync(() => rateMenu.ChoiceCount == 6 && menu.ChoiceCount == 0, token);
            Require(report, "FlyoutsRemainExclusive", true);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await WaitAsync(() => !player.HasOpenMenu, token);

            report.Stage = "异步切换与错误";
            var probeSession = new QualityProbeSession();
            probe = new PlayerOverlay(probeSession, services.GetRequiredService<WindowContext>(), toasts, settings,
                services.GetRequiredService<IBulletChatService>());
            host = (Grid)player.FindName("Root");
            host.Children.Add(probe);
            await WaitAsync(() => probe.IsLoaded && probe.ViewModel.CanControl, token);
            probe.ShowControlsForSmoke();
            probeSession.HoldQualityChange();
            var pending = probe.DispatchSmokeVideoQualityAsync(VideoQualityMode.Clear);
            Require(report, "OnlyQualityDisabled", !((Button)probe.FindName("VideoQualityButton")).IsEnabled &&
                ((Button)probe.FindName("PauseButton")).IsEnabled && ((Button)probe.FindName("RateButton")).IsEnabled &&
                ((Slider)probe.FindName("SeekSlider")).IsEnabled);
            await probe.DispatchSmokeKeyAsync(VirtualKey.Space);
            Require(report, "PauseDuringQualityChange", probeSession.Snapshot.IsPaused);
            probeSession.ReleaseQualityChange();
            await pending;
            await WaitAsync(() => probe.ViewModel.CanChangeVideoQuality && probe.ViewModel.VideoQualityMode == VideoQualityMode.Clear, token);
            Require(report, "QualityReenabled", true);

            probeSession.FailNextQualityChange = true;
            await probe.DispatchSmokeVideoQualityAsync(VideoQualityMode.Anime);
            Require(report, "ManualFailureKeepsMode", probe.ViewModel.VideoQualityMode == VideoQualityMode.Clear &&
                probe.ViewModel.CanChangeVideoQuality && toasts.Items.Count(item => item.Text == QualityProbeSession.ManualErrorText) == 1);
            var automaticError = new AppError(AppErrorKind.Player, "diagnostic.video_quality_restore", "画质烟测：自动恢复失败", false);
            probeSession.SetSnapshot(probeSession.Snapshot with { VideoQualityError = automaticError });
            probeSession.SetSnapshot(probeSession.Snapshot with { PositionTicks = TimeSpan.FromSeconds(1).Ticks });
            probeSession.SetSnapshot(probeSession.Snapshot with { PositionTicks = TimeSpan.FromSeconds(2).Ticks });
            Require(report, "AutomaticErrorDeduplicated", toasts.Items.Count(item => item.Text == automaticError.Message) == 1);

            report.Stage = "关闭清理";
            probeSession.HoldQualityChange();
            pending = probe.DispatchSmokeVideoQualityAsync(VideoQualityMode.Anime);
            probe.FreezeForClose();
            await pending;
            probeSession.SetSnapshot(probeSession.Snapshot with
            {
                VideoQualityError = automaticError with { Message = "画质烟测：关闭后应忽略" },
            });
            Require(report, "CloseCancelsAndUnsubscribes", probeSession.SubscriberCount == 0 &&
                !probe.IsClockRunning && !probe.HasOpenMenu && !toasts.Items.Any(item => item.Text == "画质烟测：关闭后应忽略"));
            report.Passed = true;
            report.Stage = "完成";
        }
        catch (Exception error)
        {
            report.ErrorKind = error.GetType().Name;
            Environment.ExitCode = 1;
        }
        finally
        {
            if (probe is not null)
            {
                host?.Children.Remove(probe);
                probe.Dispose();
            }
            try
            {
                if (session is not null) await session.CloseAsync();
            }
            catch (Exception error) when (error is AppException or OperationCanceledException)
            {
                report.Passed = false;
                report.ErrorKind = error.GetType().Name;
                Environment.ExitCode = 1;
            }
            Save(reportPath, report);
            window.Close();
        }
    }

    private static void Require(VideoQualityUiReport report, string name, bool passed)
    {
        report.Checks[name] = passed;
        if (!passed) throw new InvalidOperationException(name);
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(20, token);
        }
    }

    private static void Save(string path, VideoQualityUiReport report)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, VideoQualityUiSmokeJsonContext.Default.VideoQualityUiReport));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Environment.ExitCode = 1; }
    }

    private sealed class QualityProbeSession : IPlaybackSession
    {
        internal const string ManualErrorText = "画质烟测：切换失败";
        private TaskCompletionSource? qualityGate;
        public SessionSnapshot Snapshot { get; private set; } = new()
        {
            Phase = PlayerPhase.Playing, EngineKind = EngineKind.Demo,
            Entry = new PlaybackEntry("quality-ui-probe", "画质界面演示"), DurationTicks = TimeSpan.FromMinutes(1).Ticks,
            DemoColorArgb = 0xFF182536,
        };
        public event EventHandler? SnapshotChanged;
        public int SubscriberCount => SnapshotChanged?.GetInvocationList().Length ?? 0;
        public bool FailNextQualityChange { get; set; }
        public void SetSnapshot(SessionSnapshot value) { Snapshot = value; SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        public void HoldQualityChange() => qualityGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseQualityChange() { qualityGate?.TrySetResult(); qualityGate = null; }
        public async Task SetVideoQualityModeAsync(VideoQualityMode mode, CancellationToken cancellationToken = default)
        {
            SetSnapshot(Snapshot with { IsVideoQualityChanging = true, VideoQualityError = null });
            try
            {
                if (qualityGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (FailNextQualityChange)
                {
                    FailNextQualityChange = false;
                    throw new AppException(new(AppErrorKind.Player, "diagnostic.video_quality_switch", ManualErrorText, false));
                }
                SetSnapshot(Snapshot with { VideoQualityMode = mode });
            }
            finally { SetSnapshot(Snapshot with { IsVideoQualityChanging = false }); }
        }
        public Task TogglePauseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetSnapshot(Snapshot with { IsPaused = !Snapshot.IsPaused });
            return Task.CompletedTask;
        }
        public Task CloseAsync(CancellationToken cancellationToken = default) => CloseAsync(PlaybackEndReason.UserClosed, cancellationToken);
        public Task CloseAsync(PlaybackEndReason reason, CancellationToken cancellationToken = default)
        {
            SetSnapshot(Snapshot with { Phase = PlayerPhase.Closed });
            return Task.CompletedTask;
        }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectEntryAsync(string itemId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepFrameAsync(FrameStepDirection direction, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

internal sealed class VideoQualityUiReport
{
    public bool Passed { get; set; }
    public string Stage { get; set; } = "开始";
    public string ErrorKind { get; set; } = "";
    public Dictionary<string, bool> Checks { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VideoQualityUiReport))]
internal sealed partial class VideoQualityUiSmokeJsonContext : JsonSerializerContext;
