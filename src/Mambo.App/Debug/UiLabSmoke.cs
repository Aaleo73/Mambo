using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinRT;

namespace Mambo.App.Debug;

/// <summary>通过实际 XAML 外壳验证导航与播放；只允许假服务，不读取本机凭据。</summary>
internal static partial class UiLabSmoke
{
    public static async Task RunAsync(MainWindow window, string reportPath)
    {
        var report = new UiLabReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        var services = window.Services;
        var navigation = services.GetRequiredService<Navigator>();
        var library = services.GetRequiredService<ILibraryService>();
        var playback = services.GetRequiredService<IPlaybackService>();
        var settings = services.GetRequiredService<ISettingsService>();
        var presentation = services.GetRequiredService<WindowContext>();
        var fakeVerified = playback is FakePlaybackService && library is FakeLibraryService &&
            settings is FakeSettingsService && services.GetRequiredService<ISessionService>() is FakeSessionService &&
            services.GetRequiredService<IImageService>() is FakeImageService &&
            services.GetRequiredService<ILibraryPreferences>() is FakeLibraryPreferences;
        if (!fakeVerified)
        {
            report.Stage = "不支持的诊断环境";
            report.ErrorKind = "RealBackendRejected";
            await SaveReportAsync(report, reportPath);
            return;
        }
        var shotRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, Path.GetFileNameWithoutExtension(reportPath));
        using var input = new UiInputProbe(window);
        // 六项假服务已验证；只为本轮合成页面保存构造错误，不订阅真实页面。
        void OnPageFailure(Exception error)
        {
            report.PageFailure = error.ToString();
            report.PageFailures.Add(error.ToString());
        }
        window.Shell.PageHost.DiagnosticFailure += OnPageFailure;
        try
        {
            var skeletonStyle = Application.Current.Resources["SkeletonBlockStyle"];
            report.SkeletonResourceRuntimeType = skeletonStyle.GetType().FullName ?? "";
            report.SkeletonResourceIsStyle = skeletonStyle is Style;
            if (window.Shell.PageHost.CurrentPage is ErrorPage) navigation.RetryCurrent();
            await SaveReportAsync(report, reportPath);
            await WaitAsync(() => window.Shell.IsLoaded && window.Shell.ActualWidth > 0, token);
            report.Stage = "取得本轮窗口输入";
            await input.AcquireAsync(token);
            report.AnimationsEnabled = Themes.Motion.AnimationsEnabled;
            using var libraries = library.ObserveLibraries(token);
            await libraries.RefreshAsync(token);
            await WaitAsync(() => libraries.IsInitialized && libraries.Current.Length > 0, token);
            var movies = libraries.Current.First(item => item.Kind == LibraryKind.Movies);
            var series = libraries.Current.First(item => item.Kind == LibraryKind.TvShows);
            report.Stage = "首页";
            await SaveReportAsync(report, reportPath);
            await ShotAsync(window, shotRoot, "home", report, token);
            report.Accessibility.Add(await AccessibilityProbe.RunAsync(window));
            report.Home = window.Shell.PageHost.CurrentPage is HomePage;
            report.WindowLoadedMilliseconds = StartupTimeline.WindowLoadedMilliseconds;
            report.HomeContentMilliseconds = StartupTimeline.HomeContentMilliseconds;

            report.Stage = "资料库和最近播放";
            await SaveReportAsync(report, reportPath);
            navigation.Navigate(Route.Library(movies.Id));
            if (window.Shell.PageHost.CurrentPage is ErrorPage)
            {
                await ShotAsync(window, shotRoot, "library-error", report, token);
                throw new InvalidOperationException("FakeLibraryPageConstructionFailed");
            }
            await WaitAsync(() => window.Shell.PageHost.CurrentPage is LibraryPage page && page.ViewModel.Cards.IsInitialized, token);
            var moviePage = (LibraryPage)window.Shell.PageHost.CurrentPage!;
            report.Library = moviePage.ViewModel.Cards.Items.Count > 0;
            await ShotAsync(window, shotRoot, "library", report, token);
            report.Accessibility.Add(await AccessibilityProbe.RunAsync(window));
            report.Performance = await UiPerformanceProbe.RunAsync(moviePage, token);
            navigation.Navigate(Route.Recent);
            await WaitAsync(() => window.Shell.PageHost.CurrentPage is RecentPage page && page.ViewModel.Cards.IsInitialized, token);
            report.Recent = ((RecentPage)window.Shell.PageHost.CurrentPage!).ViewModel.Cards.Items.Count > 0;
            await ShotAsync(window, shotRoot, "recent", report, token);

            report.Stage = "详情与换季";
            await SaveReportAsync(report, reportPath);
            using var shows = library.ObserveLibrary(series.Id, new(), scopeToken: token);
            await shows.RefreshAsync(token);
            await WaitAsync(() => shows.IsInitialized && shows.Items.Length > 0, token);
            navigation.Navigate(Route.Detail(shows.Items[0].Id));
            await WaitAsync(() => window.Shell.PageHost.CurrentPage is DetailPage page && page.ViewModel.HasContent, token);
            var detail = (DetailPage)window.Shell.PageHost.CurrentPage!;
            await WaitAsync(() => detail.ViewModel.Seasons.Count > 0 && detail.ViewModel.Episodes.Count > 0, token);
            report.Detail = detail.ViewModel.Title.Length > 0;
            await ShotAsync(window, shotRoot, "detail", report, token);
            report.Accessibility.Add(await AccessibilityProbe.RunAsync(window));

            report.Stage = "主题与设置";
            await SaveReportAsync(report, reportPath);
            navigation.Navigate(Route.Settings);
            await settings.UpdateAsync(value => value with { ThemeMode = SettingsThemeMode.Dark }, token);
            await WaitAsync(() => services.GetRequiredService<ThemeService>().Mode == ThemeMode.Dark, token);
            await ShotAsync(window, shotRoot, "settings-dark", report, token);
            await settings.UpdateAsync(value => value with { ThemeMode = SettingsThemeMode.Light }, token);
            await WaitAsync(() => services.GetRequiredService<ThemeService>().Mode == ThemeMode.Light, token);
            report.Theme = true;
            report.Cache = await settings.GetCacheSizeAsync(token) > 0 && settings.LogDirectory == "";
            await ShotAsync(window, shotRoot, "settings-light", report, token);
            report.Accessibility.Add(await AccessibilityProbe.RunAsync(window));
            navigation.Navigate(Route.Search("星"));
            await ShotAsync(window, shotRoot, "search", report, token);
            report.Search = window.Shell.PageHost.CurrentPage is SearchPage;
            report.Stage = "页面淘汰与深滚动恢复";
            await SaveReportAsync(report, reportPath);
            report.Navigation = await NavigationSmoke.RunAsync(window, token, navigationReport =>
            {
                report.Navigation = navigationReport;
                return SaveReportAsync(report, reportPath);
            });
            report.Stage = "页面异常恢复";
            await SaveReportAsync(report, reportPath);
            report.PageRecovery = await PageRecoverySmoke.RunAsync(window, token);

            report.Stage = "播放停止后页面进度刷新";
            await SaveReportAsync(report, reportPath);
            report.PlaybackRefresh = await PlaybackRefreshSmoke.RunAsync(window,
                Path.Combine(shotRoot, "playback-refresh-" + Guid.NewGuid().ToString("N")), token);

            report.Stage = "动效中断与呈现生命周期";
            await SaveReportAsync(report, reportPath);
            report.Motion = new MotionReport();
            await RunMotionAsync(window, input, report.Motion, token);

            report.Stage = "播放层与控制";
            await SaveReportAsync(report, reportPath);
            var session = await playback.PreviewAsync(token);
            await WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && !window.Shell.IsTransitioning &&
                window.Shell.ActivePlayer is { IsLoaded: true } loaded && loaded.VideoSurface.IsDemoAttached, token);
            report.Overlay = navigation.ForwardBlocked && navigation.BackInterceptor?.CanHandle == true;
            var behind = navigation.Current;
            navigation.Navigate(Route.Home);
            report.NavigationLocked = ReferenceEquals(behind, navigation.Current);
            await session.SetRateAsync(1.5, token);
            await session.TogglePauseAsync(token);
            await session.SeekAsync(TimeSpan.FromSeconds(30), token);
            await session.SetVolumeAsync(47, token);
            await WaitAsync(() => session.Snapshot.IsPaused && session.Snapshot.PlaybackRate == 1.5 && session.Snapshot.Volume == 47, token);
            report.Controls = session.Snapshot.PositionTicks >= TimeSpan.FromSeconds(30).Ticks;
            window.Shell.ActivePlayer!.ShowControlsForSmoke();
            await ShotAsync(window, shotRoot, "player", report, token);
            report.Accessibility.Add(await AccessibilityProbe.RunAsync(window));
            presentation.ToggleFullscreen();
            await WaitAsync(() => presentation.IsFullscreen, token);
            window.Shell.ActivePlayer!.ShowControlsForSmoke();
            await ShotAsync(window, shotRoot, "player-fullscreen", report, token);
            presentation.ExitFullscreen();
            report.Fullscreen = !presentation.IsFullscreen;
            await session.CloseAsync(token);
            await WaitAsync(() => playback.Current is null && window.Shell.ActivePlayer is null && !navigation.ForwardBlocked && !window.Shell.IsTransitioning, token);
            report.Closed = true;
            report.PlayerControls = await PlayerControlsSmoke.RunAsync(window, playback, token);

