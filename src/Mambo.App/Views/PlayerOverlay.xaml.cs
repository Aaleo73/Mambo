using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.Video;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT;
using VirtualKey = Windows.System.VirtualKey;

namespace Mambo.App.Views;

/// <summary>一个会话的 XAML 播放层；与下方浏览页同时存活，全部引擎控制经过契约。</summary>
public sealed partial class PlayerOverlay : UserControl, IDisposable
{
    private static readonly double[] Rates = [.5, .75, 1, 1.25, 1.5, 2];
    private readonly IPlaybackSession session;
    private readonly WindowContext window;
    private readonly ToastService toasts;
    private readonly ISettingsService settings;
    private readonly DispatcherQueueTimer clock;
    private readonly DispatcherQueueTimer singleClick;
    private readonly InputSystemCursor arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<MenuFlyout> openFlyouts = [];
    private readonly Dictionary<MenuFlyout, List<(MenuFlyoutItem Item, RoutedEventHandler Handler)>> menuHandlers = [];
    private readonly List<WeakReference<Button>> episodeButtons = [];
    private readonly List<(UIElement Element, RoutedEvent Event, object Handler)> routedHandlers = [];
    private ScalarKeyFrameAnimation? chromeAnimation;
    private CubicBezierEasingFunction? chromeEasing;
    private ScalarKeyFrameAnimation? hintAnimation;
    private ScalarKeyFrameAnimation? pulseAnimation;
    private ScalarKeyFrameAnimation? pingFadeAnimation;
    private Vector3KeyFrameAnimation? pingAnimation;
    private bool stateAnimationsRunning;
    private long hintUntil;
    private bool hintFading;
    private bool transitionActive;
    private bool episodePanelCollapsed;
    private bool seekPointerInside;
    private int seekEditVersion;
    private double seekHoverFraction;
    private long lastActivity;
    private long ignoreTapUntil;
    private bool pointerInside;
    private bool pointerPressed;
    private bool chromeVisible;
    private bool settingControls;
    private bool draggingSeek;
    private bool disposed;
    private bool closing;
    private bool attached;
    private int openMenus;
    private string? previousErrorCode;
    private bool dragVolume;
    private Task lastCommand = Task.CompletedTask;
    private Task layoutSave = Task.CompletedTask;
    private bool volumePointerInside;
    private double seekTipSeconds;

