using System.Numerics;
using System.ComponentModel;
using Mambo.App.Images;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mambo.App.Shell;

/// <summary>外壳：40px 标题栏、208px 侧栏、内容区（PageHost）以及通知、对话框覆盖层。</summary>
public sealed partial class ShellView : UserControl, IBackInterceptor, IDisposable
{
    private readonly Navigator navigator;
    private readonly IPlaybackService playback;
    private readonly ISettingsService settings;
    private readonly WindowContext window;
    private readonly ToastService toasts;
    private readonly ToastHost toastHost;
    private readonly DialogService dialogs;
    private DetailViewModel? titledDetail;
    private Control? previousFocus;
    private Control? coveredPage;
    private bool coveredPageEnabled;
    private string pageTitle = "";
    private PlayerOverlay? player;
    private IPlaybackSession? startingSession;
    private Task pendingPreferenceSaves = Task.CompletedTask;
    private int playerPresentationVersion;
    private CompositionRoundedRectangleGeometry? wellClip;
    private bool disposed;

    public ShellView(ShellViewModel viewModel, Navigator navigator, ToastService toasts, DialogService dialogs, Func<Route, FrameworkElement> pageFactory,
        IPlaybackService playback, WindowContext window, ISettingsService settings, Action<Exception>? reportPageFailure = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ViewModel = viewModel;
        this.navigator = navigator;
        this.playback = playback;
        this.settings = settings;
        this.window = window;
        this.toasts = toasts;
        this.dialogs = dialogs;
        InitializeComponent();
        Sidebar = new SidebarView(viewModel, navigator);
        SidebarSlot.Child = Sidebar;
        toastHost = new ToastHost(toasts);
        OverlayLayer.Children.Add(toastHost);
        dialogs.Attach(Dialogs);
        CoverTransition.Attach(TransitionLayer);
        Pages.Initialize(pageFactory, navigator, reportPageFailure);
        navigator.Navigated += OnNavigated;
        viewModel.AccountChanged += OnAccountChanged;
        playback.SessionStarted += OnSessionStarted;
        playback.SessionEnded += OnSessionEnded;
        playback.EntrySkipped += OnEntrySkipped;
        window.PresentationChanged += OnPresentationChanged;
        Well.SizeChanged += (_, _) => UpdateWellClip();
        TitleBar.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        NavButtons.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        CenterContent.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        InitializeShortcuts();
        OnNavigated(null, new NavigatedEventArgs(null, navigator.Current, NavigationMode.New));
        Loaded += (_, _) => { if (!CanHandle) Sidebar.FocusNavigation(); };
        if (playback.Current is { } current) ShowPlayer(current);
    }

    public ShellViewModel ViewModel { get; }
    public SidebarView Sidebar { get; }
    public PageHost PageHost => Pages;
    public FrameworkElement TitleBarElement => TitleBar;
    public FrameworkElement MaximizeElement => MaximizeButton;
    internal PlayerOverlay? ActivePlayer => player;
    internal bool LastPlayerFocusRestoreSucceeded { get; private set; }
    internal bool LastPlayerFocusRestoredWithinShell { get; private set; }
    public bool CanHandle => player is not null || startingSession is not null;
    public IEnumerable<FrameworkElement> PassthroughElements => [NavButtons, MinimizeButton, CloseButton, CenterContent];

    public event EventHandler? TitleBarLayoutChanged;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? CloseRequested;

    public void SetLiveResize(bool active) => player?.SetLiveResize(active);

    /// <summary>系统材质尚未连接或不可用时，底层实色仍随 XAML 主题变化。</summary>
    internal void SetSystemBackdropAvailable(bool available)
    {
        if (disposed) return;
        BackdropFallback.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
    }

    internal bool IsBackdropFallbackVisible => BackdropFallback.Visibility == Visibility.Visible;

    public bool TryHandleBack()
    {
        if (player is { } active) { _ = active.CloseAsync(); return true; }
        if (startingSession is not { } pending) return false;
        playerPresentationVersion++;
        _ = ClosePendingSessionAsync(pending);
        return true;
    }

    private async Task ClosePendingSessionAsync(IPlaybackSession session)
    {
        try
        {
            await pendingPreferenceSaves;
            await session.CloseAsync();
        }
        catch (AppException error) { toasts.Show(ToastKind.Error, error.Error.Message); }
        catch (OperationCanceledException) { }
    }