            report.Stage = "50 次播放释放";
            await SaveReportAsync(report, reportPath);
            using var process = Process.GetCurrentProcess();
            await CollectAsync(token);
            process.Refresh();
            await Task.Delay(100, token);
            process.Refresh();
            report.HandlesBefore = process.HandleCount;
            report.HandleTypesBefore = HandleDiagnostics.Capture();
            report.MemoryBefore = process.PrivateMemorySize64;
            var closedPlayers = new List<WeakReference>();
            var closedSurfaces = new List<WeakReference>();
            for (var index = 0; index < 50; index++)
            {
                var repeated = await playback.PreviewAsync(token);
                await WaitAsync(() => repeated.Snapshot.Phase == PlayerPhase.Playing && !window.Shell.IsTransitioning &&
                    window.Shell.ActivePlayer is { IsLoaded: true } loaded && loaded.VideoSurface.IsDemoAttached, token);
                await AwaitNextRenderingAsync(token);
                report.OpenedFrameCycles++;
                closedPlayers.Add(new WeakReference(window.Shell.ActivePlayer!));
                closedSurfaces.Add(new WeakReference(window.Shell.ActivePlayer!.VideoSurface));
                await repeated.CloseAsync(token);
                await WaitAsync(() => playback.Current is null && window.Shell.ActivePlayer is null && !window.Shell.IsTransitioning, token);
                await AwaitNextRenderingAsync(token);
                report.ClosedFrameCycles++;
                report.SessionsClosed++;
                if (window.Shell.LastPlayerFocusRestoreSucceeded && window.Shell.LastPlayerFocusRestoredWithinShell)
                    report.FocusRestoresSucceeded++;
            }
            await Task.Delay(1000, token);
            await CollectAsync(token);
            process.Refresh();
            await Task.Delay(500, token);
            var collection = Stopwatch.StartNew();
            var cleanupFrameCycles = 0;
            while (collection.Elapsed < TimeSpan.FromSeconds(10))
            {
                await CollectAsync(token);
                // 验收协议：GC 后的 WinUI 原生释放由 PerFrameCallback/ReleaseQueuedObjects 清理。
                // 普通 Delay 不保证渲染 tick；连续等待两次 Rendering，才能让前帧完整经过。
                // 此处只覆盖 XAML 清理周期，不证明 GPU 呈现或产品留存根因已修复。
                await AwaitNextRenderingAsync(token);
                cleanupFrameCycles++;
                await AwaitNextRenderingAsync(token);
                cleanupFrameCycles++;
                await Task.Delay(200, token);
                // 原生清理后再收集；第二次 GC 同样需要明确经过 XAML 帧。
                // 每轮记录计数，不把堆转储中的零托管根直接视为已释放。
                await CollectAsync(token, waitForFinalizers: false);
                await AwaitNextRenderingAsync(token);
                cleanupFrameCycles++;
                await AwaitNextRenderingAsync(token);
                cleanupFrameCycles++;
                var playersAlive = closedPlayers.Count(reference => reference.IsAlive);
                var surfacesAlive = closedSurfaces.Count(reference => reference.IsAlive);
                report.CollectionSamples.Add(new(collection.ElapsedMilliseconds, playersAlive, surfacesAlive, cleanupFrameCycles));
                if (playersAlive == 0 && surfacesAlive == 0) break;
            }
            process.Refresh();
            report.HandlesAfter = process.HandleCount;
            report.HandleTypesAfter = HandleDiagnostics.Capture();
            report.MemoryAfter = process.PrivateMemorySize64;
            report.RetainedPlayers = closedPlayers.Count(reference => reference.IsAlive);
            report.RetainedSurfaces = closedSurfaces.Count(reference => reference.IsAlive);
            report.RetainedPlayerIndices = closedPlayers.Select((reference, index) => (reference, index))
                .Where(item => item.reference.IsAlive).Select(item => item.index).ToArray();
            report.LibMpvLoaded = process.Modules.Cast<ProcessModule>().Any(module => module.ModuleName.Equals("libmpv-2.dll", StringComparison.OrdinalIgnoreCase));
            report.PlayerPresentationReleased = window.Shell.ActivePlayer is null && window.Shell.RetiringPlayer is null &&
                !window.Shell.IsTransitioning && !navigation.ForwardBlocked;
            report.Passed = report.Home && report.Library && report.Recent && report.Detail && report.Theme && report.Cache &&
                report.Search && report.Overlay && report.NavigationLocked && report.Controls && report.Fullscreen && report.Closed &&
                report.SessionsClosed == 50 && report.FocusRestoresSucceeded == 50 &&
                report.OpenedFrameCycles == 50 && report.ClosedFrameCycles == 50 &&
                report.CollectionSamples.LastOrDefault() is
                    { ElapsedMilliseconds: <= 10_000, PlayersAlive: 0, SurfacesAlive: 0, CleanupFrameCycles: >= 4 } &&
                report.RetainedPlayers == 0 && report.RetainedSurfaces == 0 &&
                !report.LibMpvLoaded && report.PlayerControls?.Passed == true && report.Motion?.Passed == true &&
                report.PlayerPresentationReleased &&
                report.Accessibility.Count == 5 && report.Accessibility.All(item => item.Status == "Passed") &&
                report.Navigation?.Passed == true && report.PageRecovery?.Passed == true && report.PlaybackRefresh?.Passed == true;
            report.Stage = report.Passed ? "完成" : "界面检查未通过";
            if ((report.RetainedPlayers > 0 || report.RetainedSurfaces > 0) &&
                Environment.GetEnvironmentVariable("MAMBO_UI_DIAG_HOLD_RETAINED") == "1")
            {
                // 仅当前假数据回归的失败现场；保持未通过，供类型/引用根诊断。
                report.DiagnosticFakeServicesVerified = fakeVerified;
                report.DiagnosticProcessId = Environment.ProcessId;
                report.DiagnosticProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                report.Stage = "释放诊断暂停";
                await SaveReportAsync(report, reportPath);
                await Task.Delay(TimeSpan.FromSeconds(60), token);
            }
        }
        catch (Exception error)
        {
            report.Passed = false;
            report.FailureStage = report.Stage;
            report.ErrorKind = error is UiInputProbe.InputFailure ? error.Message : error.GetType().Name;
            report.HResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
        }
        finally
        {
            window.Shell.PageHost.DiagnosticFailure -= OnPageFailure;
            try { await CloseFromCaptionAsync(window, report, reportPath); }
            catch (Exception error)
            {
                window.BeforeFinalCloseForSmoke = null;
                report.Passed = false;
                report.ErrorKind = error.GetType().Name;
                try { await window.CloseForSmokeAsync(); }
                catch (Exception cleanupError) { report.ErrorKind = cleanupError.GetType().Name; }
                report.CompletedAtUtc = DateTimeOffset.UtcNow;
                await SaveReportAsync(report, reportPath);
                window.Close();
            }
        }
    }

    private static async Task CloseFromCaptionAsync(MainWindow window, UiLabReport report, string reportPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var playback = window.Services.GetRequiredService<IPlaybackService>();
        var dialogs = window.Services.GetRequiredService<DialogService>();
        if (playback.Current is not null) throw new InvalidOperationException("CaptionCloseRequiresIdlePlayback");
        // 不先调用 CloseForSmokeAsync：真实 caption 请求必须亲自完成确认、停止播放与窗口清理。
        // 此会话仅验证关窗，原 50 次开关/释放样本和五轮 UIA 均已结束且保持原样。
        var session = await playback.PreviewAsync(token);
        await WaitAsync(() => session.Snapshot.Phase == PlayerPhase.Playing && window.Shell.ActivePlayer is { IsLoaded: true }, token);
        var caption = window.Shell.FindName("CloseButton").As<Button>();
        var dialog = window.Shell.FindName("Dialogs").As<DialogHost>();
        Invoke(caption);
        await WaitAsync(() => dialogs.IsOpen && dialog.Visibility == Visibility.Visible, token);
        var pendingCount = window.CloseRequestCountForSmoke;
        Invoke(caption);
        await Task.Yield();
        report.CaptionCloseReentryIgnored = window.CloseRequestCountForSmoke == pendingCount && dialogs.IsOpen &&
            window.IsCloseRequestPendingForSmoke && !window.HasCompletedShutdownForSmoke;
        Invoke(dialog.FindName("CancelButton").As<Button>());
        await WaitAsync(() => !dialogs.IsOpen && !window.IsCloseRequestPendingForSmoke, token);
        report.CaptionCloseCancelled = !window.HasCompletedShutdownForSmoke && ReferenceEquals(playback.Current, session) &&
            session.Snapshot.Phase == PlayerPhase.Playing && window.Shell.IsLoaded;

        var shutdownRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.BeforeFinalCloseForSmoke = async () =>
        {
            report.CaptionCloseConfirmed = session.Snapshot.Phase == PlayerPhase.Closed && playback.Current is null;
            report.CaptionCloseCleanupCompleted = window.HasCompletedShutdownForSmoke && window.Shell.ActivePlayer is null;
            report.Passed = report.Passed && report.CaptionCloseCancelled && report.CaptionCloseReentryIgnored &&
                report.CaptionCloseConfirmed && report.CaptionCloseCleanupCompleted;
            report.Stage = report.Passed ? "完成" : "界面检查未通过";
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            await SaveReportAsync(report, reportPath);
            shutdownRecorded.TrySetResult();
        };
        Invoke(caption);
        await WaitAsync(() => dialogs.IsOpen && dialog.Visibility == Visibility.Visible, token);
        Invoke(dialog.FindName("DangerButton").As<Button>());
        await shutdownRecorded.Task.WaitAsync(token);

        static void Invoke(Button button)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("CaptionCloseInvokeUnavailable");
            invoke.Invoke();
        }
    }

    internal static async Task AwaitNextRenderingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = deadline.Token.Register(() => complete.TrySetCanceled(deadline.Token));
        void OnRendering(object? sender, object args) => complete.TrySetResult();

        // 只证明挂接/移除后经过真实 XAML 渲染回调，不据此宣称 GPU 已呈现。
        // 订阅仅持有完成源；它不会延长播放层或 Surface 的存活。
        CompositionTarget.Rendering += OnRendering;
        try { await complete.Task; }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private static async Task CollectAsync(CancellationToken token, bool waitForFinalizers = true)
    {
        token.ThrowIfCancellationRequested();
        // WinUI 的 UIAffinityReleaseQueue 通过 BuildTreeService 的 UI tick 执行清理。
        // 终结器的 COM modal wait 仅泵部分消息，不能代替正常 DispatcherQueue。
        // 后台仅触发 GC/等待终结器；调用方 await 保留 UI 上下文，让窗口继续正常调度。
        await Task.Run(() =>
        {
            GC.Collect();
            if (waitForFinalizers) GC.WaitForPendingFinalizers();
        }, token);
        // 已开始的 GC/终结器等待不能取消；先观察任务完成，避免关闭 HWND 时留下后台回收。
        token.ThrowIfCancellationRequested();
    }

    private static async Task SaveReportAsync(UiLabReport report, string reportPath)
    {
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            // 所有验收读取者都等进程退出后解析终报；运行中的检查点可能尚未写完。
            // 不反复替换文件身份：允许写入但未共享 Delete 的观察句柄会阻止 Replace/Move。
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, UiLabJsonContext.Default.UiLabReport));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 固定诊断字段区分报告落盘与页面操作失败，不保存异常文本或文件路径。
            report.ReportWriteErrorKind = error.GetType().Name;
            report.ReportWriteHResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
            throw;
        }
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(20, token);
    }

    private static async Task ShotAsync(MainWindow window, string directory, string name, UiLabReport report, CancellationToken token)
    {
        report.Stage = $"截图/{name}/页面交接";
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        report.Stage = $"截图/{name}/Hero前景";
        if (window.Shell.PageHost.CurrentPage is HomePage home) await home.PendingPresentation.WaitAsync(token);
        report.Stage = $"截图/{name}/共享背景";
        await window.Services.GetRequiredService<BrowseTransitionCoordinator>().PendingBackdrop.WaitAsync(token);
        report.Stage = $"截图/{name}/播放面";
        await window.Shell.PendingPresentation.WaitAsync(token);
        report.Stage = $"截图/{name}/图片就绪";
        await WaitAsync(() => !HasPendingImage(window.Shell), token);
        report.Stage = $"截图/{name}/实际布局帧";
        // RenderTargetBitmap 不包含窗口的原生系统 backdrop；用同主题底色保证离屏截图可读。
        var snapshotRoot = (Microsoft.UI.Xaml.Controls.Grid)window.Shell.FindName("Root");
        var oldBackground = snapshotRoot.Background;
        var dark = window.Shell.ActualTheme == ElementTheme.Dark;
        snapshotRoot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 24, 26, 31) : Windows.UI.Color.FromArgb(255, 246, 247, 249));
        var bitmap = new RenderTargetBitmap();
        try
        {
            await AwaitNextRenderingAsync(token);
            await bitmap.RenderAsync(window.Shell);
        }
        finally { snapshotRoot.Background = oldBackground; }
        var pixels = await bitmap.GetPixelsAsync();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".png");
        await File.WriteAllBytesAsync(path, [], token);
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    private static bool HasPendingImage(DependencyObject root)
    {
        if (root is RemoteImage { IsLoaded: true } image && (image.IsLoading || image.IsRevealing)) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (HasPendingImage(VisualTreeHelper.GetChild(root, index))) return true;
        return false;
    }
}

