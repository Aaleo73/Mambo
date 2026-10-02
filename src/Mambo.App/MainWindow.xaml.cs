using System.Runtime.InteropServices;
using Mambo.App.Composition;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Windowing;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

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
    private readonly nint hwnd;
    private RectInt32 normalBounds;
    private bool closing;

    public MainWindow(ServiceProvider services)
    {
        this.services = services;
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        theme = services.GetRequiredService<ThemeService>();
        settings = services.GetRequiredService<ISettingsService>();
        var context = services.GetRequiredService<WindowContext>();
        context.WindowId = AppWindow.Id;
        context.Handle = hwnd;
        services.GetRequiredService<ToastService>().Attach(DispatcherQueue);

        ExtendsContentIntoTitleBar = true;
        presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);

        var pages = services.GetRequiredService<PageFactory>();
        shell = new ShellView(services.GetRequiredService<ShellViewModel>(), services.GetRequiredService<Navigator>(),
            services.GetRequiredService<ToastService>(), services.GetRequiredService<DialogService>(), pages.Create);
        Content = shell;
        SetTitleBar(shell.TitleBarElement);
        SystemBackdrop = new MamboBackdrop();
        chrome = new WindowChrome(this, presenter, shell, hwnd);
        shell.MinimizeRequested += (_, _) => presenter.Minimize();
        shell.CloseRequested += (_, _) => Close();
        shell.Loaded += (_, _) => { if (shell.XamlRoot is { } root) root.Changed += (_, _) => ApplyMinimumSize(); };

        ApplyTheme();
        theme.Changed += (_, _) => ApplyTheme();
        RestorePlacement();
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => chrome.Dispose();
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
        if ((args.DidPositionChange || args.DidSizeChange) && presenter.State == OverlappedPresenterState.Restored)
            normalBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true;
        var playback = services.GetRequiredService<IPlaybackService>();
        if (playback.Current is not null &&
            !await services.GetRequiredService<DialogService>().ConfirmAsync(new ConfirmRequest("退出应用？", "当前播放将结束并保存进度。", "退出", danger: true)))
            return;
        closing = true;
        var placement = new WindowPlacement(normalBounds.X, normalBounds.Y, normalBounds.Width, normalBounds.Height,
            presenter.State == OverlappedPresenterState.Maximized);
        try { await settings.UpdateAsync(s => s with { Window = placement }); }
        catch (AppException) { }
        await services.GetRequiredService<AppShutdownCoordinator>().CloseAsync();
        await services.DisposeAsync();
        Close();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