    internal async Task FlushPlaybackPreferencesAsync()
    {
        Task pending;
        PlayerOverlay? active;
        do
        {
            pending = pendingPreferenceSaves;
            active = player;
            await pending;
            if (active is not null) await active.FlushPreferencesAsync();
        } while (!ReferenceEquals(pending, pendingPreferenceSaves) || !ReferenceEquals(active, player));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        playerPresentationVersion++;
        startingSession = null;
        Bindings.StopTracking();
        CoverTransition.Detach(TransitionLayer);
        playback.SessionStarted -= OnSessionStarted;
        playback.SessionEnded -= OnSessionEnded;
        playback.EntrySkipped -= OnEntrySkipped;
        window.PresentationChanged -= OnPresentationChanged;
        navigator.Navigated -= OnNavigated;
        ViewModel.AccountChanged -= OnAccountChanged;
        DetachPageTitle();
        if (player is { } active)
        {
            pendingPreferenceSaves = Task.WhenAll(pendingPreferenceSaves, active.PendingPreferenceSave);
            active.Dispose();
        }
        player = null;
        if (ReferenceEquals(navigator.BackInterceptor, this)) navigator.BackInterceptor = null;
        navigator.ForwardBlocked = false;
        Pages.Clear();
        var visual = ElementCompositionPreview.GetElementVisual(Pages);
        var clip = visual.Clip;
        visual.Clip = null;
        clip?.Dispose();
        wellClip?.Dispose();
        wellClip = null;
    }

    private void OnSessionStarted(object? sender, PlaybackSessionEventArgs e) => ShowPlayer(e.Session);
    // UI 事件适配器：意外的初始化异常继续交给 XAML 的未处理异常通道，不能变成未观察 Task。
    private async void ShowPlayer(IPlaybackSession session) => await ShowPlayerAsync(session);
    private async Task ShowPlayerAsync(IPlaybackSession session)
    {
        if (disposed || ReferenceEquals(player?.Session, session) || ReferenceEquals(startingSession, session)) return;
        if (CanHandle) RemovePlayer();
        var version = ++playerPresentationVersion;
        startingSession = session;
        pageTitle = ViewModel.TitleText;
        previousFocus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        Well.IsHitTestVisible = false;
        SidebarSlot.IsHitTestVisible = false;
        Sidebar.IsEnabled = false;
        coveredPage = Pages.CurrentPage as Control;
        coveredPageEnabled = coveredPage?.IsEnabled ?? false;
        if (coveredPage is not null) coveredPage.IsEnabled = false;
        (Pages.CurrentPage as HomePage)?.SetCovered(true);
        CenterContent.Visibility = Visibility.Collapsed;
        TitleBar.RequestedTheme = ElementTheme.Dark;
        PlayerCaptionBackground.Visibility = Visibility.Visible;
        SetPlayerToastResources();
        toastHost.RequestedTheme = ElementTheme.Dark;
        navigator.BackInterceptor = this;
        navigator.ForwardBlocked = true;
        // 外部关闭可以先结束会话、再销毁 Overlay；等旧布局写入完成后才读取 Current。
        await pendingPreferenceSaves;
        if (disposed || version != playerPresentationVersion || !ReferenceEquals(playback.Current, session)) return;
        startingSession = null;
        player = new PlayerOverlay(session, window, toasts, settings);
        player.TitleChanged += OnPlayerTitleChanged;
        PlayerSlot.Children.Add(player);
        PlayerSlot.Visibility = Visibility.Visible;
        OnPlayerTitleChanged(null, EventArgs.Empty);
        OnPresentationChanged(null, EventArgs.Empty);
    }

    private void OnPlayerTitleChanged(object? sender, EventArgs e)
    {
        if (player is not null) ViewModel.TitleText = player.ViewModel.ShellTitle;
    }

    private void OnSessionEnded(object? sender, PlaybackSessionEventArgs e)
    {
        // 替换时旧会话先结束，然后才会通知新会话启动。
        if (!ReferenceEquals(player?.Session, e.Session) && !ReferenceEquals(startingSession, e.Session)) return;
        RemovePlayer();
        if (e.EndReason == PlaybackEndReason.SeasonEnded)
            toasts.Show(ToastKind.Info, "本季已播放完", duration: TimeSpan.FromSeconds(6));
        else if (e.EndReason == PlaybackEndReason.Failed)
            toasts.Show(ToastKind.Warning, "播放意外中断，已保存最新进度", duration: TimeSpan.FromSeconds(8));
    }

    private void OnEntrySkipped(object? sender, PlaybackEntrySkippedEventArgs e) =>
        toasts.Show(ToastKind.Warning, "有一集无法加入连播，已跳过", duration: TimeSpan.FromSeconds(6));