internal sealed class UiLabReport
{
    public bool DiagnosticFakeServicesVerified { get; set; }
    public int DiagnosticProcessId { get; set; }
    public long DiagnosticProcessStartUtcTicks { get; set; }
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public bool Passed { get; set; }
    public bool AnimationsEnabled { get; set; }
    public bool PlayerPresentationReleased { get; set; }
    public string Stage { get; set; } = "等待外壳";
    public string FailureStage { get; set; } = "";
    public string ErrorKind { get; set; } = "";
    public string HResult { get; set; } = "";
    public string ReportWriteErrorKind { get; set; } = "";
    public string ReportWriteHResult { get; set; } = "";
    public string PageFailure { get; set; } = "";
    public List<string> PageFailures { get; set; } = [];
    public string SkeletonResourceRuntimeType { get; set; } = "";
    public bool SkeletonResourceIsStyle { get; set; }
    public bool Home { get; set; }
    public bool Library { get; set; }
    public bool Recent { get; set; }
    public bool Detail { get; set; }
    public bool Search { get; set; }
    public bool Theme { get; set; }
    public bool Cache { get; set; }
    public bool Overlay { get; set; }
    public bool NavigationLocked { get; set; }
    public bool Controls { get; set; }
    public bool Fullscreen { get; set; }
    public bool Closed { get; set; }
    public bool CaptionCloseCancelled { get; set; }
    public bool CaptionCloseReentryIgnored { get; set; }
    public bool CaptionCloseConfirmed { get; set; }
    public bool CaptionCloseCleanupCompleted { get; set; }
    public bool LibMpvLoaded { get; set; }
    public int SessionsClosed { get; set; }
    public int OpenedFrameCycles { get; set; }
    public int ClosedFrameCycles { get; set; }
    public int FocusRestoresSucceeded { get; set; }
    public int HandlesBefore { get; set; }
    public int HandlesAfter { get; set; }
    public Dictionary<string, int> HandleTypesBefore { get; set; } = [];
    public Dictionary<string, int> HandleTypesAfter { get; set; } = [];
    public long MemoryBefore { get; set; }
    public long MemoryAfter { get; set; }
    public int RetainedPlayers { get; set; }
    public int RetainedSurfaces { get; set; }
    public int[] RetainedPlayerIndices { get; set; } = [];
    public List<UiCollectionSample> CollectionSamples { get; set; } = [];
    public long WindowLoadedMilliseconds { get; set; }
    public long HomeContentMilliseconds { get; set; }
    public UiPerformanceReport? Performance { get; set; }
    public PlayerControlsReport? PlayerControls { get; set; }
    public List<AccessibilityReport> Accessibility { get; set; } = [];
    public NavigationReport? Navigation { get; set; }
    public PageRecoveryReport? PageRecovery { get; set; }
    public PlaybackRefreshReport? PlaybackRefresh { get; set; }
    public MotionReport? Motion { get; set; }
}

internal sealed class MotionReport
{
    public bool Passed { get; set; }
    public string Stage { get; set; } = "AcquireWindow";
    public string FailureKind { get; set; } = "";
    public Dictionary<string, bool> Checks { get; set; } = [];
    public Dictionary<string, double> Measurements { get; set; } = [];
    public double ElapsedMilliseconds { get; set; }
}

// CleanupFrameCycles 是本次回收窗口中明确等待完成的累计 XAML Rendering 次数，不是 GPU 帧数。
internal sealed record UiCollectionSample(long ElapsedMilliseconds, int PlayersAlive, int SurfacesAlive, int CleanupFrameCycles);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiLabReport))]
internal sealed partial class UiLabJsonContext : JsonSerializerContext;
