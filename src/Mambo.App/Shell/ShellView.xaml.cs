using System.Numerics;
using System.ComponentModel;
using Mambo.App.Images;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.App.Themes;
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
    private readonly IBulletChatService bulletChat;
    private readonly WindowContext window;
    private readonly BrowseTransitionCoordinator transitions;
    private readonly WindowMotionObserver motionObserver;
    private readonly PageInputScope browseInput;
    private readonly ToastService toasts;
    private readonly ToastHost toastHost;
    private readonly DialogService dialogs;
    private DetailViewModel? titledDetail;
    private Control? previousFocus;
    private string pageTitle = "";
    private string playerTitle = "";
    private PlayerOverlay? retiringPlayer;
    private double retiringPanelWidth;
    private PlayerOverlay? player;
    private IPlaybackSession? startingSession;
    private IPlaybackSession? observedSession;
    private bool externalPlaybackAnnounced;
    private Task pendingPreferenceSaves = Task.CompletedTask;
    private int playerPresentationVersion;
    private readonly PlayerFoldTransition fold;
    private bool browseCovered;
    private bool playerFacing;
    private bool presentationPending;
    private CompositionRoundedRectangleGeometry? wellClip;
    private bool disposed;

    public ShellView(ShellViewModel viewModel, Navigator navigator, ToastService toasts, DialogService dialogs, Func<Route, FrameworkElement> pageFactory,
        IPlaybackService playback, WindowContext window, BrowseTransitionCoordinator transitions, ISettingsService settings, IBulletChatService bulletChat,
        Action<Exception>? reportPageFailure = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ViewModel = viewModel;
        this.navigator = navigator;
        this.playback = playback;
        this.settings = settings;
        this.bulletChat = bulletChat;
        this.window = window;
        this.transitions = transitions;
        this.toasts = toasts;
        this.dialogs = dialogs;
        InitializeComponent();
        fold = new PlayerFoldTransition(BrowseFace, PlayerSlot, BrowseShade, PlayerShade);
        fold.FacingChanged += OnFacingChanged;
        browseInput = new PageInputScope(BrowseFace, transitions.ReportFailure);
        transitions.Attach(HeroBackdrop);
        motionObserver = new WindowMotionObserver(window, DispatcherQueue, OnAnimationsChanged);
        Sidebar = new SidebarView(viewModel, navigator);
        SidebarSlot.Child = Sidebar;
        toastHost = new ToastHost(toasts, window);
        OverlayLayer.Children.Add(toastHost);
        Dialogs.Initialize(window);
        dialogs.Attach(Dialogs);
        Pages.Initialize(pageFactory, navigator, transitions, reportPageFailure);
        Pages.ParkFocus = () => Sidebar.FocusNavigation();
        navigator.Navigated += OnNavigated;
        viewModel.AccountChanged += OnAccountChanged;
        playback.SessionStarted += OnSessionStarted;
        playback.SessionEnded += OnSessionEnded;
        playback.EntrySkipped += OnEntrySkipped;
        window.PresentationChanged += OnPresentationChanged;
        window.ActiveChanged += OnWindowActiveChanged;
        Well.SizeChanged += (_, _) => UpdateWellClip();
        TitleBar.SizeChanged += OnTitleBarSizeChanged;
        Root.SizeChanged += OnRootSizeChanged;
        NavButtons.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        CenterContent.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        InitializeShortcuts();
        OnNavigated(null, new NavigatedEventArgs(null, navigator.Current, NavigationMode.New));
        Loaded += (_, _) => { if (!CanHandle) Sidebar.FocusNavigation(); };
        if (playback.Current is { } current) ObserveSession(current);
    }

    public ShellViewModel ViewModel { get; }
    public SidebarView Sidebar { get; }
    public PageHost PageHost => Pages;
    public FrameworkElement TitleBarElement => TitleBar;
    public FrameworkElement MaximizeElement => MaximizeButton;
    internal PlayerOverlay? ActivePlayer => player;
    internal PlayerOverlay? RetiringPlayer => retiringPlayer;
    internal double FoldProgress => fold.Progress;
    internal bool IsPlayerFacing => playerFacing;
    internal Task PendingPresentation { get; private set; } = Task.CompletedTask;
    internal bool IsTransitioning => presentationPending || fold.IsRunning;
    internal bool LastPlayerFocusRestoreSucceeded { get; private set; }
    internal bool LastPlayerFocusRestoredWithinShell { get; private set; }
    public bool CanHandle => browseCovered;
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
        if (startingSession is not { } pending) return browseCovered;
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
        fold.RequestTarget(false);
        ParkPlayerFocus();
        disposed = true;
        playerPresentationVersion++;
        startingSession = null;
        StopObservingSession();
        fold.FacingChanged -= OnFacingChanged;
        fold.Dispose();
        presentationPending = false;
        browseCovered = false;
        PlayerSlot.Visibility = Visibility.Collapsed;
        Root.SizeChanged -= OnRootSizeChanged;
        TitleBar.SizeChanged -= OnTitleBarSizeChanged;
        Bindings.StopTracking();
        motionObserver.Dispose();
        playback.SessionStarted -= OnSessionStarted;
        playback.SessionEnded -= OnSessionEnded;
        playback.EntrySkipped -= OnEntrySkipped;
        window.PresentationChanged -= OnPresentationChanged;
        window.ActiveChanged -= OnWindowActiveChanged;
        navigator.Navigated -= OnNavigated;
        ViewModel.AccountChanged -= OnAccountChanged;
        DetachPageTitle();
        if (player is { } active)
        {
            PreservePreferences(active.PendingPreferenceSave);
            active.TitleChanged -= OnPlayerTitleChanged;
            active.LayoutChanged -= OnPlayerLayoutChanged;
            active.Dispose();
        }
        player = null;
        ReleaseRetiringPlayer();
        browseInput.Dispose();
        PlayerContent.Children.Clear();
        if (ReferenceEquals(navigator.BackInterceptor, this)) navigator.BackInterceptor = null;
        navigator.ForwardBlocked = false;
        Dialogs.Dispose();
        toastHost.Dispose();
        Pages.Dispose();
        transitions.Dispose();
        var visual = ElementCompositionPreview.GetElementVisual(WellContent);
        var clip = visual.Clip;
        visual.Clip = null;
        clip?.Dispose();
        wellClip?.Dispose();
        wellClip = null;
    }

    private void OnAnimationsChanged(bool enabled)
    {
        if (disposed || enabled) return;
        Pages.SettleTransition();
        transitions.SettleBackdrop();
        SettlePlayerPresentation();
        Motion.SettleDescendants(this);
    }

    private void OnSessionStarted(object? sender, PlaybackSessionEventArgs e) => ObserveSession(e.Session);

    private void ObserveSession(IPlaybackSession session)
    {
        if (disposed || ReferenceEquals(observedSession, session)) return;
        StopObservingSession();
        observedSession = session;
        externalPlaybackAnnounced = false;
        session.SnapshotChanged += OnPlaybackSnapshotChanged;
        OnPlaybackSnapshotChanged(session, EventArgs.Empty);
    }

    private void StopObservingSession()
    {
        if (observedSession is { } session) session.SnapshotChanged -= OnPlaybackSnapshotChanged;
        observedSession = null;
    }

    private void OnPlaybackSnapshotChanged(object? sender, EventArgs e)
    {
        if (disposed || observedSession is not { } session || !ReferenceEquals(playback.Current, session)) return;
        var snapshot = session.Snapshot;
        if (snapshot.Phase is PlayerPhase.Closing or PlayerPhase.Closed) return;
        if (snapshot.EngineKind != EngineKind.External)
        {
            // 指纹复验失败时后端可能回退到内置引擎，以实际会话类型决定是否展示播放层。
            ShowPlayer(session);
            return;
        }
        if (player is not null || startingSession is not null) HidePlayer();
        if (!externalPlaybackAnnounced && snapshot.Phase == PlayerPhase.Playing)
        {
            externalPlaybackAnnounced = true;
            toasts.Show(ToastKind.Info, "已交给外置 MPV 播放");
        }
    }
    // UI 事件适配器：意外的初始化异常继续交给 XAML 的未处理异常通道，不能变成未观察 Task。
    private async void ShowPlayer(IPlaybackSession session)
    {
        if (disposed || session.Snapshot.EngineKind == EngineKind.External ||
            ReferenceEquals(player?.Session, session) || ReferenceEquals(startingSession, session)) return;
        await (PendingPresentation = ShowPlayerAsync(session));
    }
    private async Task ShowPlayerAsync(IPlaybackSession session)
    {
        var version = ++playerPresentationVersion;
        fold.RequestTarget(true);
        presentationPending = true;
        RetireActivePlayer();
        startingSession = session;
        Pages.SettleTransition();
        transitions.SettleBackdrop();
        if (!browseCovered)
        {
            browseCovered = true;
            pageTitle = ViewModel.TitleText;
            previousFocus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
            LastPlayerFocusRestoreSucceeded = false;
            LastPlayerFocusRestoredWithinShell = false;
        }
        if (XamlRoot is { } root && PageInputScope.Contains(BrowseFace, FocusManager.GetFocusedElement(root) as DependencyObject))
            MinimizeButton.Focus(FocusState.Programmatic);
        (Pages.CurrentPage as HomePage)?.SetCovered(true);
        Motion.SetEntranceSuppressed(BrowseFace, true);
        Motion.SetActive(BrowseFace, false);
        browseInput.SetEnabled(false);
        navigator.BackInterceptor = this;
        navigator.ForwardBlocked = true;
        // 新会话必须读到已经提交的布局偏好；等待期间保留导航锁和无活动资源的旧面。
        await pendingPreferenceSaves;
        if (disposed || version != playerPresentationVersion || !ReferenceEquals(playback.Current, session)) return;
        startingSession = null;
        ReleaseRetiringPlayer();
        var current = new PlayerOverlay(session, window, toasts, settings, bulletChat);
        player = current;
        current.SetTransitionActive(true);
        current.TitleChanged += OnPlayerTitleChanged;
        current.LayoutChanged += OnPlayerLayoutChanged;
        PlayerContent.Children.Add(current);
        PlayerSlot.Visibility = Visibility.Visible;
        OnPlayerTitleChanged(null, EventArgs.Empty);
        UpdatePresentationLayout();
        await fold.PlayAsync(true, animate: true);
        if (disposed || version != playerPresentationVersion || !ReferenceEquals(player, current) ||
            !ReferenceEquals(playback.Current, session)) return;
        current.SetTransitionActive(false);
        current.Focus(FocusState.Programmatic);
        UpdateTitleAlignment();
        presentationPending = false;
    }

    private void OnPlayerTitleChanged(object? sender, EventArgs e)
    {
        if (player is null) return;
        playerTitle = player.ViewModel.ShellTitle;
        if (playerFacing) ViewModel.TitleText = playerTitle;
    }

    private void OnFacingChanged(bool facing)
    {
        if (disposed) return;
        playerFacing = facing;
        ViewModel.TitleText = facing ? playerTitle : pageTitle;
        CenterContent.Visibility = facing ? Visibility.Collapsed : Visibility.Visible;
        UpdateTitleAlignment();
    }

    private void OnSessionEnded(object? sender, PlaybackSessionEventArgs e)
    {
        // 替换时旧会话先结束，然后才会通知新会话启动。
        var observed = ReferenceEquals(observedSession, e.Session);
        if (!observed && !ReferenceEquals(player?.Session, e.Session) && !ReferenceEquals(startingSession, e.Session)) return;
        if (observed) StopObservingSession();
        if (ReferenceEquals(player?.Session, e.Session) || ReferenceEquals(startingSession, e.Session)) HidePlayer();
        if (e.EndReason == PlaybackEndReason.SeasonEnded)
            toasts.Show(ToastKind.Info, "本季已播放完", duration: TimeSpan.FromSeconds(6));
        else if (e.EndReason == PlaybackEndReason.Failed && e.Session.Snapshot.EngineKind == EngineKind.External)
        {
            var snapshot = e.Session.Snapshot;
            var retry = snapshot.Entry;
            toasts.Show(ToastKind.Error, snapshot.Error?.Message ?? "外置 MPV 播放中断，已保存最新进度",
                retry is null ? null : "重试", retry is null ? null :
                    () => _ = new PlaybackLauncher(playback, dialogs, toasts).PlayAsync(retry.ItemId, snapshot.PositionTicks));
        }
        else if (e.EndReason == PlaybackEndReason.Failed)
            toasts.Show(ToastKind.Warning, "播放意外中断，已保存最新进度", duration: TimeSpan.FromSeconds(8));
    }

    private void OnEntrySkipped(object? sender, PlaybackEntrySkippedEventArgs e) =>
        toasts.Show(ToastKind.Warning, "有一集无法加入连播，已跳过", duration: TimeSpan.FromSeconds(6));

    private async void HidePlayer() => await (PendingPresentation = HidePlayerAsync());

    private async Task HidePlayerAsync()
    {
        if (!browseCovered) return;
        var hadPlayer = player is not null || retiringPlayer is not null;
        var version = ++playerPresentationVersion;
        fold.RequestTarget(false);
        presentationPending = true;
        // 在首个 await 前移走原生表面；退场只保留已经绘制的纯 XAML。
        RetireActivePlayer();
        startingSession = null;
        var closing = fold.PlayAsync(false, hadPlayer);
        window.ExitFullscreen();
        UpdatePresentationLayout();
        await closing;
        if (disposed || version != playerPresentationVersion) return;
        ReleaseRetiringPlayer();
        browseCovered = false;
        Motion.SetEntranceSuppressed(BrowseFace, false);
        Motion.SetActive(BrowseFace, true);
        browseInput.SetEnabled(true);
        Pages.SettleTransition();
        (Pages.CurrentPage as HomePage)?.SetCovered(false);
        UpdatePageTitle();
        UpdateTitleAlignment();
        if (ReferenceEquals(navigator.BackInterceptor, this)) navigator.BackInterceptor = null;
        navigator.ForwardBlocked = false;
        var restoreTarget = previousFocus;
        previousFocus = null;
        var restored = CanRestoreFocus(restoreTarget) && restoreTarget!.Focus(FocusState.Programmatic) && HasShellFocus();
        if (!restored) restored = Sidebar.FocusNavigation() && HasShellFocus();
        LastPlayerFocusRestoredWithinShell = HasShellFocus();
        LastPlayerFocusRestoreSucceeded = restored && LastPlayerFocusRestoredWithinShell;
        presentationPending = false;
    }

    private void RetireActivePlayer()
    {
        if (player is not { } old) return;
        ParkPlayerFocus();
        ReleaseRetiringPlayer();
        retiringPanelWidth = PlayerPanelWidth;
        old.TitleChanged -= OnPlayerTitleChanged;
        old.LayoutChanged -= OnPlayerLayoutChanged;
        old.FreezeForClose();
        PreservePreferences(old.PendingPreferenceSave);
        player = null;
        retiringPlayer = old;
    }

    private void ReleaseRetiringPlayer()
    {
        if (retiringPlayer is not { } old) return;
        retiringPlayer = null;
        retiringPanelWidth = 0;
        old.Dispose();
        PlayerContent.Children.Remove(old);
    }

    private void PreservePreferences(Task pending)
    {
        if (pending.IsCompletedSuccessfully) return;
        pendingPreferenceSaves = pendingPreferenceSaves.IsCompletedSuccessfully
            ? pending : Task.WhenAll(pendingPreferenceSaves, pending);
    }

    private void SettlePlayerPresentation()
    {
        fold.Settle();
        ReleaseRetiringPlayer();
    }

    private void ParkPlayerFocus()
    {
        if (player is null || XamlRoot is null || HasShellFocus()) return;
        // 原生视频树销毁前，焦点必须先落到仍可用的外壳控件。
        // 浏览面在翻折结束前仍禁用；标题栏按钮是这个阶段的稳定目标。
        TitleBar.Visibility = Visibility.Visible;
        Root.RowDefinitions[0].Height = (GridLength)Application.Current.Resources["TitleBarHeightGridLength"];
        TitleBar.UpdateLayout();
        MinimizeButton.Focus(FocusState.Programmatic);
    }

    private double PlayerPanelWidth => player is { EpisodePanelVisible: true } active
        ? Math.Max(0, active.ActualWidth - active.ViewportElement.ActualWidth)
        : retiringPlayer is not null ? retiringPanelWidth : 0;

    private bool CanRestoreFocus(Control? candidate) =>
        candidate is { IsLoaded: true, IsEnabled: true, IsTabStop: true }
        && ReferenceEquals(candidate.XamlRoot, XamlRoot) && IsWithinShell(candidate);

    private bool HasShellFocus() => XamlRoot is { } root
        && FocusManager.GetFocusedElement(root) is Control { IsLoaded: true, IsEnabled: true } focused
        && ReferenceEquals(focused.XamlRoot, root) && IsWithinShell(focused);

    private bool IsWithinShell(DependencyObject? candidate)
    {
        for (var current = candidate; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, PlayerSlot)) return false;
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            if (current is Control { IsEnabled: false }) return false;
            if (ReferenceEquals(current, this)) return true;
        }
        return false;
    }

    private void OnPresentationChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        SettlePlayerPresentation();
        UpdatePresentationLayout();
    }

    private void OnWindowActiveChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        Motion.SetActive(this, window.IsActive);
        // 失活时 PageHost 会隔离当前页输入；恢复动效标记本身不会恢复命中测试和 Tab。
        // 必须在两种激活状态下同步页面，播放器覆盖时仍由 BrowseFace 的标记保持隔离。
        Pages.SettleTransition();
        if (window.IsActive) return;
        transitions.SettleBackdrop();
        SettlePlayerPresentation();
    }

    private void UpdatePresentationLayout()
    {
        var fullscreen = player is not null && window.IsFullscreen;
        TitleBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[0].Height = fullscreen ? new GridLength(0) : (GridLength)Application.Current.Resources["TitleBarHeightGridLength"];
        Grid.SetRow(PlayerSlot, fullscreen ? 0 : 1);
        Grid.SetRowSpan(PlayerSlot, fullscreen ? 2 : 1);
        UpdateTitleAlignment();
    }

    private void OnPlayerLayoutChanged(object? sender, EventArgs e) => UpdateTitleAlignment();

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (disposed) return;
        SettlePlayerPresentation();
        UpdateTitleAlignment();
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleAlignment();

    private void UpdateTitleAlignment()
    {
        if (disposed) return;
        Grid.SetColumn(CenterArea, playerFacing ? 0 : 1);
        Grid.SetColumnSpan(CenterArea, playerFacing ? 3 : 2);
        // 玩家视口是标题对齐的唯一尺寸来源，不把选集栏算入画面中心。
        CenterArea.Margin = new Thickness(0, 0, playerFacing ? PlayerPanelWidth : 0, 0);
        TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>标题栏中间的可交互内容，例如首页 hero 分页点。</summary>
    public void SetCenterContent(UIElement? content)
    {
        CenterContent.Content = content;
        CenterContent.Visibility = playerFacing ? Visibility.Collapsed : Visibility.Visible;
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
        if (!playerFacing) ViewModel.TitleText = pageTitle;
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        SettlePlayerPresentation();
        ImageLoader.Current?.ClearDecodedCache();
        transitions.Clear();
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
        if (e.Handled || dialogs.IsOpen) return;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed) { navigator.GoBack(); e.Handled = true; }
        else if (properties.IsXButton2Pressed) { navigator.GoForward(); e.Handled = true; }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || dialogs.IsOpen || player is not null) return;
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
        var visual = ElementCompositionPreview.GetElementVisual(WellContent);
        var compositor = visual.Compositor;
        if (wellClip is null)
        {
            wellClip = compositor.CreateRoundedRectangleGeometry();
            wellClip.CornerRadius = new Vector2(11, 11);
            visual.Clip = compositor.CreateGeometricClip(wellClip);
        }
        wellClip.Size = new Vector2((float)WellContent.ActualWidth, (float)WellContent.ActualHeight + 12);
    }
}