    private void RemovePlayer()
    {
        if (!CanHandle) return;
        playerPresentationVersion++;
        var old = player;
        var restoreTarget = previousFocus;
        previousFocus = null;
        LastPlayerFocusRestoreSucceeded = false;
        LastPlayerFocusRestoredWithinShell = false;
        var focusRestored = false;
        try
        {
            window.ExitFullscreen();
            Well.IsHitTestVisible = true;
            SidebarSlot.IsHitTestVisible = true;
            Sidebar.IsEnabled = true;
            if (coveredPage is not null) coveredPage.IsEnabled = coveredPageEnabled;
            (Pages.CurrentPage as HomePage)?.SetCovered(false);
            CenterContent.Visibility = Visibility.Visible;
            TitleBar.RequestedTheme = ElementTheme.Default;
            PlayerCaptionBackground.Visibility = Visibility.Collapsed;
            toastHost.Resources.ThemeDictionaries.Remove("Default");
            toastHost.Resources.ThemeDictionaries.Remove("HighContrast");
            toastHost.RequestedTheme = ElementTheme.Default;
            OnPresentationChanged(null, EventArgs.Empty);
            // 浏览控件先恢复可聚焦性，再移走焦点；移除仍有焦点的播放器会触发
            // 框架另找临时目标。导航锁保持到播放器和它的资源全部移除之后。
            focusRestored = CanRestoreFocus(restoreTarget, old)
                && restoreTarget!.Focus(FocusState.Programmatic) && HasShellFocus(old);
            if (!focusRestored) focusRestored = Sidebar.FocusNavigation() && HasShellFocus(old);
        }
        finally
        {
            try
            {
                if (old is not null)
                {
                    pendingPreferenceSaves = Task.WhenAll(pendingPreferenceSaves, old.PendingPreferenceSave);
                    old.TitleChanged -= OnPlayerTitleChanged;
                    old.Dispose();
                }
            }
            finally
            {
                PlayerSlot.Children.Clear();
                player = null;
                startingSession = null;
                PlayerSlot.Visibility = Visibility.Collapsed;
                coveredPage = null;
                UpdatePageTitle();
                navigator.BackInterceptor = null;
                navigator.ForwardBlocked = false;
                OnPresentationChanged(null, EventArgs.Empty);
                LastPlayerFocusRestoredWithinShell = HasShellFocus(old);
                LastPlayerFocusRestoreSucceeded = focusRestored && LastPlayerFocusRestoredWithinShell;
            }
        }
    }

    private bool CanRestoreFocus(Control? candidate, PlayerOverlay? old) =>
        candidate is { IsLoaded: true, IsEnabled: true, IsTabStop: true }
        && ReferenceEquals(candidate.XamlRoot, XamlRoot) && IsWithinShell(candidate, old);

    private bool HasShellFocus(PlayerOverlay? old) => XamlRoot is { } root
        && FocusManager.GetFocusedElement(root) is Control { IsLoaded: true, IsEnabled: true } focused
        && ReferenceEquals(focused.XamlRoot, root) && IsWithinShell(focused, old);