    public PlayerOverlay(IPlaybackSession session, WindowContext window, ToastService toasts, ISettingsService settings)
    {
        this.session = session;
        this.window = window;
        this.toasts = toasts;
        this.settings = settings;
        ViewModel = new(session);
        InitializeComponent();
        episodePanelCollapsed = settings.Current.EpisodePanelCollapsed;
        EpisodeListScroll.Visibility = settings.Current.UseEpisodeGrid ? Visibility.Collapsed : Visibility.Visible;
        EpisodeGridScroll.Visibility = settings.Current.UseEpisodeGrid ? Visibility.Visible : Visibility.Collapsed;
        clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromMilliseconds(100);
        clock.Tick += OnClock;
        singleClick = DispatcherQueue.CreateTimer();
        singleClick.Interval = TimeSpan.FromMilliseconds(250);
        singleClick.IsRepeating = false;
        singleClick.Tick += OnSingleClick;
        RegisterRoutedHandler(this, PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
        RegisterRoutedHandler(this, PointerPressedEvent, new PointerEventHandler(OnAnyPointerPressed));
        RegisterRoutedHandler(SeekSlider, PointerPressedEvent, new PointerEventHandler(OnSeekPressed));
        RegisterRoutedHandler(SeekSlider, PointerReleasedEvent, new PointerEventHandler(OnSeekReleased));
        RegisterRoutedHandler(SeekSlider, PointerCaptureLostEvent, new PointerEventHandler(OnSeekCaptureLost));
        RegisterRoutedHandler(SeekHost, PointerMovedEvent, new PointerEventHandler(OnSeekPointerMoved));
        RegisterRoutedHandler(SeekHost, PointerEnteredEvent, new PointerEventHandler(OnSeekPointerMoved));
        RegisterRoutedHandler(SeekHost, PointerExitedEvent, new PointerEventHandler(OnSeekPointerExited));
        RegisterRoutedHandler(VolumeSlider, PointerPressedEvent, new PointerEventHandler((_, _) => dragVolume = true));
        RegisterRoutedHandler(VolumeSlider, PointerReleasedEvent, new PointerEventHandler((_, _) => dragVolume = false));
        RegisterRoutedHandler(VolumeSlider, PointerCaptureLostEvent, new PointerEventHandler((_, _) => dragVolume = false));
        RegisterRoutedHandler(Chrome, PointerMovedEvent, new PointerEventHandler((_, _) => { pointerInside = true; Activity(); }));
        RegisterRoutedHandler(Chrome, PointerPressedEvent, new PointerEventHandler((_, _) => { pointerPressed = true; Activity(); }));
        RegisterRoutedHandler(EpisodePanel, PointerEnteredEvent, new PointerEventHandler(OnEpisodePanelPointerEntered));
        RegisterRoutedHandler(EpisodePanel, PointerMovedEvent, new PointerEventHandler(OnEpisodePanelPointerEntered));
        RegisterRoutedHandler(this, PointerReleasedEvent, new PointerEventHandler((_, _) => pointerPressed = false));
        RegisterRoutedHandler(this, PointerCanceledEvent, new PointerEventHandler((_, _) => pointerPressed = false));
        RegisterRoutedHandler(this, PointerCaptureLostEvent, new PointerEventHandler((_, _) => pointerPressed = false));
        session.SnapshotChanged += OnSnapshotChanged;
        ViewModel.PropertyChanged += OnProjectionChanged;
        window.PresentationChanged += OnPresentationChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        EpisodeList.ElementPrepared += OnEpisodeElementPrepared;
        EpisodeGrid.ElementPrepared += OnEpisodeElementPrepared;
        OnSnapshotChanged(null, EventArgs.Empty);
    }

    public PlayerViewModel ViewModel { get; }
    public VideoSurface VideoSurface => Surface;
    internal IPlaybackSession Session => session;
    internal bool ControlsVisible => chromeVisible;
    internal bool EpisodePanelVisible => EpisodePanel.Visibility == Visibility.Visible;
    internal FrameworkElement ViewportElement => VideoViewport;
    internal bool KeyHintVisible => KeyHint.Visibility == Visibility.Visible;
    internal string KeyHintText => KeyHintLabel.Text;
    internal double SeekTipSeconds => seekTipSeconds;
    internal bool HasOpenMenu => openMenus > 0;
    internal bool EpisodeGridVisible => EpisodeGridScroll.Visibility == Visibility.Visible;
    internal bool ErrorPanelVisible => ErrorPanel.Visibility == Visibility.Visible;
    internal bool OpeningPanelVisible => OpeningPanel.Visibility == Visibility.Visible;
    internal bool SlowOpeningVisible => OpeningPanelVisible && SlowOpeningHint.Visibility == Visibility.Visible;
    internal bool BufferingVisible => BufferingPill.Visibility == Visibility.Visible;
    internal bool ExternalPanelVisible => ExternalPanel.Visibility == Visibility.Visible;
    internal bool SeekTipVisible => SeekTip.Visibility == Visibility.Visible;
    internal double DisplayedSeekSeconds => SeekSlider.Value;
    internal Task PendingPreferenceSave => layoutSave;
    public event EventHandler? TitleChanged;
    public event EventHandler? LayoutChanged;

    internal void ShowControlsForSmoke()
    {
        pointerInside = true;
        Activity();
    }

    /// <summary>回归入口复用真实事件的分派路径；调用后还需等待快照通知到达。</summary>
    internal async Task<bool> DispatchSmokeKeyAsync(VirtualKey key, bool alt = false, bool control = false)
    {
        var handled = HandleKey(key, alt, control);
        if (handled) await lastCommand;
        return handled;
    }

    internal void DispatchSmokeTap(bool doubleTap = false)
    {
        if (doubleTap) HandleSurfaceDoubleTap();
        else HandleSurfaceTap();
    }

    internal MenuFlyout ShowMenuForSmoke(bool tracks)
    {
        var menu = tracks ? CreateTracksMenu() : CreateRateMenu();
        menu.ShowAt(tracks ? TracksButton : RateButton);
        return menu;
    }
    internal void ToggleEpisodesForSmoke() => ToggleEpisodePanel();
    internal void DispatchSmokeSeekHover(double fraction) => PreviewSeekHover(fraction);
    internal void DispatchSmokeSeekExit() => ExitSeekHover();
    internal void SetEpisodeGridForSmoke(bool grid) => SetEpisodeLayout(grid);
    internal async Task SetEpisodeGridForSmokeAsync(bool grid)
    {
        SetEpisodeLayout(grid);
        await layoutSave;
    }

    internal async Task DispatchSmokeEpisodeAsync(string itemId)
    {
        SelectEpisode(itemId);
        await lastCommand;
    }

    internal async Task DispatchSmokeRateAsync(double rate)
    {
        if (!Rates.Contains(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        SelectRate(rate);
        await lastCommand;
    }

    internal async Task DispatchSmokeTrackAsync(string? trackId, bool subtitle)
    {
        var tracks = subtitle ? ViewModel.Snapshot.SubtitleTracks : ViewModel.Snapshot.AudioTracks;
        if (!(subtitle && trackId is null) && !tracks.Any(track => track.Id == trackId)) throw new ArgumentException("菜单中没有该轨道。", nameof(trackId));
        SelectTrack(trackId, subtitle);
        await lastCommand;
    }

    internal async Task DispatchSmokeSeekAsync(double seconds)
    {
        BeginSmokeSeek(seconds);
        await CommitSmokeSeekAsync();
    }

    internal void BeginSmokeSeek(double seconds)
    {
        BeginSeek();
        SeekSlider.Value = seconds;
    }

    internal async Task CommitSmokeSeekAsync()
    {
        CommitSeek();
        await lastCommand;
    }

    internal async Task DispatchSmokeRetryAsync()
    {
        Retry();
        await lastCommand;
    }

    internal async Task DispatchSmokeMouseButtonAsync(bool back)
    {
        HandleMouseButton(back);
        await lastCommand;
    }

    internal async Task DispatchSmokeNavigationAsync(bool next)
    {
        NavigateEpisode(next);
        await lastCommand;
    }

    internal async Task DispatchSmokeVolumeAsync(double volume)
    {
        VolumeSlider.Value = Math.Clamp(volume, 0, 100);
        await lastCommand;
    }

    public void SetLiveResize(bool active) => Surface.SetLiveResize(active);

    public void SetTransitionActive(bool active)
    {
        if (disposed || transitionActive == active) return;
        transitionActive = active;
        if (active)
        {
            Surface.Detach();
            attached = false;
            Surface.Visibility = Visibility.Collapsed;
            HideMenus();
            SetChrome(false);
            StopChromeAnimation(ElementCompositionPreview.GetElementVisual(Chrome));
            Chrome.Opacity = 0;
            StopHintAnimation();
            KeyHint.Visibility = Visibility.Collapsed;
        }
        else OnSnapshotChanged(null, EventArgs.Empty);
        UpdateTransitionVisibility();
    }

    private void UpdateTransitionVisibility()
    {
        StateLayer.Visibility = transitionActive ? Visibility.Collapsed : Visibility.Visible;
        ExternalPanel.Visibility = !transitionActive && ViewModel.ShowExternalPanel ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    public async Task CloseAsync()
    {
        if (closing || disposed) return;
        closing = true;
        singleClick.Stop();
        HideMenus();
        window.ExitFullscreen();
        window.SetPlaybackActive(false);
        Surface.Detach();
        attached = false;
        Surface.Visibility = Visibility.Collapsed;
        StopHintAnimation();
        KeyHint.Visibility = Visibility.Collapsed;
        try
        {
            await FlushPreferencesAsync();
            await session.CloseAsync();
        }
        catch (AppException ex) { toasts.Show(ToastKind.Error, ex.Error.Message); closing = false; OnSnapshotChanged(null, EventArgs.Empty); }
        catch (OperationCanceledException) { closing = false; OnSnapshotChanged(null, EventArgs.Empty); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var registration in routedHandlers)
            registration.Element.RemoveHandler(registration.Event, registration.Handler);
        routedHandlers.Clear();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        clock.Stop();
        singleClick.Stop();
        clock.Tick -= OnClock;
        singleClick.Tick -= OnSingleClick;
        StopChromeAnimation(ElementCompositionPreview.GetElementVisual(Chrome));
        StopHintAnimation();
        StopStateAnimations();
        DisposeMenus();
        DetachXamlEvents();
        Bindings.StopTracking();
        EpisodeList.ItemsSource = null;
        EpisodeGrid.ItemsSource = null;
        lifetime.Cancel();
        lifetime.Dispose();
        session.SnapshotChanged -= OnSnapshotChanged;
        window.PresentationChanged -= OnPresentationChanged;
        ViewModel.PropertyChanged -= OnProjectionChanged;
        ViewModel.Dispose();
        Surface.Dispose();
        ProtectedCursor = null;
        window.SetPlaybackActive(false);
        arrow.Dispose();
        TitleChanged = null;
        LayoutChanged = null;
        Content = null;
    }

    private void OnEpisodeElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (disposed) return;
        // Both episode templates have a Button as their root.
        var button = args.Element.As<Button>();
        if (!episodeButtons.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, button)))
            episodeButtons.Add(new(button));
    }

    private void DetachXamlEvents()
    {
        // XAML Connect attaches instance delegates to controls. A cached native peer
        // must not keep a permanently closed player alive through these delegates.
        InputSurface.Tapped -= OnSurfaceTapped;
        InputSurface.DoubleTapped -= OnSurfaceDoubleTapped;
        InputSurface.PointerMoved -= OnSurfacePointerMoved;
        InputSurface.PointerEntered -= OnSurfacePointerEntered;
        InputSurface.PointerExited -= OnSurfacePointerExited;
        InputSurface.PointerPressed -= OnSurfacePointerPressed;
        InputSurface.PointerReleased -= OnSurfacePointerReleased;
        InputSurface.PointerCanceled -= OnSurfacePointerReleased;
        SeekHost.SizeChanged -= OnSeekHostSizeChanged;
        VideoViewport.SizeChanged -= OnViewportSizeChanged;
        SeekSlider.ValueChanged -= OnSeekValueChanged;
        VolumeSlider.ValueChanged -= OnVolumeChanged;
        VolumeHost.PointerEntered -= OnVolumeEntered;
        VolumeHost.PointerExited -= OnVolumeExited;
        VolumeHost.GotFocus -= OnVolumeGotFocus;
        VolumeHost.LostFocus -= OnVolumeLostFocus;
        EpisodeList.ElementPrepared -= OnEpisodeElementPrepared;
        EpisodeGrid.ElementPrepared -= OnEpisodeElementPrepared;
        ExternalStopButton.Click -= OnCloseClick;
        CloseButton.Click -= OnCloseClick;
        PreviousButton.Click -= OnPreviousClick;
        PauseButton.Click -= OnPauseClick;
        NextButton.Click -= OnNextClick;
        RateButton.Click -= OnRateClick;
        TracksButton.Click -= OnTracksClick;
        MuteButton.Click -= OnMuteClick;
        EpisodesButton.Click -= OnEpisodesClick;
        FullscreenButton.Click -= OnFullscreenClick;
        MaximizeButton.Click -= OnMaximizeClick;
        SlowOpeningCloseButton.Click -= OnCloseClick;
        BigPlay.Click -= OnPauseClick;
        RetryButton.Click -= OnRetryClick;
        ErrorCloseButton.Click -= OnCloseClick;
        UpNextPlayButton.Click -= OnNextClick;
        UpNextCancelButton.Click -= OnDismissUpNextClick;
        EpisodeListButton.Click -= OnEpisodeListClick;
        EpisodeGridButton.Click -= OnEpisodeGridClick;
        // A previously recycled episode button can also have an external native peer.
        foreach (var reference in episodeButtons)
            if (reference.TryGetTarget(out var button)) button.Click -= OnEpisodeClick;
        episodeButtons.Clear();
    }

    private void RegisterRoutedHandler(UIElement element, RoutedEvent routedEvent, object handler)
    {
        element.AddHandler(routedEvent, handler, true);
        routedHandlers.Add((element, routedEvent, handler));
    }

    internal async Task FlushPreferencesAsync()
    {
        // 关闭期间禁止再发起布局修改；外部调用也能等到其观察时刻的最新一笔写入。
        Task pending;
        do
        {
            pending = layoutSave;
            await pending;
        } while (!ReferenceEquals(pending, layoutSave));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed) return;
        Focus(FocusState.Programmatic);
        OnSnapshotChanged(null, EventArgs.Empty);
        UpdateControls();
        clock.Start();
        OnPresentationChanged(null, EventArgs.Empty);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (disposed) return;
        clock.Stop();
        singleClick.Stop();
        Surface.Detach();
        attached = false;
        Surface.Visibility = Visibility.Collapsed;
        DisposeMenus();
        StopHintAnimation();
        KeyHint.Visibility = Visibility.Collapsed;
        StopStateAnimations();
        StopChromeAnimation(ElementCompositionPreview.GetElementVisual(Chrome));
        ProtectedCursor = arrow;
    }

