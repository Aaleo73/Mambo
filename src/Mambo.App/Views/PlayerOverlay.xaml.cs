using System.ComponentModel;
using System.Globalization;
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
    private Control? drawerPreviousFocus;

    public PlayerOverlay(IPlaybackSession session, WindowContext window, ToastService toasts, ISettingsService settings)
    {
        this.session = session;
        this.window = window;
        this.toasts = toasts;
        this.settings = settings;
        ViewModel = new(session);
        InitializeComponent();
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
        RegisterRoutedHandler(VolumeSlider, PointerPressedEvent, new PointerEventHandler((_, _) => dragVolume = true));
        RegisterRoutedHandler(VolumeSlider, PointerReleasedEvent, new PointerEventHandler((_, _) => dragVolume = false));
        RegisterRoutedHandler(VolumeSlider, PointerCaptureLostEvent, new PointerEventHandler((_, _) => dragVolume = false));
        RegisterRoutedHandler(Chrome, PointerMovedEvent, new PointerEventHandler((_, _) => { pointerInside = true; Activity(); }));
        RegisterRoutedHandler(Chrome, PointerPressedEvent, new PointerEventHandler((_, _) => { pointerPressed = true; Activity(); }));
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
    internal bool EpisodeDrawerOpen => EpisodeDrawer.Visibility == Visibility.Visible;
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
    internal void ToggleEpisodesForSmoke() => ToggleEpisodeDrawer();
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
        drawerPreviousFocus = null;
        TitleChanged = null;
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
        EpisodeCloseButton.Click -= OnEpisodesClick;
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
        AttachSurface();
        UpdateControls();
        clock.Start();
        OnPresentationChanged(null, EventArgs.Empty);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void AttachSurface()
    {
        if (disposed || closing || !IsLoaded || attached || ViewModel.IsExternal) return;
        try { Surface.Attach(session); attached = true; }
        catch (AppException ex) { toasts.Show(ToastKind.Error, ex.Error.Message); }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (ViewModel.IsExternal && attached) { Surface.Detach(); attached = false; }
        else AttachSurface();
        window.SetPlaybackActive(!closing && ViewModel.CanControl && !ViewModel.IsPaused);
        // 外部窗口面板一直保留控制；内置画面按空闲时间收起控制。
        if (ViewModel.IsExternal || ViewModel.IsFailed) SetChrome(true);
        UpdateControls();
        UpdateStatus();
        DrawBuffers();
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
        if (chromeVisible == visible) { UpdateCursor(); return; }
        chromeVisible = visible;
        Chrome.IsHitTestVisible = visible;
        var visual = ElementCompositionPreview.GetElementVisual(Chrome);
        StopChromeAnimation(visual);
        Chrome.Opacity = visible ? 1 : 0;
        if (Motion.AnimationsEnabled)
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
        var hide = ViewModel.CanControl && !ViewModel.IsPaused && !chromeVisible && pointerInside && !EpisodeDrawerOpen;
        ProtectedCursor = hide ? null : arrow;
        Surface.HideCursor(hide);
    }

    private void UpdateStatus()
    {
        BigPlay.Visibility = ViewModel.IsPaused && chromeVisible && !ViewModel.IsExternal ? Visibility.Visible : Visibility.Collapsed;
        UpNext.Visibility = ViewModel.ShowUpNext && !EpisodeDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
        EpisodeList.IsHitTestVisible = ViewModel.CanControl;
        EpisodeGrid.IsHitTestVisible = ViewModel.CanControl;
        EpisodeList.Opacity = EpisodeGrid.Opacity = ViewModel.CanControl ? 1 : .45;
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
        FullscreenTitles.Visibility = window.IsFullscreen ? Visibility.Visible : Visibility.Collapsed;
        FullscreenGlyph.Glyph = window.IsFullscreen ? "\uE73F" : "\uE740";
        MaximizeGlyph.Glyph = window.IsMaximized ? "\uE923" : "\uE922";
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
        // 音量滑块获得键盘焦点时，方向键交给其 RangeValue 操作；其他区域沿用播放器快捷键。
        if (IsWithin(e.OriginalSource as DependencyObject, VolumeSlider) &&
            e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            Activity();
            return;
        }
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (HandleKey(e.Key, alt, control)) e.Handled = true;
    }

    private bool HandleKey(VirtualKey key, bool alt, bool control)
    {
        if (disposed || closing) return false;
        lastCommand = Task.CompletedTask;
        Activity();
        if (key == VirtualKey.F11) { HideMenus(); window.ToggleFullscreen(); return true; }
        if (key == VirtualKey.Escape)
        {
            if (openMenus > 0) HideMenus();
            else if (EpisodeDrawerOpen) ToggleEpisodeDrawer();
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
            case VirtualKey.Space: Run(() => session.TogglePauseAsync(lifetime.Token)); break;
            case VirtualKey.M: Run(() => session.SetMutedAsync(!ViewModel.Snapshot.IsMuted, lifetime.Token)); break;
            case VirtualKey.Left: SeekRelative(-5); break;
            case VirtualKey.Right: SeekRelative(5); break;
            case VirtualKey.Up: Run(() => session.SetVolumeAsync(Math.Min(100, ViewModel.Volume + 5), lifetime.Token)); break;
            case VirtualKey.Down: Run(() => session.SetVolumeAsync(Math.Max(0, ViewModel.Volume - 5), lifetime.Token)); break;
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
    }
    private void CycleSubtitles()
    {
        var tracks = ViewModel.Snapshot.SubtitleTracks;
        var index = -1;
        for (var i = 0; i < tracks.Length; i++) if (tracks[i].Id == ViewModel.Snapshot.SelectedSubtitleTrackId) index = i;
        var next = index + 1 < tracks.Length ? tracks[index + 1] : null;
        Run(() => session.SelectSubtitleTrackAsync(next?.Id, lifetime.Token));
        toasts.Show(ToastKind.Info, "字幕：" + (next?.Label ?? "关闭"));
    }
    private void CycleAudio()
    {
        var tracks = ViewModel.Snapshot.AudioTracks;
        if (tracks.IsEmpty) { toasts.Show(ToastKind.Info, "这个文件没有可选音轨"); return; }
        var index = -1;
        for (var i = 0; i < tracks.Length; i++) if (tracks[i].Id == ViewModel.Snapshot.SelectedAudioTrackId) index = i;
        var next = tracks[(index + 1) % tracks.Length];
        Run(() => session.SelectAudioTrackAsync(next.Id, lifetime.Token));
        toasts.Show(ToastKind.Info, "音轨：" + next.Label);
    }

    private void OnSeekPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.CanControl || !e.GetCurrentPoint(SeekSlider).Properties.IsLeftButtonPressed) return;
        BeginSeek();
    }
    private void BeginSeek()
    {
        if (disposed || closing || !ViewModel.CanControl) return;
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
            // 原生键盘 Home/End/Page 键及自动化 RangeValue 不走指针拖动事件。
            ViewModel.CommitSeekPreview();
            Run(() => session.SeekAsync(TimeSpan.FromTicks(ViewModel.DisplayPositionTicks), lifetime.Token), cancelSeekOnError: true);
        }
    }
    private void OnSeekReleased(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void OnSeekCaptureLost(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void CommitSeek()
    {
        if (!draggingSeek) return;
        draggingSeek = false;
        pointerPressed = false;
        SeekTip.Visibility = Visibility.Collapsed;
        ViewModel.CommitSeekPreview();
        var target = TimeSpan.FromTicks(ViewModel.DisplayPositionTicks);
        Run(() => session.SeekAsync(target, lifetime.Token), cancelSeekOnError: true);
    }
    private void UpdateSeekTip()
    {
        SeekTipText.Text = ViewModel.PositionText;
        var x = SeekSlider.Maximum > 0 ? SeekSlider.Value / SeekSlider.Maximum * SeekHost.ActualWidth : 0;
        SeekTip.Margin = new(Math.Clamp(x - 24, 0, Math.Max(0, SeekHost.ActualWidth - 56)), -28, 0, 0);
    }
    private void OnSeekHostSizeChanged(object sender, SizeChangedEventArgs e) => DrawBuffers();
    private void DrawBuffers()
    {
        BufferedCanvas.Children.Clear();
        var duration = ViewModel.Snapshot.DurationTicks;
        if (duration <= 0 || SeekHost.ActualWidth <= 0) return;
        foreach (var range in ViewModel.Snapshot.BufferedRanges)
        {
            var left = Math.Clamp((double)range.StartTicks / duration, 0, 1) * SeekHost.ActualWidth;
            var right = Math.Clamp((double)range.EndTicks / duration, 0, 1) * SeekHost.ActualWidth;
            if (right <= left) continue;
            var bar = XamlResources.Border(XamlResources.Template(Resources, "BufferedRangeTemplate"));
            bar.Width = right - left;
            Canvas.SetLeft(bar, left);
            BufferedCanvas.Children.Add(bar);
        }
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
        if (!settingControls && ViewModel.CanControl) Run(() => session.SetVolumeAsync(e.NewValue, lifetime.Token));
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
        ToggleEpisodeDrawer();
    }
    private void ToggleEpisodeDrawer()
    {
        if (disposed) return;
        var close = EpisodeDrawerOpen;
        if (!close) drawerPreviousFocus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        EpisodeDrawer.Visibility = close ? Visibility.Collapsed : Visibility.Visible;
        if (!close) EpisodeCloseButton.Focus(FocusState.Programmatic);
        else
        {
            if (drawerPreviousFocus is { IsLoaded: true, IsEnabled: true }) drawerPreviousFocus.Focus(FocusState.Programmatic);
            else Focus(FocusState.Programmatic);
            drawerPreviousFocus = null;
        }
        UpdateStatus();
        UpdateCursor();
    }
    private void OnEpisodeListClick(object sender, RoutedEventArgs e) => SetEpisodeLayout(false);
    private void OnEpisodeGridClick(object sender, RoutedEventArgs e) => SetEpisodeLayout(true);
    private void SetEpisodeLayout(bool grid)
    {
        if (disposed || closing) return;
        EpisodeListScroll.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        EpisodeGridScroll.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        // 写入按点击次序串行；原子变换保留同时修改的其他设置。关闭播放页不取消已确认的偏好。
        layoutSave = SaveEpisodeLayoutAsync(grid, layoutSave);
    }
    private async Task SaveEpisodeLayoutAsync(bool grid, Task previousSave)
    {
        await previousSave;
        try
        {
            if (settings.Current.UseEpisodeGrid != grid)
                await settings.UpdateAsync(current => current with { UseEpisodeGrid = grid });
        }
        catch (Exception exception) when (exception is AppException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // ToastService 是窗口级服务；外部会话关闭后也不能静默隐藏保存失败。
            toasts.Show(ToastKind.Warning, "选集布局暂时未能保存，下次打开会使用原设置。");
        }
        catch (OperationCanceledException)
        {
            toasts.Show(ToastKind.Warning, "选集布局保存已取消，下次打开会使用原设置。");
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
        menu.Items.Add(new MenuFlyoutItem { Text = "字幕", IsEnabled = false });
        var off = new ToggleMenuFlyoutItem { Text = "关闭字幕", IsChecked = ViewModel.Snapshot.SelectedSubtitleTrackId is null };
        RegisterMenuItem(menu, off, (_, _) => SelectTrack(null, subtitle: true));
        menu.Items.Add(off);
        foreach (var track in ViewModel.Snapshot.SubtitleTracks.Take(32)) AddTrack(menu, track, subtitle: true);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "音轨", IsEnabled = false });
        foreach (var track in ViewModel.Snapshot.AudioTracks.Take(32)) AddTrack(menu, track, subtitle: false);
        if (ViewModel.Snapshot.AudioTracks.IsEmpty && ViewModel.Snapshot.SubtitleTracks.IsEmpty)
            menu.Items.Add(new MenuFlyoutItem { Text = "这个文件没有可选轨道", IsEnabled = false });
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
