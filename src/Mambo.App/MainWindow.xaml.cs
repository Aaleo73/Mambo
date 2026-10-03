using System.Runtime.InteropServices;
using Mambo.App.Composition;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Windowing;
using Mambo.App.Platform;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT;

namespace Mambo.App;

/// <summary>外壳窗口：无系统标题栏、亚克力背景、窗口位置记忆和有序退出。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WinUI 窗口的 Closed 事件释放 chrome。")]
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1500;
    private const int DefaultHeight = 860;
    private const int MinimumWidth = 1100;
    private const int MinimumHeight = 720;
    private readonly ServiceProvider services;
    private readonly OverlappedPresenter presenter;
    private readonly ShellView shell;
    private readonly WindowChrome chrome;
    private readonly ThemeService theme;
    private readonly ISettingsService settings;
    private readonly WindowContext context;
    private readonly WindowResizeHook resizeHook;
    private readonly MamboBackdrop backdrop;
    private readonly PowerRequest power = new();
    private readonly nint hwnd;
    private RectInt32 normalBounds;
    private bool closing;
    private bool finalClose;
    private bool isClosed;
    private Task? closeRequest;

    public MainWindow(ServiceProvider services)
    {
        Debug.StartupTimeline.Mark("MainWindowConstructor");
        this.services = services;
        InitializeComponent();
        Debug.StartupTimeline.Mark("WindowInitializeComponent");
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        theme = services.GetRequiredService<ThemeService>();
        settings = services.GetRequiredService<ISettingsService>();
        context = services.GetRequiredService<WindowContext>();
        context.WindowId = AppWindow.Id;
        context.Handle = hwnd;
        context.FullscreenRequested = SetFullscreen;
        context.MaximizeRequested = ToggleMaximize;
        context.PlaybackActiveRequested = power.SetPlaying;
        Activated += (_, e) => context.SetActive(e.WindowActivationState != WindowActivationState.Deactivated);
        services.GetRequiredService<ToastService>().Attach(DispatcherQueue);
        // XAML 模板里的图片和卡片经静态入口取得这两个服务，先创建它们。
        _ = services.GetRequiredService<Images.ImageLoader>();
        _ = services.GetRequiredService<CardActions>();
        Debug.StartupTimeline.Mark("WindowBasicServicesResolved");

        // Window 已带有 OverlappedPresenter；替换它会让系统重复配置同一个窗口。
        var currentPresenter = AppWindow.Presenter;
        Debug.StartupTimeline.Mark("WindowPresenterRead");
        presenter = currentPresenter.As<OverlappedPresenter>();
        Debug.StartupTimeline.Mark("WindowPresenterProjected");
        presenter.SetBorderAndTitleBar(true, false);
        Debug.StartupTimeline.Mark("SystemTitleBarHidden");
        ExtendsContentIntoTitleBar = true;
        Debug.StartupTimeline.Mark("CustomTitleBarEnabled");
        Debug.StartupTimeline.Mark("WindowPresenterConfigured");

        var pages = services.GetRequiredService<PageFactory>();
        shell = new ShellView(services.GetRequiredService<ShellViewModel>(), services.GetRequiredService<Navigator>(),
            services.GetRequiredService<ToastService>(), services.GetRequiredService<DialogService>(), pages.Create,
            services.GetRequiredService<IPlaybackService>(), context, services.GetRequiredService<ISettingsService>(),
            services.GetRequiredService<AppShutdownCoordinator>().ReportPageFailure);
        Debug.StartupTimeline.Mark("ShellCreated");
        Content = shell;
        Debug.StartupTimeline.Mark("WindowContentAssigned");
        services.GetRequiredService<TitleBarService>().Attach(shell.SetCenterContent);
        SetTitleBar(shell.TitleBarElement);
        backdrop = new MamboBackdrop();
        backdrop.AvailabilityChanged += OnBackdropAvailabilityChanged;
        SystemBackdrop = backdrop;
        Debug.StartupTimeline.Mark("BackdropAttached");
        chrome = new WindowChrome(this, presenter, shell, hwnd);
        resizeHook = new WindowResizeHook(hwnd, shell.SetLiveResize);
        Debug.StartupTimeline.Mark("ChromeReady");
        shell.MinimizeRequested += (_, _) => presenter.Minimize();
        shell.CloseRequested += OnShellCloseRequested;
        shell.Loaded += (_, _) => { if (shell.XamlRoot is { } root) root.Changed += (_, _) => ApplyMinimumSize(); };

        ApplyTheme();
        theme.Changed += OnThemeChanged;
        RestorePlacement();
        Debug.StartupTimeline.Mark("PlacementRestored");
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Program.Instance?.Attach(() => DispatcherQueue.TryEnqueue(ActivateExistingInstance));
    }

    /// <summary>启动后调用一次 RestoreAsync；失败时状态由会话服务公开，首页据此显示。</summary>
    public async void StartSession()
    {
        try { await services.GetRequiredService<ISessionService>().RestoreAsync(); }
        catch (AppException) { }
        catch (OperationCanceledException) { }
    }

    private double Scale => GetDpiForWindow(hwnd) / 96.0;

    private void ApplyTheme() => shell.RequestedTheme = theme.ElementTheme;
    private void OnThemeChanged(object? sender, EventArgs args) => ApplyTheme();
    private void OnBackdropAvailabilityChanged(bool available) => shell.SetSystemBackdropAvailable(available);

    private void SetFullscreen(bool enabled)
    {
        if (context.IsFullscreen == enabled) return;
        if (enabled)
        {
            SetTitleBar(null);
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(presenter);
            SetTitleBar(shell.TitleBarElement);
        }
        UpdatePresentation();
        chrome.UpdateRegions();
    }

    private void ToggleMaximize()
    {
        if (context.IsFullscreen) SetFullscreen(false);
        chrome.ToggleMaximize();
    }

    private void UpdatePresentation() => context.SetPresentation(
        AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen,
        presenter.State == OverlappedPresenterState.Maximized);

    private void ApplyMinimumSize()
    {
        presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWidth * Scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(MinimumHeight * Scale);
    }

    private void RestorePlacement()
    {
        ApplyMinimumSize();
        var saved = settings.Current.Window;
        if (saved is not null && saved.Width > 0 && saved.Height > 0)
        {
            var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null)
            {
                AppWindow.MoveAndResize(rect);
                normalBounds = rect;
                if (saved.IsMaximized) presenter.Maximize();
                return;
            }
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)Math.Round(DefaultWidth * Scale), area.Width);
        var height = Math.Min((int)Math.Round(DefaultHeight * Scale), area.Height);
        normalBounds = new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
        AppWindow.MoveAndResize(normalBounds);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange) && AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped && presenter.State == OverlappedPresenterState.Restored)
            normalBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        if (args.DidPresenterChange || args.DidSizeChange) UpdatePresentation();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (finalClose) return;
        args.Cancel = true;
        await RequestCloseAsync();
    }

    private async void OnShellCloseRequested(object? sender, EventArgs args) => await RequestCloseAsync();

    private Task RequestCloseAsync()
    {
        if (closing || isClosed) return closeRequest ?? Task.CompletedTask;
        closing = true;
        CloseRequestCountForSmoke++;
        return closeRequest = CloseRequestedCoreAsync();
    }

    private async Task CloseRequestedCoreAsync()
    {
        Debug.FakeLifetimeProbe.Mark("WindowClosing");
        // 系统 Closing 与自绘按钮共用确认/清理；先返回原生回调，最终 Close 仍须排队。
        await Task.Yield();
        var playback = services.GetRequiredService<IPlaybackService>();
        if (playback.Current is not null &&
            !await services.GetRequiredService<DialogService>().ConfirmAsync(new ConfirmRequest("退出应用？", "正在播放。关闭应用会结束播放并保存进度，是否退出？", "退出", danger: true)))
        {
            closing = false;
            closeRequest = null;
            return;
        }
        await ShutdownResourcesAsync(savePlacement: true);
        // 只有本轮假 UI 诊断可延迟最终关窗写报告；正常应用不经过这个回调。
        var beforeClose = BeforeFinalCloseForSmoke;
        BeforeFinalCloseForSmoke = null;
        if (Program.Arguments.Contains("--ui-smoke", StringComparer.Ordinal) && beforeClose is not null)
            await beforeClose();
        Debug.FakeLifetimeProbe.Mark("WindowCloseQueued");
        if (!DispatcherQueue.TryEnqueue(Close)) throw new InvalidOperationException("WindowCloseCouldNotBeQueued");
    }

    private async Task ShutdownResourcesAsync(bool savePlacement)
    {
        Debug.FakeLifetimeProbe.Mark("ShutdownStarted");
        if (savePlacement)
        {
            var placement = new WindowPlacement(normalBounds.X, normalBounds.Y, normalBounds.Width, normalBounds.Height,
                presenter.State == OverlappedPresenterState.Maximized);
            try { await settings.UpdateAsync(s => s with { Window = placement }); }
            catch (AppException) { }
            Debug.FakeLifetimeProbe.Mark("PlacementSaved");
        }
        try
        {
            await shell.FlushPlaybackPreferencesAsync();
            await services.GetRequiredService<AppShutdownCoordinator>().CloseAsync();
            Debug.FakeLifetimeProbe.Mark("BackendClosed");
        }
        catch (Exception error) when (error is AppException or IOException or TimeoutException or InvalidOperationException)
        {
            if (!savePlacement) throw;
            services.GetRequiredService<ToastService>().Show(ToastKind.Error, "退出时部分清理未完成，进度将在下次启动时补发。");
        }
        finally
        {
            shell.Dispose();
            Debug.FakeLifetimeProbe.Mark("ShellDisposed");
            await services.DisposeAsync();
            Debug.FakeLifetimeProbe.Mark("ServicesDisposed");
            finalClose = true;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Debug.FakeLifetimeProbe.Mark("WindowClosed");
        isClosed = true;
        backdrop.AvailabilityChanged -= OnBackdropAvailabilityChanged;
        shell.CloseRequested -= OnShellCloseRequested;
        theme.Changed -= OnThemeChanged;
        AppWindow.Changed -= OnAppWindowChanged;
        AppWindow.Closing -= OnClosing;
        context.FullscreenRequested = null;
        context.MaximizeRequested = null;
        context.PlaybackActiveRequested = null;
        resizeHook.Dispose(); chrome.Dispose(); power.Dispose();
        Debug.FakeLifetimeProbe.Mark("WindowHooksDisposed");
    }

    private void ActivateExistingInstance()
    {
        if (closing || isClosed) return;
        if (!context.IsFullscreen && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        Activate();
        _ = SetForegroundWindow(hwnd);
    }

    internal ShellView Shell => shell;
    internal ServiceProvider Services => services;
    internal bool IsCloseRequestPendingForSmoke => closing && !finalClose && !isClosed;
    internal bool HasCompletedShutdownForSmoke => finalClose;
    internal int CloseRequestCountForSmoke { get; private set; }
    internal Func<Task>? BeforeFinalCloseForSmoke { get; set; }
    internal async Task CloseForSmokeAsync()
    {
        // 诊断也走完整窗口释放；真实启动测量不改用户窗口位置。
        // finalClose 先置位，随后写报告、Close，不再触及已释放的 SettingsStore。
        closing = true;
        await ShutdownResourcesAsync(savePlacement: false);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