    private void AttachSurface()
    {
        if (disposed || closing || transitionActive || !IsLoaded || attached || ViewModel.IsExternal) return;
        try { Surface.Attach(session); attached = true; }
        catch (AppException ex) { toasts.Show(ToastKind.Error, ex.Error.Message); }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        if ((ViewModel.IsExternal || transitionActive || closing) && attached) { Surface.Detach(); attached = false; }
        else AttachSurface();
        Surface.Visibility = !transitionActive && !closing && !ViewModel.IsExternal ? Visibility.Visible : Visibility.Collapsed;
        window.SetPlaybackActive(!closing && ViewModel.CanControl && !ViewModel.IsPaused);
        // 外部窗口面板一直保留控制；内置画面按空闲时间收起控制。
        if (ViewModel.IsExternal || ViewModel.IsFailed) SetChrome(true);
        UpdateControls();
        UpdateStatus();
        DrawBuffers();
        UpdateEpisodePanel();
        UpdateTransitionVisibility();
        TitleChanged?.Invoke(this, EventArgs.Empty);
        if (ViewModel.IsFailed && ViewModel.Snapshot.Error is { } error && error.Code != previousErrorCode)
        {
            previousErrorCode = error.Code;
            var weakOwner = new WeakReference<PlayerOverlay>(this);
            toasts.Show(ToastKind.Error, "播放失败：" + error.Message, "重试", () =>
            {
                if (weakOwner.TryGetTarget(out var owner) && !owner.disposed) owner.Retry();
            });
        }
        else if (!ViewModel.IsFailed) previousErrorCode = null;
    }