    private bool IsWithinShell(DependencyObject? candidate, PlayerOverlay? old)
    {
        for (var current = candidate; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, old)) return false;
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            if (current is Control { IsEnabled: false }) return false;
            if (ReferenceEquals(current, this)) return true;
        }
        return false;
    }

    private void OnPresentationChanged(object? sender, EventArgs e)
    {
        var fullscreen = player is not null && window.IsFullscreen;
        TitleBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        PlayerCaptionBackground.Visibility = CanHandle && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
        SidebarSlot.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Well.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[0].Height = fullscreen ? new GridLength(0) : (GridLength)Application.Current.Resources["TitleBarHeightGridLength"];
        Grid.SetRow(PlayerSlot, fullscreen ? 0 : 1);
        Grid.SetRowSpan(PlayerSlot, fullscreen ? 2 : 1);
        TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetPlayerToastResources()
    {
        // 使用主题字典，使已显示的 Toast 也能跟随系统对比度切换。
        var tokens = Application.Current.Resources.MergedDictionaries.First(dictionary =>
            dictionary.ThemeDictionaries.TryGetValue("HighContrast", out var theme) &&
            theme is ResourceDictionary contrast && contrast.ContainsKey("PlayerPanelBrush"));
        foreach (var name in new[] { "Default", "HighContrast" })
        {
            var theme = (ResourceDictionary)tokens.ThemeDictionaries[name];
            toastHost.Resources.ThemeDictionaries[name] = new ResourceDictionary
            {
                ["PopupBackgroundBrush"] = theme["PlayerPanelBrush"],
                ["PopupStrokeBrush"] = theme["PlayerPanelStrokeBrush"],
            };
        }
    }

    /// <summary>标题栏中间的可交互内容，例如首页 hero 分页点。</summary>
    public void SetCenterContent(UIElement? content)
    {
        CenterContent.Content = content;
        TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMaximizeVisual(bool hover, bool pressed, bool maximized)
    {
        MaximizeHover.Opacity = hover && !pressed ? 1 : 0;
        MaximizePressed.Opacity = pressed ? 1 : 0;
        MaximizeGlyph.Opacity = hover || pressed ? 1 : 0.8;
        MaximizeGlyph.Glyph = (string)Application.Current.Resources[maximized ? "CaptionRestore" : "CaptionMaximize"];
        ToolTipService.SetToolTip(MaximizeButton, maximized ? "还原" : "最大化");
    }

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        DetachPageTitle();
        Pages.Show(e);
        if (Pages.CurrentPage is DetailPage detail)
        {
            titledDetail = detail.ViewModel;
            titledDetail.PropertyChanged += OnDetailTitleChanged;
        }
        UpdatePageTitle();
    }

    private void OnDetailTitleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailViewModel.Title)) UpdatePageTitle();
    }

    private void DetachPageTitle()
    {
        if (titledDetail is not null) titledDetail.PropertyChanged -= OnDetailTitleChanged;
        titledDetail = null;
    }

    private void UpdatePageTitle()
    {
        // 页面自己有大标题，标题栏只在详情页显示片名。
        pageTitle = Pages.CurrentPage is DetailPage detail ? detail.ViewModel.Title : "";
        if (!CanHandle) ViewModel.TitleText = pageTitle;
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        ImageLoader.Current?.ClearDecodedCache();
        DetachPageTitle();
        var keep = navigator.Current.Route.Kind == PageKind.Settings ? Route.Settings : Route.Home;
        Pages.Clear();
        navigator.Reset(keep);
    }

    private void InitializeShortcuts()
    {
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, Sidebar.FocusSearch);
        AddShortcut((VirtualKey)191, VirtualKeyModifiers.None, Sidebar.FocusSearch, () => !IsTextInputFocused());
        AddShortcut(VirtualKey.Divide, VirtualKeyModifiers.None, Sidebar.FocusSearch, () => !IsTextInputFocused());
        AddShortcut((VirtualKey)188, VirtualKeyModifiers.Control, () => navigator.Navigate(Route.Settings));
        AddShortcut(VirtualKey.F5, VirtualKeyModifiers.None, Pages.RefreshCurrent);
        AddShortcut(VirtualKey.R, VirtualKeyModifiers.Control, Pages.RefreshCurrent);
        for (var index = 0; index < 9; index++)
        {
            var libraryIndex = index;
            AddShortcut((VirtualKey)((int)VirtualKey.Number1 + index), VirtualKeyModifiers.Control, () =>
            {
                if (libraryIndex < ViewModel.Libraries.Count) navigator.Navigate(Route.Library(ViewModel.Libraries[libraryIndex].Id));
            });
        }
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action, Func<bool>? canHandle = null)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            if (CanHandle || dialogs.IsOpen || canHandle?.Invoke() == false) return;
            action();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => navigator.GoBack();
    private void OnForwardClick(object sender, RoutedEventArgs e) => navigator.GoForward();
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => window.ToggleMaximize();
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed) { navigator.GoBack(); e.Handled = true; }
        else if (properties.IsXButton2Pressed) { navigator.GoForward(); e.Handled = true; }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || player is not null) return;
        if (startingSession is not null && e.Key == VirtualKey.Escape)
        {
            e.Handled = TryHandleBack();
            return;
        }
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (alt && e.Key == VirtualKey.Left) { navigator.GoBack(); e.Handled = true; }
        else if (alt && e.Key == VirtualKey.Right) { navigator.GoForward(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape && !e.Handled && !IsTextInputFocused()) { e.Handled = navigator.GoBack(); }
    }

    private bool IsTextInputFocused() =>
        XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox;

    /// <summary>内容区只圆左上、右上两个角：裁剪几何向下多延伸一个圆角，把下方两个角藏到可见区域外。</summary>
    private void UpdateWellClip()
    {
        if (disposed) return;
        var visual = ElementCompositionPreview.GetElementVisual(Pages);
        var compositor = visual.Compositor;
        if (wellClip is null)
        {
            wellClip = compositor.CreateRoundedRectangleGeometry();
            wellClip.CornerRadius = new Vector2(11, 11);
            visual.Clip = compositor.CreateGeometricClip(wellClip);
        }
        wellClip.Size = new Vector2((float)Pages.ActualWidth, (float)Pages.ActualHeight + 12);
    }
}