    private void OnProjectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.DisplayPositionTicks) or nameof(PlayerViewModel.ShowUpNext))
        {
            UpdateControls();
            UpdateStatus();
        }
    }

    private void OnClock(DispatcherQueueTimer sender, object args)
    {
        if (disposed) return;
        ViewModel.Tick();
        UpdateKeyHint();
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var focusInControls = IsWithin(focused, Chrome) || IsWithin(focused, BigPlay) || IsWithin(focused, UpNext);
        var visible = ViewModel.IsExternal || ViewModel.IsFailed || pointerPressed || openMenus > 0 || focusInControls
            || pointerInside && Environment.TickCount64 - lastActivity < 3000;
        SetChrome(visible);
    }

    private void OnSingleClick(DispatcherQueueTimer sender, object args)
    {
        if (ViewModel.CanControl) Run(() => session.TogglePauseAsync(lifetime.Token));
    }

    private void Activity()
    {
        lastActivity = Environment.TickCount64;
        SetChrome(true);
    }

    private void SetChrome(bool visible)
    {
        if (disposed) return;
        visible &= !transitionActive;
        if (chromeVisible == visible) { UpdateCursor(); return; }
        chromeVisible = visible;
        Chrome.IsHitTestVisible = visible;
        var visual = ElementCompositionPreview.GetElementVisual(Chrome);
        StopChromeAnimation(visual);
        Chrome.Opacity = visible ? 1 : 0;
        if (!transitionActive && Motion.AnimationsEnabled)
        {
            chromeAnimation = visual.Compositor.CreateScalarKeyFrameAnimation();
            chromeEasing = Motion.CreateEasing(visual.Compositor, Motion.Enter);
            chromeAnimation.InsertKeyFrame(0, visible ? 0 : 1);
            chromeAnimation.InsertKeyFrame(1, visible ? 1 : 0, chromeEasing);
            chromeAnimation.Duration = TimeSpan.FromMilliseconds(240);
            visual.StartAnimation("Opacity", chromeAnimation);
        }
        Chrome.TabFocusNavigation = visible ? KeyboardNavigationMode.Local : KeyboardNavigationMode.Once;
        UpdateStatus();
        UpdateCursor();
    }

    private void StopChromeAnimation(Visual visual)
    {
        // Visual 属于 XAML；这里只释放本播放层创建的动画与 easing。
        visual.StopAnimation("Opacity");
        chromeAnimation?.Dispose();
        chromeAnimation = null;
        chromeEasing?.Dispose();
        chromeEasing = null;
    }

    private void UpdateCursor()
    {
        var hide = !transitionActive && ViewModel.CanControl && !ViewModel.IsPaused && !chromeVisible && pointerInside;
        ProtectedCursor = hide ? null : arrow;
        Surface.HideCursor(hide);
    }

    private void UpdateStatus()
    {
        BigPlay.Visibility = ViewModel.IsPaused && !ViewModel.IsExternal && !transitionActive ? Visibility.Visible : Visibility.Collapsed;
        UpNext.Visibility = ViewModel.ShowUpNext && !transitionActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateStateAnimations();
    }

    private void UpdateControls()
    {
        settingControls = true;
        try
        {
            if (!draggingSeek) SeekSlider.Value = Math.Clamp(ViewModel.PositionSeconds, 0, SeekSlider.Maximum);
            if (!dragVolume) VolumeSlider.Value = ViewModel.Volume;
        }
        finally { settingControls = false; }
    }

    private void OnPresentationChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        TopBar.Visibility = window.IsFullscreen ? Visibility.Visible : Visibility.Collapsed;
        FullscreenGlyph.Glyph = (string)Application.Current.Resources[window.IsFullscreen ? "IconFullscreenExit" : "IconFullscreen"];
        MaximizeGlyph.Glyph = (string)Application.Current.Resources[window.IsMaximized ? "CaptionRestore" : "CaptionMaximize"];
        VideoViewport.Margin = window.IsFullscreen ? new Thickness(0) : new Thickness(0, 12, 0, 0);
        VideoViewport.CornerRadius = window.IsFullscreen ? new CornerRadius(0) : new CornerRadius(12, 12, 0, 0);
        VideoHost.CornerRadius = VideoViewport.CornerRadius;
        Surface.SetViewportClip(window.IsFullscreen ? 0 : 12, topOnly: true);
        UpdateEpisodePanel();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEpisodePanelPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        pointerInside = false;
        UpdateCursor();
    }

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e) { pointerInside = true; Activity(); }
    private void OnSurfacePointerEntered(object sender, PointerRoutedEventArgs e) => pointerInside = true;
    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs e) => pointerInside = false;
    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(InputSurface).Properties.IsLeftButtonPressed) return;
        pointerPressed = true;
        Activity();
        Focus(FocusState.Programmatic);
    }
    private void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs e) => pointerPressed = false;
    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = HandleSurfaceTap();
    }
    private bool HandleSurfaceTap()
    {
        if (disposed || !ViewModel.CanControl || Environment.TickCount64 < ignoreTapUntil) return false;
        Activity();
        singleClick.Stop();
        singleClick.Start();
        return true;
    }
    private void OnSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        HandleSurfaceDoubleTap();
        e.Handled = true;
    }
    private void HandleSurfaceDoubleTap()
    {
        if (disposed) return;
        singleClick.Stop();
        ignoreTapUntil = Environment.TickCount64 + 300;
        if (ViewModel.CanControl) window.ToggleFullscreen();
    }

    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed) { HandleMouseButton(back: true); e.Handled = true; }
        else if (properties.IsXButton2Pressed) { HandleMouseButton(back: false); e.Handled = true; }
    }
    private void HandleMouseButton(bool back) => lastCommand = back ? CloseAsync() : Task.CompletedTask;

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Tab reveals the controls before normal focus navigation; the episode panel is not a focus trap.
        if (e.Key == VirtualKey.Tab) Activity();
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (HandleKey(e.Key, alt, control)) e.Handled = true;
    }

    private bool HandleKey(VirtualKey key, bool alt, bool control)
    {
        if (disposed || closing) return false;
        lastCommand = Task.CompletedTask;
        if (key == VirtualKey.F11) { HideMenus(); window.ToggleFullscreen(); return true; }
        if (key == VirtualKey.Escape)
        {
            if (openMenus > 0) HideMenus();
            else if (window.IsFullscreen) window.ExitFullscreen();
            else lastCommand = CloseAsync();
            return true;
        }
        if (alt && key == VirtualKey.Left) { lastCommand = CloseAsync(); return true; }
        if (alt && key == VirtualKey.Right) return true;
        if (alt || control) return false;
        if (!ViewModel.CanControl) return false;
        switch (key)
        {
            case VirtualKey.Space: Activity(); Run(() => session.TogglePauseAsync(lifetime.Token)); break;
            case VirtualKey.M:
                var muted = !ViewModel.Snapshot.IsMuted;
                ShowKeyHint(muted ? "已静音" : "已取消静音");
                Run(() => session.SetMutedAsync(muted, lifetime.Token));
                break;
            case VirtualKey.Left: ShowKeyHint("−5 秒"); SeekRelative(-5); break;
            case VirtualKey.Right: ShowKeyHint("+5 秒"); SeekRelative(5); break;
            case VirtualKey.Up: ChangeVolume(5); break;
            case VirtualKey.Down: ChangeVolume(-5); break;
            case VirtualKey.C: CycleSubtitles(); break;
            case VirtualKey.V: CycleAudio(); break;
            case (VirtualKey)188: Run(() => session.StepFrameAsync(FrameStepDirection.Backward, lifetime.Token)); break;
            case (VirtualKey)190: Run(() => session.StepFrameAsync(FrameStepDirection.Forward, lifetime.Token)); break;
            case (VirtualKey)219: ChangeRate(-1); break;
            case (VirtualKey)221: ChangeRate(1); break;
            default: return false;
        }
        return true;
    }

    private void ChangeVolume(double delta)
    {
        var volume = Math.Clamp(ViewModel.Volume + delta, 0, 100);
        ShowKeyHint("音量 " + volume.ToString("0", CultureInfo.InvariantCulture) + "%");
        Run(() => session.SetVolumeAsync(volume, lifetime.Token));
    }

    private void ShowKeyHint(string text)
    {
        if (disposed || transitionActive) return;
        StopHintAnimation();
        KeyHintLabel.Text = text;
        KeyHint.Opacity = 1;
        KeyHint.Visibility = Visibility.Visible;
        hintUntil = Environment.TickCount64 + 1200;
        hintFading = false;
    }

    private void UpdateKeyHint()
    {
        if (!KeyHintVisible) return;
        var remaining = hintUntil - Environment.TickCount64;
        if (remaining <= 0)
        {
            StopHintAnimation();
            KeyHint.Visibility = Visibility.Collapsed;
        }
        else if (remaining <= 200 && !hintFading && Motion.AnimationsEnabled)
        {
            hintFading = true;
            var visual = ElementCompositionPreview.GetElementVisual(KeyHint);
            hintAnimation = visual.Compositor.CreateScalarKeyFrameAnimation();
            hintAnimation.InsertKeyFrame(0, 1);
            hintAnimation.InsertKeyFrame(1, 0);
            hintAnimation.Duration = TimeSpan.FromMilliseconds(remaining);
            visual.StartAnimation("Opacity", hintAnimation);
        }
    }

    private void StopHintAnimation()
    {
        ElementCompositionPreview.GetElementVisual(KeyHint).StopAnimation("Opacity");
        hintAnimation?.Dispose();
        hintAnimation = null;
    }

    private void UpdateStateAnimations()
    {
        var active = IsLoaded && !transitionActive && !disposed && Motion.AnimationsEnabled && (ViewModel.IsOpening || ViewModel.IsBuffering);
        if (active == stateAnimationsRunning) return;
        if (!active) { StopStateAnimations(); return; }
        stateAnimationsRunning = true;
        var dot = ElementCompositionPreview.GetElementVisual(BufferingDot);
        var ping = ElementCompositionPreview.GetElementVisual(OpeningPing);
        pulseAnimation = dot.Compositor.CreateScalarKeyFrameAnimation();
        pulseAnimation.InsertKeyFrame(0, .4f);
        pulseAnimation.InsertKeyFrame(.5f, 1);
        pulseAnimation.InsertKeyFrame(1, .4f);
        pulseAnimation.Duration = TimeSpan.FromMilliseconds(1200);
        pulseAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
        dot.StartAnimation("Opacity", pulseAnimation);
        pingFadeAnimation = ping.Compositor.CreateScalarKeyFrameAnimation();
        pingFadeAnimation.InsertKeyFrame(0, 1);
        pingFadeAnimation.InsertKeyFrame(1, 0);
        pingFadeAnimation.Duration = TimeSpan.FromMilliseconds(1200);
        pingFadeAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
        ping.StartAnimation("Opacity", pingFadeAnimation);
        ping.CenterPoint = new Vector3(20, 20, 0);
        pingAnimation = ping.Compositor.CreateVector3KeyFrameAnimation();
        pingAnimation.InsertKeyFrame(0, Vector3.One);
        pingAnimation.InsertKeyFrame(1, new Vector3(1.65f, 1.65f, 1));
        pingAnimation.Duration = TimeSpan.FromMilliseconds(1200);
        pingAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
        ping.StartAnimation("Scale", pingAnimation);
    }

    private void StopStateAnimations()
    {
        stateAnimationsRunning = false;
        ElementCompositionPreview.GetElementVisual(BufferingDot).StopAnimation("Opacity");
        var ping = ElementCompositionPreview.GetElementVisual(OpeningPing);
        ping.StopAnimation("Opacity");
        ping.StopAnimation("Scale");
        pulseAnimation?.Dispose();
        pulseAnimation = null;
        pingFadeAnimation?.Dispose();
        pingFadeAnimation = null;
        pingAnimation?.Dispose();
        pingAnimation = null;
    }

    private void SeekRelative(double seconds)
    {
        var position = ViewModel.ClampPosition(ViewModel.DisplayPositionTicks + (long)(seconds * TimeSpan.TicksPerSecond));
        ViewModel.PreviewSeek(TimeSpan.FromTicks(position).TotalSeconds);
        ViewModel.CommitSeekPreview();
        Run(() => session.SeekAsync(TimeSpan.FromTicks(position), lifetime.Token), cancelSeekOnError: true);
    }

    private void ChangeRate(int direction)
    {
        var rate = ViewModel.Snapshot.PlaybackRate;
        var selected = direction > 0 ? Rates.FirstOrDefault(r => r > rate + .001, Rates[^1]) : Rates.LastOrDefault(r => r < rate - .001, Rates[0]);
        Run(() => session.SetRateAsync(selected, lifetime.Token));
        ShowKeyHint(selected.ToString("0.##", CultureInfo.InvariantCulture) + "×");
    }
    private void CycleSubtitles()
    {
        var tracks = ViewModel.Snapshot.SubtitleTracks;
        var index = -1;
        for (var i = 0; i < tracks.Length; i++) if (tracks[i].Id == ViewModel.Snapshot.SelectedSubtitleTrackId) index = i;
        var next = index + 1 < tracks.Length ? tracks[index + 1] : null;
        Run(() => session.SelectSubtitleTrackAsync(next?.Id, lifetime.Token));
        ShowKeyHint("字幕：" + (next?.Label ?? "关闭"));
    }
    private void CycleAudio()
    {
        var tracks = ViewModel.Snapshot.AudioTracks;
        if (tracks.IsEmpty) { ShowKeyHint("这个文件没有可选音轨"); return; }
        var index = -1;
        for (var i = 0; i < tracks.Length; i++) if (tracks[i].Id == ViewModel.Snapshot.SelectedAudioTrackId) index = i;
        var next = tracks[(index + 1) % tracks.Length];
        Run(() => session.SelectAudioTrackAsync(next.Id, lifetime.Token));
        ShowKeyHint("音轨：" + next.Label);
    }

    private void OnSeekPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.CanControl || !e.GetCurrentPoint(SeekSlider).Properties.IsLeftButtonPressed) return;
        BeginSeek();
    }
    private void BeginSeek()
    {
        if (disposed || closing || !ViewModel.CanControl) return;
        seekEditVersion++;
        draggingSeek = true;
        pointerPressed = true;
        Activity();
        ViewModel.PreviewSeek(SeekSlider.Value);
        SeekTip.Visibility = Visibility.Visible;
        UpdateSeekTip();
    }
    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (settingControls || !ViewModel.CanControl) return;
        ViewModel.PreviewSeek(e.NewValue);
        if (draggingSeek) UpdateSeekTip();
        else
        {
            // Slider may update its value before its handled PointerPressed event reaches us.
            // Yield to that event before treating a change as keyboard/UIA input.
            lastCommand = CommitNonPointerSeekAsync(++seekEditVersion, ViewModel.DisplayPositionTicks);
        }
    }
    private async Task CommitNonPointerSeekAsync(int version, long ticks)
    {
        await Task.Yield();
        if (disposed || closing || draggingSeek || version != seekEditVersion) return;
        ViewModel.CommitSeekPreview();
        if (XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, SeekSlider))
            ShowKeyHint("跳转至 " + PlayerViewModel.FormatTicks(ticks));
        await RunAsync(() => session.SeekAsync(TimeSpan.FromTicks(ticks), lifetime.Token), cancelSeekOnError: true);
    }
    private void OnSeekReleased(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void OnSeekCaptureLost(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void CommitSeek()
    {
        if (!draggingSeek) return;
        seekEditVersion++;
        draggingSeek = false;
        pointerPressed = false;
        SeekTip.Visibility = seekPointerInside ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.CommitSeekPreview();
        var target = TimeSpan.FromTicks(ViewModel.DisplayPositionTicks);
        Run(() => session.SeekAsync(target, lifetime.Token), cancelSeekOnError: true);
        if (seekPointerInside) UpdateSeekTip();
    }
    private void OnSeekPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var width = Math.Max(1, SeekHost.ActualWidth - 14);
        PreviewSeekHover((e.GetCurrentPoint(SeekHost).Position.X - 7) / width);
    }
    private void OnSeekPointerExited(object sender, PointerRoutedEventArgs e) => ExitSeekHover();
    private void PreviewSeekHover(double fraction)
    {
        if (disposed || closing || !ViewModel.CanControl) return;
        seekPointerInside = true;
        seekHoverFraction = Math.Clamp(fraction, 0, 1);
        SeekTip.Visibility = Visibility.Visible;
        UpdateSeekTip();
    }
    private void ExitSeekHover()
    {
        seekPointerInside = false;
        if (!draggingSeek) SeekTip.Visibility = Visibility.Collapsed;
    }
    private void UpdateSeekTip()
    {
        seekTipSeconds = draggingSeek ? SeekSlider.Value : seekHoverFraction * SeekSlider.Maximum;
        SeekTipText.Text = PlayerViewModel.FormatTicks((long)(seekTipSeconds * TimeSpan.TicksPerSecond));
        // 外层 DesiredSize 包含上次定位的 Margin；只量内容，避免连续悬停时气泡向左漂移。
        SeekTipText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = SeekTipText.DesiredSize.Width + SeekTip.Padding.Left + SeekTip.Padding.Right +
            SeekTip.BorderThickness.Left + SeekTip.BorderThickness.Right;
        var x = 7 + (SeekSlider.Maximum > 0 ? seekTipSeconds / SeekSlider.Maximum : 0) * Math.Max(0, SeekHost.ActualWidth - 14);
        SeekTip.Margin = new(Math.Clamp(x - width / 2, 0, Math.Max(0, SeekHost.ActualWidth - width)), -28, 0, 0);
    }
    private void OnSeekHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawBuffers();
        if (SeekTipVisible) UpdateSeekTip();
    }
    private void DrawBuffers()
    {
        var duration = ViewModel.Snapshot.DurationTicks;
        var width = Math.Max(0, SeekHost.ActualWidth - 14);
        var used = 0;
        if (duration > 0 && width > 0)
        {
            foreach (var range in ViewModel.Snapshot.BufferedRanges)
            {
                var left = 7 + Math.Clamp((double)range.StartTicks / duration, 0, 1) * width;
                var right = 7 + Math.Clamp((double)range.EndTicks / duration, 0, 1) * width;
                if (right <= left) continue;
                Border bar;
                if (used < BufferedCanvas.Children.Count) bar = BufferedCanvas.Children[used].As<Border>();
                else
                {
                    bar = XamlResources.Border(XamlResources.Template(Resources, "BufferedRangeTemplate"));
                    BufferedCanvas.Children.Add(bar);
                }
                bar.Width = right - left;
                Canvas.SetLeft(bar, left);
                used++;
            }
        }
        while (BufferedCanvas.Children.Count > used) BufferedCanvas.Children.RemoveAt(BufferedCanvas.Children.Count - 1);
    }
    private void OnVolumeEntered(object sender, PointerRoutedEventArgs e)
    {
        volumePointerInside = true;
        VolumeSlider.Visibility = Visibility.Visible;
        Activity();
    }
    private void OnVolumeExited(object sender, PointerRoutedEventArgs e)
    {
        volumePointerInside = false;
        HideVolumeIfIdle();
    }
    private void OnVolumeGotFocus(object sender, RoutedEventArgs e)
    {
        VolumeSlider.Visibility = Visibility.Visible;
        Activity();
    }
    private void OnVolumeLostFocus(object sender, RoutedEventArgs e)
    {
        // 焦点先离开静音按钮再进入滑块；等焦点路由完成后决定是否收起。
        DispatcherQueue.TryEnqueue(HideVolumeIfIdle);
    }
    private void HideVolumeIfIdle()
    {
        if (disposed || volumePointerInside || pointerPressed) return;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, VolumeHost)) return;
            focused = VisualTreeHelper.GetParent(focused);
        }
        VolumeSlider.Visibility = Visibility.Collapsed;
    }
    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (settingControls || !ViewModel.CanControl) return;
        if (!dragVolume && XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, VolumeSlider))
            ShowKeyHint("音量 " + e.NewValue.ToString("0", CultureInfo.InvariantCulture) + "%");
        Run(() => session.SetVolumeAsync(e.NewValue, lifetime.Token));
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => Run(() => session.TogglePauseAsync(lifetime.Token));
    private void OnMuteClick(object sender, RoutedEventArgs e) => Run(() => session.SetMutedAsync(!ViewModel.Snapshot.IsMuted, lifetime.Token));
    private void OnPreviousClick(object sender, RoutedEventArgs e) => NavigateEpisode(next: false);
    private void OnNextClick(object sender, RoutedEventArgs e) => NavigateEpisode(next: true);
    private void NavigateEpisode(bool next)
    {
        if (!(next ? ViewModel.CanNext : ViewModel.CanPrevious)) return;
        Run(() => next ? session.NextAsync(lifetime.Token) : session.PreviousAsync(lifetime.Token));
    }
    private void OnRetryClick(object sender, RoutedEventArgs e) => Retry();
    private void Retry() => Run(() => session.RetryAsync(lifetime.Token));
    private void OnCloseClick(object sender, RoutedEventArgs e) => _ = CloseAsync();
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => window.ToggleFullscreen();
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => window.ToggleMaximize();
    private void OnDismissUpNextClick(object sender, RoutedEventArgs e) => ViewModel.DismissUpNext();
    private void OnEpisodesClick(object sender, RoutedEventArgs e)
    {
        ToggleEpisodePanel();
    }
    private void ToggleEpisodePanel()
    {
        if (disposed || closing || !ViewModel.HasEpisodes) return;
        episodePanelCollapsed = !episodePanelCollapsed;
        UpdateEpisodePanel();
        layoutSave = SaveEpisodePreferenceAsync(null, episodePanelCollapsed, layoutSave);
    }
    private void UpdateEpisodePanel()
    {
        var visible = ViewModel.HasEpisodes && !window.IsFullscreen && !episodePanelCollapsed;
        if (visible == EpisodePanelVisible) return;
        if (!visible && XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, EpisodePanel))
            Focus(FocusState.Programmatic);
        EpisodePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => LayoutChanged?.Invoke(this, EventArgs.Empty);
    private void OnEpisodeListClick(object sender, RoutedEventArgs e) => SetEpisodeLayout(false);
    private void OnEpisodeGridClick(object sender, RoutedEventArgs e) => SetEpisodeLayout(true);
    private void SetEpisodeLayout(bool grid)
    {
        if (disposed || closing) return;
        EpisodeListScroll.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        EpisodeGridScroll.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        // 写入按点击次序串行；原子变换保留同时修改的其他设置。关闭播放页不取消已确认的偏好。
        layoutSave = SaveEpisodePreferenceAsync(grid, null, layoutSave);
    }
    private async Task SaveEpisodePreferenceAsync(bool? grid, bool? collapsed, Task previousSave)
    {
        await previousSave;
        try
        {
            await settings.UpdateAsync(current => current with
            {
                UseEpisodeGrid = grid ?? current.UseEpisodeGrid,
                EpisodePanelCollapsed = collapsed ?? current.EpisodePanelCollapsed
            });
        }
        catch (Exception exception) when (exception is AppException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // ToastService 是窗口级服务；外部会话关闭后也不能静默隐藏保存失败。
            toasts.Show(ToastKind.Warning, "选集偏好暂时未能保存，下次打开会使用原设置。");
        }
        catch (OperationCanceledException)
        {
            toasts.Show(ToastKind.Warning, "选集偏好保存已取消，下次打开会使用原设置。");
        }
    }
    private void OnEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanControl || sender is not Button { Tag: string id }) return;
        SelectEpisode(id);
    }
    private void SelectEpisode(string itemId)
    {
        if (!ViewModel.CanControl || !ViewModel.Episodes.Any(episode => episode.ItemId == itemId)) return;
        Run(() => session.SelectEntryAsync(itemId, lifetime.Token));
    }

    private void OnRateClick(object sender, RoutedEventArgs e) => CreateRateMenu().ShowAt(RateButton);
    private MenuFlyout CreateRateMenu()
    {
        var menu = CreateMenu();
        foreach (var rate in Rates)
        {
            var choice = rate;
            var item = new ToggleMenuFlyoutItem { Text = rate == 1 ? "正常" : rate.ToString("0.##", CultureInfo.InvariantCulture) + "×", IsChecked = Math.Abs(ViewModel.Snapshot.PlaybackRate - rate) < .001 };
            RegisterMenuItem(menu, item, (_, _) => SelectRate(choice));
            menu.Items.Add(item);
        }
        return menu;
    }
    private void OnTracksClick(object sender, RoutedEventArgs e) => CreateTracksMenu().ShowAt(TracksButton);
    private MenuFlyout CreateTracksMenu()
    {
        var menu = CreateMenu();
        menu.Items.Add(new MenuFlyoutItem { Text = "字幕", IsEnabled = false, Style = XamlResources.Style(Resources, "PlayerMenuHeading") });
        var off = new ToggleMenuFlyoutItem { Text = "关闭字幕", IsChecked = ViewModel.Snapshot.SelectedSubtitleTrackId is null };
        RegisterMenuItem(menu, off, (_, _) => SelectTrack(null, subtitle: true));
        menu.Items.Add(off);
        foreach (var track in ViewModel.Snapshot.SubtitleTracks.Take(32)) AddTrack(menu, track, subtitle: true);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "音轨", IsEnabled = false, Style = XamlResources.Style(Resources, "PlayerMenuHeading") });
        foreach (var track in ViewModel.Snapshot.AudioTracks.Take(32)) AddTrack(menu, track, subtitle: false);
        if (ViewModel.Snapshot.AudioTracks.IsEmpty && ViewModel.Snapshot.SubtitleTracks.IsEmpty)
            menu.Items.Add(new MenuFlyoutItem { Text = "这个文件没有可选轨道", IsEnabled = false, Style = XamlResources.Style(Resources, "PlayerMenuHeading") });
        return menu;
    }
    private void AddTrack(MenuFlyout menu, TrackInfo track, bool subtitle)
    {
        var selected = subtitle ? ViewModel.Snapshot.SelectedSubtitleTrackId : ViewModel.Snapshot.SelectedAudioTrackId;
        var item = new ToggleMenuFlyoutItem { Text = track.Label.Length > 96 ? track.Label[..96] : track.Label, IsChecked = selected == track.Id };
        RegisterMenuItem(menu, item, (_, _) => SelectTrack(track.Id, subtitle));
        menu.Items.Add(item);
    }
    private void SelectRate(double rate)
    {
        HideMenus();
        Run(() => session.SetRateAsync(rate, lifetime.Token));
    }
    private void SelectTrack(string? trackId, bool subtitle)
    {
        HideMenus();
        Run(() => subtitle ? session.SelectSubtitleTrackAsync(trackId, lifetime.Token) : session.SelectAudioTrackAsync(trackId, lifetime.Token));
    }
    private MenuFlyout CreateMenu()
    {
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = XamlResources.Style(Resources, "PlayerMenuPresenter") };
        menuHandlers.Add(menu, []);
        menu.Opened += OnMenuOpened;
        menu.Closed += OnMenuClosed;
        return menu;
    }

    private void RegisterMenuItem(MenuFlyout menu, MenuFlyoutItem item, RoutedEventHandler handler)
    {
        item.Style = XamlResources.Style(Resources, "PlayerMenuChoice");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, item.Text);
        item.Click += handler;
        menuHandlers[menu].Add((item, handler));
    }

    private void OnMenuOpened(object? sender, object args)
    {
        if (sender is null) return;
        var menu = sender.As<MenuFlyout>();
        if (disposed || closing) { menu.Hide(); return; }
        if (!openFlyouts.Contains(menu)) openFlyouts.Add(menu);
        openMenus = openFlyouts.Count;
        Activity();
    }

    private void OnMenuClosed(object? sender, object args)
    {
        if (sender is null) return;
        var menu = sender.As<MenuFlyout>();
        var removed = openFlyouts.Remove(menu);
        openMenus = openFlyouts.Count;
        DetachMenu(menu);
        if (removed && openMenus == 0 && !disposed && !closing)
        {
            Focus(FocusState.Programmatic);
            lastActivity = Environment.TickCount64;
        }
    }

    private void DetachMenu(MenuFlyout menu)
    {
        menu.Opened -= OnMenuOpened;
        menu.Closed -= OnMenuClosed;
        if (menuHandlers.Remove(menu, out var handlers))
            foreach (var registration in handlers) registration.Item.Click -= registration.Handler;
    }

    private void HideMenus()
    {
        foreach (var menu in openFlyouts.ToArray()) menu.Hide();
        // Hide can animate asynchronously; only Closed removes an open instance.
    }

    private void DisposeMenus()
    {
        foreach (var menu in menuHandlers.Keys.ToArray())
        {
            menu.Hide();
            DetachMenu(menu);
        }
        openFlyouts.Clear();
        openMenus = 0;
    }
    private void Run(Func<Task> action, bool cancelSeekOnError = false) => lastCommand = RunAsync(action, cancelSeekOnError);

    private async Task RunAsync(Func<Task> action, bool cancelSeekOnError)
    {
        if (disposed || closing) return;
        try { await action(); }
        catch (OperationCanceledException) { if (cancelSeekOnError) ViewModel.CancelSeekPreview(); }
        catch (AppException ex) { if (cancelSeekOnError) ViewModel.CancelSeekPreview(); toasts.Show(ToastKind.Error, ex.Error.Message); }
    }
    private static bool IsWithin(DependencyObject? element, DependencyObject parent)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }
}
