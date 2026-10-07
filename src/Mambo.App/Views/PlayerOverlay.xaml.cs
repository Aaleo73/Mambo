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
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
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
    private readonly IBulletChatService bulletChat;
    private readonly DispatcherQueueTimer clock;
    private readonly DispatcherQueueTimer singleClick;
    private readonly InputSystemCursor arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly CancellationTokenSource lifetime = new();
    private readonly WindowMotionObserver motionObserver;
    private readonly PopupTransition bigPlayTransition;
    private readonly PopupTransition upNextTransition;
    private readonly PopupTransition episodeTransition;
    private readonly long episodeVisibilityToken;
    private bool episodesAvailable;
    private readonly List<FlyoutBase> openFlyouts = [];
    private FlyoutBase[] panelFlyouts = [];
    private readonly List<WeakReference<Button>> episodeButtons = [];
    private readonly List<(UIElement Element, RoutedEvent Event, object Handler)> routedHandlers = [];
    private ScalarKeyFrameAnimation? chromeAnimation;
    private CubicBezierEasingFunction? chromeEasing;
    private CompositionScopedBatch? chromeBatch;
    private ScalarKeyFrameAnimation? hintAnimation;
    private CubicBezierEasingFunction? hintEasing;
    private CompositionScopedBatch? hintBatch;
    private ScalarKeyFrameAnimation? pulseAnimation;
    private ScalarKeyFrameAnimation? pingFadeAnimation;
    private Vector3KeyFrameAnimation? pingAnimation;
    private bool openingAnimationRunning;
    private bool bufferingAnimationRunning;
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
    private bool presentationFrozen;
    private bool closing;
    private bool attached;
    private int openMenus;
    private string? previousErrorCode;
    private AppError? previousVideoQualityError;
    private bool dragVolume;
    private Task lastCommand = Task.CompletedTask;
    private Task layoutSave = Task.CompletedTask;
    private bool volumePointerInside;
    private bool volumePopupPointerInside;
    private double seekTipSeconds;

    public PlayerOverlay(IPlaybackSession session, WindowContext window, ToastService toasts, ISettingsService settings, IBulletChatService bulletChat)
    {
        this.session = session;
        this.window = window;
        this.toasts = toasts;
        this.settings = settings;
        this.bulletChat = bulletChat;
        ViewModel = new(session);
        InitializeComponent();
        SubtitlesPanel.EnableSubtitleControls(session);
        BulletChatPanel.Initialize(bulletChat, settings, () => ViewModel.Snapshot.Entry is { } entry ? entry.SeriesName ?? entry.Title : null);
        BulletChatView.Apply(settings.Current.BulletChat);
        episodePanelCollapsed = settings.Current.EpisodePanelCollapsed;
        motionObserver = new(window, DispatcherQueue, OnMotionChanged);
        // 大播放钮在控制层里，只淡变；即将播放卡片是独立弹层，带 4 DIP 位移。
        bigPlayTransition = new(BigPlay, rise: 0);
        upNextTransition = new(UpNext);
        // 选集栏只淡变；它一折叠或出现，画面卡片就跟着改右边距
        episodeTransition = new(EpisodePanel, rise: 0);
        episodeVisibilityToken = EpisodePanel.RegisterPropertyChangedCallback(VisibilityProperty, OnEpisodePanelVisibilityChanged);
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
        RegisterRoutedHandler(VolumeSlider, PointerReleasedEvent, new PointerEventHandler(OnVolumeDragEnded));
        RegisterRoutedHandler(VolumeSlider, PointerCanceledEvent, new PointerEventHandler(OnVolumeDragEnded));
        RegisterRoutedHandler(VolumeSlider, PointerCaptureLostEvent, new PointerEventHandler(OnVolumeDragEnded));
        RegisterRoutedHandler(Chrome, PointerMovedEvent, new PointerEventHandler((_, _) => { pointerInside = true; Activity(); }));
        RegisterRoutedHandler(Chrome, PointerPressedEvent, new PointerEventHandler((_, _) => { pointerPressed = true; Activity(); }));
        RegisterRoutedHandler(EpisodePanel, PointerEnteredEvent, new PointerEventHandler(OnEpisodePanelPointerEntered));
        RegisterRoutedHandler(EpisodePanel, PointerMovedEvent, new PointerEventHandler(OnEpisodePanelPointerEntered));
        RegisterRoutedHandler(this, PointerReleasedEvent, new PointerEventHandler(OnAnyPointerReleased));
        RegisterRoutedHandler(this, PointerCanceledEvent, new PointerEventHandler(OnAnyPointerReleased));
        RegisterRoutedHandler(this, PointerCaptureLostEvent, new PointerEventHandler(OnAnyPointerReleased));
        session.SnapshotChanged += OnSnapshotChanged;
        ViewModel.PropertyChanged += OnProjectionChanged;
        bulletChat.Changed += OnBulletChatChanged;
        settings.Changed += OnBulletChatSettingsChanged;
        BulletChatPanel.PreviewChanged += OnBulletChatPreview;
        BulletChatPanel.Completed += OnBulletChatPanelCompleted;
        // 面板随播放层存活，可以反复打开；打开与收起统一记账，供控制层显隐和 Esc 使用。
        panelFlyouts = [RateFlyout, VideoQualityFlyout, BulletChatFlyout, SubtitlesFlyout, AudioFlyout];
        foreach (var flyout in panelFlyouts)
        {
            flyout.Opened += OnMenuOpened;
            flyout.Closed += OnMenuClosed;
        }
        RateFlyout.Opening += OnRateOpening;
        VideoQualityFlyout.Opening += OnVideoQualityOpening;
        SubtitlesFlyout.Opening += OnSubtitlesOpening;
        AudioFlyout.Opening += OnAudioOpening;
        window.PresentationChanged += OnPresentationChanged;
        window.ActiveChanged += OnWindowActiveChanged;
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
    internal bool EpisodePanelCollapsed => episodePanelCollapsed;
    internal bool CanToggleEpisodes => episodesAvailable;
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
    internal bool SeekTipVisible => SeekTip.Visibility == Visibility.Visible;
    internal double DisplayedSeekSeconds => SeekSlider.Value;
    internal Task PendingPreferenceSave => layoutSave;
    internal bool IsPresentationFrozen => presentationFrozen;
    internal bool HasAttachedSurface => attached;
    internal bool HasVideoSurface => VideoHost.Children.Contains(Surface);
    internal Mambo.App.BulletChat.BulletChatLayer BulletChatLayer => BulletChatView;
    internal BulletChatPanel BulletChatPanelView => BulletChatPanel;
    internal bool BulletChatButtonDimmed => BulletChatGlyph.Opacity < 1;
    internal void ShowBulletChatPanelForSmoke() => BulletChatFlyout.ShowAt(BulletChatButton);
    internal bool IsClockRunning => clock.IsRunning;
    internal bool IsSingleClickPending => singleClick.IsRunning;
    internal bool StateAnimationsRunning => openingAnimationRunning || bufferingAnimationRunning;
    public event EventHandler? TitleChanged;
    public event EventHandler? LayoutChanged;

    internal void ShowControlsForSmoke()
    {
        if (disposed || presentationFrozen || closing) return;
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

    internal PlayerChoicePanel ShowMenuForSmoke(bool tracks)
    {
        (tracks ? SubtitlesFlyout : RateFlyout).ShowAt(tracks ? SubtitlesButton : RateButton);
        return tracks ? SubtitlesPanel : RatePanel;
    }
    internal PlayerChoicePanel ShowAudioMenuForSmoke()
    {
        AudioFlyout.ShowAt(AudioButton);
        return AudioPanel;
    }
    internal PlayerChoicePanel ShowVideoQualityMenuForSmoke()
    {
        VideoQualityFlyout.ShowAt(VideoQualityButton);
        return VideoQualityPanel;
    }

    internal async Task DispatchSmokeVideoQualityAsync(VideoQualityMode mode)
    {
        SelectVideoQuality(mode);
        await lastCommand;
    }
    internal void ToggleEpisodes() => ToggleEpisodePanel(animate: true);
    internal void ToggleEpisodesForSmoke() => ToggleEpisodePanel(animate: false);
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
        if (disposed || presentationFrozen || closing || transitionActive) return;
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
        if (disposed || presentationFrozen || closing || transitionActive) return;
        VolumeSlider.Value = Math.Clamp(volume, 0, 100);
        await lastCommand;
    }

    public void SetLiveResize(bool active)
    {
        if (!disposed && !presentationFrozen) Surface.SetLiveResize(active);
    }

    public void SetTransitionActive(bool active)
    {
        if (disposed || presentationFrozen || transitionActive == active) return;
        transitionActive = active;
        IsHitTestVisible = !active;
        if (active)
        {
            CloseVolume();
            Surface.Detach();
            attached = false;
            Surface.Visibility = Visibility.Collapsed;
            UpdateBulletChat();
            clock.Stop();
            singleClick.Stop();
            HideMenus();
            // The fold presents this XAML, not the detached video. Keep the existing
            // controls readable without starting a second entrance animation.
            SetChrome(true, animate: false);
            StopHintAnimation();
            KeyHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetChrome(ShouldShowChrome(), animate: false);
            OnSnapshotChanged(null, EventArgs.Empty);
            if (IsLoaded && !closing) clock.Start();
        }
        UpdateStatus();
    }

    public async Task CloseAsync()
    {
        if (closing || disposed || presentationFrozen) return;
        closing = true;
        singleClick.Stop();
        CloseVolume();
        HideMenus();
        window.SetPlaybackActive(false);
        Surface.Detach();
        attached = false;
        Surface.Visibility = Visibility.Collapsed;
        UpdateBulletChat();
        StopHintAnimation();
        KeyHint.Visibility = Visibility.Collapsed;
        SetChrome(true, animate: false);
        SettleOverlays();
        StopStateAnimations();
        try
        {
            await FlushPreferencesAsync();
            if (disposed || presentationFrozen) return;
            await session.CloseAsync();
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
            if (disposed || presentationFrozen) return;
            closing = false;
            OnSnapshotChanged(null, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            if (disposed || presentationFrozen) return;
            closing = false;
            OnSnapshotChanged(null, EventArgs.Empty);
        }
    }

    /// <summary>先释放活动资源，只留下翻折退场所需的静态 XAML；此方法不等待任何异步操作。</summary>
    internal void FreezeForClose()
    {
        if (disposed || presentationFrozen) return;
        ReleaseActiveResources();
    }

    private void ReleaseActiveResources()
    {
        if (presentationFrozen) return;
        CloseVolume();
        closing = true;
        // An idle player can have no visible controls. Resolve its static closing
        // face before freezing; otherwise detaching video leaves only black canvas.
        SetChrome(true, animate: false);
        // 选集栏先落到终态，画面卡片的右边距才和冻结面一致
        episodeTransition.Settle();
        EpisodePanel.UnregisterPropertyChangedCallback(VisibilityProperty, episodeVisibilityToken);
        presentationFrozen = true;
        seekEditVersion++;
        Bindings.StopTracking();
        session.SnapshotChanged -= OnSnapshotChanged;
        ViewModel.PropertyChanged -= OnProjectionChanged;
        bulletChat.Changed -= OnBulletChatChanged;
        settings.Changed -= OnBulletChatSettingsChanged;
        BulletChatPanel.PreviewChanged -= OnBulletChatPreview;
        BulletChatPanel.Completed -= OnBulletChatPanelCompleted;
        BulletChatPanel.Dispose();
        SubtitlesPanel.DisposeSubtitleControls();
        AudioPanel.DisposeSubtitleControls();
        RatePanel.DisposeSubtitleControls();
        VideoQualityPanel.DisposeSubtitleControls();
        ViewModel.Dispose();
        window.PresentationChanged -= OnPresentationChanged;
        window.ActiveChanged -= OnWindowActiveChanged;
        motionObserver.Dispose();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        foreach (var registration in routedHandlers)
            registration.Element.RemoveHandler(registration.Event, registration.Handler);
        routedHandlers.Clear();
        DetachXamlEvents();
        clock.Stop();
        singleClick.Stop();
        clock.Tick -= OnClock;
        singleClick.Tick -= OnSingleClick;
        SettleChrome();
        SettleOverlays();
        SettleHint();
        StopStateAnimations();
        DisposeMenus();
        IsHitTestVisible = false;
        DisableFrozenInput(this);
        draggingSeek = false;
        dragVolume = false;
        pointerPressed = false;
        Surface.Detach();
        attached = false;
        Surface.Dispose();
        VideoHost.Children.Remove(Surface);
        BulletChatView.Dispose();
        VideoHost.Children.Remove(BulletChatView);
        // 已确认的布局写入不使用 lifetime，Shell 仍可等待 PendingPreferenceSave。
        lifetime.Cancel();
        lifetime.Dispose();
        ProtectedCursor = null;
        window.SetPlaybackActive(false);
        arrow.Dispose();
        TitleChanged = null;
        LayoutChanged = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        ReleaseActiveResources();
        disposed = true;
        bigPlayTransition.Dispose();
        upNextTransition.Dispose();
        episodeTransition.Dispose();
        EpisodeList.ElementPrepared -= OnEpisodeElementPrepared;
        EpisodeGrid.ElementPrepared -= OnEpisodeElementPrepared;
        DetachXamlEvents();
        EpisodeList.ItemsSource = null;
        EpisodeGrid.ItemsSource = null;
        Content = null;
    }

    private static void DisableFrozenInput(DependencyObject node)
    {
        AutomationProperties.SetAccessibilityView(node, AccessibilityView.Raw);
        if (node is Control control) control.IsTabStop = false;
        if (node is UIElement element) element.ReleasePointerCaptures();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            DisableFrozenInput(VisualTreeHelper.GetChild(node, index));
    }

    private void OnEpisodeElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        // 退场树首次布局仍可能实现模板；冻结后只隔离新节点，最终 Dispose 才移除此钩子。
        if (disposed || presentationFrozen)
        {
            if (args.Element is Button retired) retired.Click -= OnEpisodeClick;
            DisableFrozenInput(args.Element);
            return;
        }
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
        VolumePopup.PointerEntered -= OnVolumeEntered;
        VolumePopup.PointerExited -= OnVolumeExited;
        VolumePopup.GotFocus -= OnVolumeGotFocus;
        VolumePopup.LostFocus -= OnVolumeLostFocus;
        VolumeCanvas.SizeChanged -= OnVolumeLayoutChanged;
        PlaybackActions.SizeChanged -= OnVolumeLayoutChanged;
        CloseButton.Click -= OnCloseClick;
        PreviousButton.Click -= OnPreviousClick;
        PauseButton.Click -= OnPauseClick;
        NextButton.Click -= OnNextClick;
        MuteButton.Click -= OnMuteClick;
        FullscreenButton.Click -= OnFullscreenClick;
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
        DetachEpisodeTemplateEvents(EpisodeList);
        DetachEpisodeTemplateEvents(EpisodeGrid);
    }

    private void DetachEpisodeTemplateEvents(DependencyObject node)
    {
        if (node is Button button) button.Click -= OnEpisodeClick;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            DetachEpisodeTemplateEvents(VisualTreeHelper.GetChild(node, index));
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
        if (disposed || presentationFrozen) return;
        if (!transitionActive) Focus(FocusState.Programmatic);
        OnSnapshotChanged(null, EventArgs.Empty);
        UpdateControls();
        if (!transitionActive && !closing) clock.Start();
        OnPresentationChanged(null, EventArgs.Empty);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (disposed || presentationFrozen) return;
        clock.Stop();
        singleClick.Stop();
        Surface.Detach();
        attached = false;
        Surface.Visibility = Visibility.Collapsed;
        DisposeMenus();
        StopHintAnimation();
        KeyHint.Visibility = Visibility.Collapsed;
        StopStateAnimations();
        SettleChrome();
        SettleOverlays();
        ProtectedCursor = arrow;
    }

    private void AttachSurface()
    {
        if (disposed || presentationFrozen || closing || transitionActive || !IsLoaded || attached || ViewModel.IsExternal) return;
        try { Surface.Attach(session); attached = true; }
        catch (AppException ex) { toasts.Show(ToastKind.Error, ex.Error.Message); }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        if (disposed || presentationFrozen) return;
        if ((ViewModel.IsExternal || transitionActive || closing) && attached) { Surface.Detach(); attached = false; }
        else AttachSurface();
        Surface.Visibility = !transitionActive && !closing && !ViewModel.IsExternal ? Visibility.Visible : Visibility.Collapsed;
        window.SetPlaybackActive(!closing && ViewModel.CanControl && !ViewModel.IsPaused);
        if (ViewModel.IsFailed) SetChrome(true);
        UpdateControls();
        UpdateStatus();
        DrawBuffers();
        UpdateEpisodePanel();
        UpdateBulletChat();
        if (openFlyouts.Contains(SubtitlesFlyout)) OnSubtitlesOpening(null, EventArgs.Empty);
        if (openFlyouts.Contains(AudioFlyout)) OnAudioOpening(null, EventArgs.Empty);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        if (ViewModel.IsFailed && ViewModel.Snapshot.Error is { } error && error.Code != previousErrorCode)
        {
            previousErrorCode = error.Code;
            var weakOwner = new WeakReference<PlayerOverlay>(this);
            toasts.Show(ToastKind.Error, "播放失败：" + error.Message, "重试", () =>
            {
                if (weakOwner.TryGetTarget(out var owner) && !owner.disposed && !owner.presentationFrozen) owner.Retry();
            });
        }
        else if (!ViewModel.IsFailed) previousErrorCode = null;
        var qualityError = ViewModel.Snapshot.VideoQualityError;
        if (!closing && qualityError is not null && qualityError != previousVideoQualityError)
            toasts.Show(ToastKind.Error, qualityError.Message);
        previousVideoQualityError = qualityError;
    }

    private void OnProjectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || presentationFrozen) return;
        if (e.PropertyName is nameof(PlayerViewModel.DisplayPositionTicks) or nameof(PlayerViewModel.ShowUpNext))
        {
            UpdateControls();
            UpdateStatus();
        }
    }

    private void OnClock(DispatcherQueueTimer sender, object args)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !IsLoaded) return;
        ViewModel.Tick();
        UpdateKeyHint();
        SetChrome(ShouldShowChrome());
    }

    private bool ShouldShowChrome()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var focusInControls = IsWithin(focused, Chrome) || IsWithin(focused, UpNext);
        return ViewModel.IsFailed || pointerPressed || openMenus > 0 || focusInControls
            || volumePointerInside || volumePopupPointerInside || dragVolume
            || pointerInside && Environment.TickCount64 - lastActivity < 3000;
    }

    private void OnSingleClick(DispatcherQueueTimer sender, object args)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !IsLoaded) return;
        if (ViewModel.CanControl) Run(() => session.TogglePauseAsync(lifetime.Token));
    }

    private void Activity()
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        lastActivity = Environment.TickCount64;
        SetChrome(true);
    }

    private void SetChrome(bool visible, bool animate = true)
    {
        if (disposed || presentationFrozen) return;
        visible |= transitionActive || closing;
        var changed = chromeVisible != visible;
        chromeVisible = visible;
        Chrome.IsHitTestVisible = visible && !transitionActive && !closing;
        if (!animate || !CanAnimate) SettleChrome();
        else if (changed)
        {
            var visual = ElementCompositionPreview.GetElementVisual(Chrome);
            // 替换动画时不 Stop、不写旧端点；仅目标帧从当前合成器呈现值续接。
            ReleaseChromeAnimation();
            chromeAnimation = visual.Compositor.CreateScalarKeyFrameAnimation();
            chromeEasing = Motion.CreateEasing(visual.Compositor, visible ? Motion.EaseOut : Motion.EaseIn);
            chromeAnimation.InsertKeyFrame(1, visible ? 1 : 0, chromeEasing);
            chromeAnimation.Duration = visible ? Motion.Feedback : Motion.Exit;
            chromeBatch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            chromeBatch.Completed += OnChromeCompleted;
            visual.StartAnimation("Opacity", chromeAnimation);
            chromeBatch.End();
        }
        Chrome.TabFocusNavigation = Chrome.IsHitTestVisible ? KeyboardNavigationMode.Local : KeyboardNavigationMode.Once;
        if (changed) UpdateStatus();
        UpdateCursor();
    }

    private void OnChromeCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (disposed || presentationFrozen || !ReferenceEquals(sender, chromeBatch)) return;
        SettleChrome();
    }

    private void SettleChrome()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Chrome);
        visual.StopAnimation("Opacity");
        ReleaseChromeAnimation();
        Chrome.Opacity = chromeVisible ? 1 : 0;
        visual.Opacity = chromeVisible ? 1 : 0;
    }

    private void ReleaseChromeAnimation()
    {
        // Visual 属于 XAML；这里只释放本播放层创建的动画、batch 与 easing。
        if (chromeBatch is not null)
        {
            chromeBatch.Completed -= OnChromeCompleted;
            chromeBatch.Dispose();
            chromeBatch = null;
        }
        chromeAnimation?.Dispose();
        chromeAnimation = null;
        chromeEasing?.Dispose();
        chromeEasing = null;
    }

    private void OnMotionChanged(bool enabled)
    {
        if (disposed || presentationFrozen) return;
        if (!enabled)
        {
            SettleChrome();
            SettleOverlays();
            SettleHint();
        }
        UpdateStateAnimations();
    }

    private void OnWindowActiveChanged(object? sender, EventArgs args)
    {
        if (disposed || presentationFrozen) return;
        if (!window.IsActive)
        {
            CloseVolume();
            SettleChrome();
            SettleOverlays();
            SettleHint();
        }
        UpdateStateAnimations();
    }

    private void UpdateCursor()
    {
        if (disposed || presentationFrozen) return;
        var hide = !transitionActive && ViewModel.CanControl && !ViewModel.IsPaused && !chromeVisible && pointerInside;
        ProtectedCursor = hide ? null : arrow;
        Surface.HideCursor(hide);
    }

    private void UpdateStatus()
    {
        if (disposed || presentationFrozen) return;
        var paused = ViewModel.IsPaused && !ViewModel.IsExternal;
        var upNext = ViewModel.ShowUpNext;
        // 逻辑关闭立即禁输入并交还焦点；退场结束才折叠。
        if (!upNext && upNextTransition.TargetVisible && XamlRoot is not null
            && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, UpNext))
            Focus(FocusState.Programmatic);
        BigPlay.IsHitTestVisible = paused;
        UpNext.IsHitTestVisible = upNext;
        Present(bigPlayTransition, paused);
        Present(upNextTransition, upNext);
        UpdateStateAnimations();
    }

    private bool CanAnimate => IsLoaded && window.IsActive && motionObserver.AnimationsEnabled && !transitionActive && !closing;

    private void Present(PopupTransition transition, bool visible)
    {
        var animate = CanAnimate;
        // 每次进度刷新都会经过这里：目标未变且没有在途动画时不重复落终态。
        if (transition.TargetVisible == visible && (animate || !transition.IsRunning)) return;
        _ = visible ? transition.OpenAsync(animate) : transition.CloseAsync(animate);
    }

    private void SettleOverlays()
    {
        bigPlayTransition.Settle();
        upNextTransition.Settle();
        episodeTransition.Settle();
    }

    private void UpdateControls()
    {
        if (disposed || presentationFrozen) return;
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
        if (disposed || presentationFrozen) return;
        CloseVolume();
        TopBar.Visibility = window.IsFullscreen ? Visibility.Visible : Visibility.Collapsed;
        FullscreenGlyph.Glyph = (string)Application.Current.Resources[window.IsFullscreen ? "IconFullscreenExit" : "IconFullscreen"];
        // 全屏、最大化这类切换直接落终态，不带着半透明的选集栏过去
        episodeTransition.Settle();
        UpdateEpisodePanel();
        UpdateViewportFrame();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // 窗口模式下画面是一张四边留白、四角全圆的卡片；右边有选集栏时，由选集栏自己的内边距充当间隔
    private void UpdateViewportFrame()
    {
        var framed = !window.IsFullscreen;
        VideoViewport.Margin = framed ? new Thickness(12, 12, EpisodePanelVisible ? 0 : 12, 12) : new Thickness(0);
        VideoViewport.CornerRadius = new CornerRadius(framed ? 12 : 0);
        VideoHost.CornerRadius = VideoViewport.CornerRadius;
        Surface.SetViewportClip(framed ? 12 : 0, topOnly: false);
    }

    private void OnEpisodePanelPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (disposed || presentationFrozen) return;
        pointerInside = false;
        UpdateCursor();
    }

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e) { pointerInside = true; Activity(); }
    private void OnSurfacePointerEntered(object sender, PointerRoutedEventArgs e) => pointerInside = true;
    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs e) => pointerInside = false;
    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
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
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanControl || Environment.TickCount64 < ignoreTapUntil) return false;
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
        if (disposed || presentationFrozen || closing || transitionActive) return;
        singleClick.Stop();
        ignoreTapUntil = Environment.TickCount64 + 300;
        if (ViewModel.CanControl) window.ToggleFullscreen();
    }

    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed) { HandleMouseButton(back: true); e.Handled = true; }
        else if (properties.IsXButton2Pressed) { HandleMouseButton(back: false); e.Handled = true; }
    }
    private void HandleMouseButton(bool back) => lastCommand = back ? CloseAsync() : Task.CompletedTask;

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        // Tab reveals the controls before normal focus navigation; the episode panel is not a focus trap.
        if (e.Key == VirtualKey.Tab)
        {
            Activity();
            var reverse = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (HandleVolumeTab(reverse)) { e.Handled = true; return; }
        }
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (HandleKey(e.Key, alt, control)) e.Handled = true;
    }

    private bool HandleKey(VirtualKey key, bool alt, bool control)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return false;
        lastCommand = Task.CompletedTask;
        if (key == VirtualKey.F11) { HideMenus(); window.ToggleFullscreen(); return true; }
        if (key == VirtualKey.Escape)
        {
            if (openMenus > 0) HideMenus();
            else if (VolumePopup.Visibility == Visibility.Visible) CloseVolume();
            else if (window.IsFullscreen) window.ExitFullscreen();
            else lastCommand = CloseAsync();
            return true;
        }
        if (alt && key == VirtualKey.Left) { lastCommand = CloseAsync(); return true; }
        if (alt && key == VirtualKey.Right) return true;
        // 文本、下拉选择与滑块使用自己的按键；编辑字幕时不能触发 C/V、空格或左右跳转。
        if (IsEditingControlFocused()) return false;
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
            case VirtualKey.D: ToggleBulletChat(); break;
            // 选集开关在标题栏，播放器的 Tab 循环到不了；键盘靠这个键。全屏时选集栏本来就不显示，不改偏好。
            case VirtualKey.E when ViewModel.HasEpisodes && !window.IsFullscreen: ToggleEpisodePanel(animate: true); break;
            case (VirtualKey)188: Run(() => session.StepFrameAsync(FrameStepDirection.Backward, lifetime.Token)); break;
            case (VirtualKey)190: Run(() => session.StepFrameAsync(FrameStepDirection.Forward, lifetime.Token)); break;
            case (VirtualKey)219: ChangeRate(-1); break;
            case (VirtualKey)221: ChangeRate(1); break;
            default: return false;
        }
        return true;
    }

    private bool IsEditingControlFocused()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox or PasswordBox or AutoSuggestBox or ComboBox or Slider or ColorPicker or CheckBox) return true;
            if (ReferenceEquals(current, this)) break;
        }
        return false;
    }

    // 覆盖层不在按钮组的视觉顺序内，显式连接静音、滑块和全屏的双向 Tab 顺序。
    internal bool HandleVolumeTab(bool reverse)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanControl || XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (IsWithin(focused, VolumeSlider))
        {
            var moved = (reverse ? MuteButton : FullscreenButton).Focus(FocusState.Keyboard);
            if (moved && !reverse) CloseVolume();
            return moved;
        }
        // 鼠标仍悬停时，向前离开全屏按钮也不再遍历 Chrome 末尾的覆盖层。
        if (!reverse && IsWithin(focused, FullscreenButton)) CloseVolume();
        if ((!reverse && IsWithin(focused, MuteButton)) || (reverse && IsWithin(focused, FullscreenButton)))
        {
            SetVolumeOpen(true);
            return VolumeSlider.Focus(FocusState.Keyboard);
        }
        return false;
    }

    private void ChangeVolume(double delta)
    {
        var volume = Math.Clamp(ViewModel.Volume + delta, 0, 100);
        ShowKeyHint("音量 " + volume.ToString("0", CultureInfo.InvariantCulture) + "%");
        Run(() => session.SetVolumeAsync(volume, lifetime.Token));
    }

    private void ShowKeyHint(string text)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        StopHintAnimation();
        KeyHintLabel.Text = text;
        KeyHint.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(KeyHint).Opacity = 1;
        KeyHint.Visibility = Visibility.Visible;
        hintUntil = Environment.TickCount64 + 1200;
        hintFading = false;
    }

    private void UpdateKeyHint()
    {
        if (disposed || presentationFrozen || !KeyHintVisible || hintFading) return;
        var remaining = hintUntil - Environment.TickCount64;
        if (remaining > Motion.Exit.TotalMilliseconds) return;
        if (!motionObserver.AnimationsEnabled || !window.IsActive)
        {
            if (remaining <= 0) KeyHint.Visibility = Visibility.Collapsed;
            return;
        }
        hintFading = true;
        var visual = ElementCompositionPreview.GetElementVisual(KeyHint);
        hintAnimation = visual.Compositor.CreateScalarKeyFrameAnimation();
        hintEasing = Motion.CreateEasing(visual.Compositor, Motion.EaseIn);
        hintAnimation.InsertKeyFrame(1, 0, hintEasing);
        hintAnimation.Duration = Motion.Exit;
        hintBatch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        hintBatch.Completed += OnHintCompleted;
        visual.StartAnimation("Opacity", hintAnimation);
        hintBatch.End();
    }

    private void OnHintCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (disposed || presentationFrozen || !ReferenceEquals(sender, hintBatch)) return;
        SettleHint();
    }

    private void StopHintAnimation()
    {
        ElementCompositionPreview.GetElementVisual(KeyHint).StopAnimation("Opacity");
        if (hintBatch is not null)
        {
            hintBatch.Completed -= OnHintCompleted;
            hintBatch.Dispose();
            hintBatch = null;
        }
        hintAnimation?.Dispose();
        hintAnimation = null;
        hintEasing?.Dispose();
        hintEasing = null;
    }

    private void SettleHint()
    {
        StopHintAnimation();
        if (hintFading) KeyHint.Visibility = Visibility.Collapsed;
        hintFading = false;
        KeyHint.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(KeyHint).Opacity = 1;
    }

    private void UpdateStateAnimations()
    {
        if (disposed || presentationFrozen) return;
        var active = IsLoaded && Visibility == Visibility.Visible && XamlRoot?.IsHostVisible == true
            && window.IsActive && !transitionActive && !closing && motionObserver.AnimationsEnabled;
        var opening = active && ViewModel.IsOpening && OpeningPanel.Visibility == Visibility.Visible;
        var buffering = active && ViewModel.IsBuffering && BufferingPill.Visibility == Visibility.Visible;
        if (opening == openingAnimationRunning && buffering == bufferingAnimationRunning) return;
        StopStateAnimations();
        openingAnimationRunning = opening;
        bufferingAnimationRunning = buffering;
        OpeningSpinner.IsActive = opening;
        if (buffering)
        {
            var dot = ElementCompositionPreview.GetElementVisual(BufferingDot);
            pulseAnimation = dot.Compositor.CreateScalarKeyFrameAnimation();
            pulseAnimation.InsertKeyFrame(0, .4f);
            pulseAnimation.InsertKeyFrame(.5f, 1);
            pulseAnimation.InsertKeyFrame(1, .4f);
            // 可见缓冲状态的指示周期，不属于有限过渡 token。
            pulseAnimation.Duration = TimeSpan.FromMilliseconds(1200);
            pulseAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
            dot.StartAnimation("Opacity", pulseAnimation);
        }
        if (opening)
        {
            var ping = ElementCompositionPreview.GetElementVisual(OpeningPing);
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
    }

    private void StopStateAnimations()
    {
        openingAnimationRunning = false;
        bufferingAnimationRunning = false;
        OpeningSpinner.IsActive = false;
        var dot = ElementCompositionPreview.GetElementVisual(BufferingDot);
        dot.StopAnimation("Opacity");
        dot.Opacity = 1;
        var ping = ElementCompositionPreview.GetElementVisual(OpeningPing);
        ping.StopAnimation("Opacity");
        ping.StopAnimation("Scale");
        ping.Opacity = 1;
        ping.Scale = Vector3.One;
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
        var generation = ViewModel.Snapshot.EntryGeneration;
        Run(() => session.SelectSubtitleTrackAsync(next?.Id, generation, lifetime.Token));
        ShowKeyHint("字幕：" + (next?.Label ?? "关闭"));
    }
    private void CycleAudio()
    {
        var tracks = ViewModel.Snapshot.AudioTracks;
        if (tracks.IsEmpty) { ShowKeyHint("这个文件没有可选音轨"); return; }
        var index = -1;
        for (var i = 0; i < tracks.Length; i++) if (tracks[i].Id == ViewModel.Snapshot.SelectedAudioTrackId) index = i;
        var next = tracks[(index + 1) % tracks.Length];
        var generation = ViewModel.Snapshot.EntryGeneration;
        Run(() => session.SelectAudioTrackAsync(next.Id, generation, lifetime.Token));
        ShowKeyHint("音轨：" + next.Label);
    }

    private void OnSeekPressed(object sender, PointerRoutedEventArgs e)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanControl || !e.GetCurrentPoint(SeekSlider).Properties.IsLeftButtonPressed) return;
        BeginSeek();
    }
    private void BeginSeek()
    {
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanControl) return;
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
        if (disposed || presentationFrozen || closing || transitionActive || settingControls || !ViewModel.CanControl) return;
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
        if (disposed || presentationFrozen || closing || transitionActive || draggingSeek || version != seekEditVersion) return;
        ViewModel.CommitSeekPreview();
        if (XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, SeekSlider))
            ShowKeyHint("跳转至 " + PlayerViewModel.FormatTicks(ticks));
        await RunAsync(() => session.SeekAsync(TimeSpan.FromTicks(ticks), lifetime.Token), cancelSeekOnError: true);
    }
    private void OnSeekReleased(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void OnSeekCaptureLost(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void CommitSeek()
    {
        if (disposed || presentationFrozen || closing || transitionActive || !draggingSeek) return;
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
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanControl) return;
        seekPointerInside = true;
        seekHoverFraction = Math.Clamp(fraction, 0, 1);
        SeekTip.Visibility = Visibility.Visible;
        UpdateSeekTip();
    }
    private void ExitSeekHover()
    {
        if (disposed || presentationFrozen) return;
        seekPointerInside = false;
        if (!draggingSeek) SeekTip.Visibility = Visibility.Collapsed;
    }
    private void UpdateSeekTip()
    {
        if (disposed || presentationFrozen) return;
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
        if (disposed || presentationFrozen) return;
        DrawBuffers();
        if (SeekTipVisible) UpdateSeekTip();
    }
    private void DrawBuffers()
    {
        if (disposed || presentationFrozen) return;
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
        => SetVolumePointerInside(ReferenceEquals(sender, VolumePopup), true);
    private void OnVolumeExited(object sender, PointerRoutedEventArgs e)
        => SetVolumePointerInside(ReferenceEquals(sender, VolumePopup), false);
    internal void SetVolumePointerInside(bool popup, bool inside)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        if (popup) volumePopupPointerInside = inside;
        else volumePointerInside = inside;
        if (inside)
        {
            SetVolumeOpen(true);
            Activity();
        }
        else
        {
            // 按钮和覆盖层是兄弟节点；等本次指针路由完成再判断，避免跨入滑块时闪退。
            DispatcherQueue.TryEnqueue(HideVolumeIfIdle);
        }
    }
    private void OnVolumeGotFocus(object sender, RoutedEventArgs e)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        SetVolumeOpen(true);
        Activity();
    }
    private void OnVolumeLostFocus(object sender, RoutedEventArgs e)
    {
        if (disposed || presentationFrozen) return;
        // 焦点先离开静音按钮再进入滑块；等焦点路由完成后决定是否收起。
        DispatcherQueue.TryEnqueue(HideVolumeIfIdle);
    }
    private void HideVolumeIfIdle()
    {
        if (disposed || presentationFrozen || volumePointerInside || volumePopupPointerInside || dragVolume || pointerPressed) return;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (IsWithin(focused, VolumeHost) || IsWithin(focused, VolumePopup)) return;
        SetVolumeOpen(false);
    }
    private void SetVolumeOpen(bool open)
    {
        VolumePopup.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        VolumeFill.Opacity = open ? 1 : 0;
        if (open) PositionVolumePopup();
    }
    private void CloseVolume()
    {
        volumePointerInside = volumePopupPointerInside = dragVolume = false;
        if (XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, VolumePopup))
            Focus(FocusState.Programmatic);
        SetVolumeOpen(false);
    }
    private void OnVolumeLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (!disposed && !presentationFrozen && VolumePopup is not null && VolumePopup.Visibility == Visibility.Visible)
            PositionVolumePopup();
    }
    private void PositionVolumePopup()
    {
        var anchor = VolumeHost.TransformToVisual(VolumeCanvas).TransformPoint(new(0, 0));
        Canvas.SetLeft(VolumePopup, anchor.X + (VolumeHost.ActualWidth - VolumePopup.Width) / 2);
        Canvas.SetTop(VolumePopup, Math.Max(0, anchor.Y - VolumePopup.Height));
    }
    private void OnVolumeDragEnded(object sender, PointerRoutedEventArgs e)
    {
        dragVolume = false;
        DispatcherQueue.TryEnqueue(HideVolumeIfIdle);
    }
    private void OnAnyPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        pointerPressed = false;
        DispatcherQueue.TryEnqueue(HideVolumeIfIdle);
    }
    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (disposed || presentationFrozen || closing || transitionActive || settingControls || !ViewModel.CanControl) return;
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
        if (disposed || presentationFrozen || closing || transitionActive || !(next ? ViewModel.CanNext : ViewModel.CanPrevious)) return;
        Run(() => next ? session.NextAsync(lifetime.Token) : session.PreviousAsync(lifetime.Token));
    }
    private void OnRetryClick(object sender, RoutedEventArgs e) => Retry();
    private void Retry() => Run(() => session.RetryAsync(lifetime.Token));
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (!transitionActive) _ = CloseAsync();
    }
    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        if (!disposed && !presentationFrozen && !closing && !transitionActive) window.ToggleFullscreen();
    }
    private void OnDismissUpNextClick(object sender, RoutedEventArgs e)
    {
        if (!disposed && !presentationFrozen && !closing && !transitionActive) ViewModel.DismissUpNext();
    }
    private void ToggleEpisodePanel(bool animate)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.HasEpisodes) return;
        episodePanelCollapsed = !episodePanelCollapsed;
        UpdateEpisodePanel(animate && CanAnimate);
        layoutSave = SaveEpisodePreferenceAsync(null, episodePanelCollapsed, layoutSave);
        // 标题栏的收起 / 展开按钮要立刻换提示，不等退场结束
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateEpisodePanel(bool animate = false)
    {
        if (disposed || presentationFrozen) return;
        var available = ViewModel.HasEpisodes && !window.IsFullscreen;
        var visible = available && !episodePanelCollapsed;
        if (visible != episodeTransition.TargetVisible)
        {
            // 逻辑收起立即禁输入并交还焦点；退场结束才折叠，画面那时再变宽。
            if (!visible && XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, EpisodePanel))
                Focus(FocusState.Programmatic);
            EpisodePanel.IsHitTestVisible = visible;
            _ = visible ? episodeTransition.OpenAsync(animate) : episodeTransition.CloseAsync(animate);
        }
        if (available == episodesAvailable) return;
        episodesAvailable = available;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnEpisodePanelVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (disposed || presentationFrozen) return;
        UpdateViewportFrame();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!disposed && !presentationFrozen) LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnEpisodeListClick(object sender, RoutedEventArgs e) => SetEpisodeLayout(false);
    private void OnEpisodeGridClick(object sender, RoutedEventArgs e) => SetEpisodeLayout(true);
    private void SetEpisodeLayout(bool grid)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
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
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanControl || sender is not Button { Tag: string id }) return;
        SelectEpisode(id);
    }
    private void SelectEpisode(string itemId)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanControl || !ViewModel.Episodes.Any(episode => episode.ItemId == itemId)) return;
        Run(() => session.SelectEntryAsync(itemId, lifetime.Token));
    }

    private void OnBulletChatChanged(object? sender, EventArgs e) => UpdateBulletChat();
    private void OnBulletChatSettingsChanged(object? sender, EventArgs e)
    {
        if (disposed || presentationFrozen) return;
        BulletChatView.Apply(settings.Current.BulletChat);
        UpdateBulletChat();
    }
    // 拖动滑块时先直接作用到画面，稍后才落盘。
    private void OnBulletChatPreview(object? sender, BulletChatSettings preview)
    {
        if (!disposed && !presentationFrozen) BulletChatView.Apply(preview);
    }
    private void OnBulletChatPanelCompleted(object? sender, EventArgs e) => BulletChatFlyout.Hide();

    private void UpdateBulletChat()
    {
        if (disposed || presentationFrozen) return;
        var state = bulletChat.Current;
        var snapshot = ViewModel.Snapshot;
        // 只显示属于当前条目的弹幕：切集后，上一集的结果在新结果到达前不上屏。
        var current = state.Status == BulletChatStatus.Loaded && state.ItemId == snapshot.Entry?.ItemId;
        BulletChatView.SetComments(current ? state.Comments : []);
        BulletChatView.Sync(snapshot, attached && !transitionActive && !closing);
        BulletChatGlyph.Opacity = settings.Current.BulletChat.Enabled ? 1 : .45;
    }

    private void ToggleBulletChat()
    {
        var enabled = !settings.Current.BulletChat.Enabled;
        ShowKeyHint(enabled ? "弹幕 开" : "弹幕 关");
        Run(() => settings.UpdateAsync(value => value with { BulletChat = value.BulletChat with { Enabled = enabled } }, lifetime.Token));
    }

    private void OnRateOpening(object? sender, object e)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        var current = ViewModel.Snapshot.PlaybackRate;
        RatePanel.SetGroups([new("倍速", [.. Rates.Select(rate => new PlayerChoice(
            rate == 1 ? "正常" : rate.ToString("0.##", CultureInfo.InvariantCulture) + "×", Math.Abs(current - rate) < .001, () => SelectRate(rate)))])]);
    }

    private void OnSubtitlesOpening(object? sender, object e)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        var snapshot = ViewModel.Snapshot;
        SubtitlesPanel.SetGroups(
        [
            new("字幕", [new("关闭字幕", snapshot.SelectedSubtitleTrackId is null, () => SelectTrack(null, subtitle: true, snapshot.EntryGeneration)),
                .. snapshot.SubtitleTracks.Take(32).Select(track => TrackChoice(track, subtitle: true, snapshot))]),
        ]);
    }

    private void OnAudioOpening(object? sender, object e)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        var snapshot = ViewModel.Snapshot;
        AudioPanel.SetGroups([new("音轨", [.. snapshot.AudioTracks.Take(32).Select(track => TrackChoice(track, subtitle: false, snapshot))], "没有可选音轨")]);
    }

    private PlayerChoice TrackChoice(TrackInfo track, bool subtitle, SessionSnapshot snapshot) => new(
        track.Label.Length > 96 ? track.Label[..96] : track.Label,
        (subtitle ? snapshot.SelectedSubtitleTrackId : snapshot.SelectedAudioTrackId) == track.Id,
        () => SelectTrack(track.Id, subtitle, snapshot.EntryGeneration));

    private void OnVideoQualityOpening(object? sender, object e)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanChangeVideoQuality) return;
        var current = ViewModel.VideoQualityMode;
        VideoQualityPanel.SetGroups([new("模式",
        [
            new("标准", current == VideoQualityMode.Standard, () => SelectVideoQuality(VideoQualityMode.Standard)),
            new("清晰", current == VideoQualityMode.Clear, () => SelectVideoQuality(VideoQualityMode.Clear)),
            new("动画", current == VideoQualityMode.Anime, () => SelectVideoQuality(VideoQualityMode.Anime)),
        ])]);
    }

    private void SelectVideoQuality(VideoQualityMode mode)
    {
        if (disposed || presentationFrozen || closing || transitionActive || !ViewModel.CanChangeVideoQuality) return;
        HideMenus();
        Run(async () =>
        {
            ViewModel.IsVideoQualityCommandPending = true;
            try { await session.SetVideoQualityModeAsync(mode, lifetime.Token); }
            finally
            {
                if (!disposed && !presentationFrozen) ViewModel.IsVideoQualityCommandPending = false;
            }
        });
    }

    private void SelectRate(double rate)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        HideMenus();
        Run(() => session.SetRateAsync(rate, lifetime.Token));
    }
    private void SelectTrack(string? trackId, bool subtitle, long? expectedGeneration = null)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        var generation = expectedGeneration ?? ViewModel.Snapshot.EntryGeneration;
        Run(() => subtitle ? session.SelectSubtitleTrackAsync(trackId, generation, lifetime.Token) : session.SelectAudioTrackAsync(trackId, generation, lifetime.Token));
    }

    private bool CanReceiveSubtitleFiles => !disposed && !presentationFrozen && !closing && !transitionActive &&
        ViewModel.Snapshot.EngineKind == EngineKind.Embedded && ViewModel.Snapshot.CanImportSubtitles;

    private void OnSubtitleDragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = CanReceiveSubtitleFiles && args.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.Handled = true;
    }

    private async void OnSubtitleDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (!CanReceiveSubtitleFiles || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        // 在系统异步交付文件列表前绑定账号、条目与操作代际；演示会话连路径也不读取。
        var context = session.BeginSubtitleImport();
        if (context is null) return;
        var deferral = args.GetDeferral();
        var token = lifetime.Token;
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            if (token.IsCancellationRequested) return;
            // 保留原始批次的成员数和顺序。Core 根据原始数量判断“单个无编号文件”，
            // 目录、无路径附件和不支持格式不能在 UI 被删掉后意外变成单文件导入。
            var files = items.Select(item => new LocalSubtitleFile(item is StorageFile file ? file.Path : "")).ToArray();
            if (files.Length > 0) await session.ImportSubtitlesAsync(context, files, token);
        }
        catch (Exception error) when (error is AppException or OperationCanceledException or IOException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            // 导入全程静默；不转交普通播放 Toast，也不输出原始系统异常或源路径。
        }
        finally { deferral.Complete(); }
    }
    private void OnMenuOpened(object? sender, object args)
    {
        if (sender is null) return;
        var menu = sender.As<FlyoutBase>();
        if (disposed || presentationFrozen || closing || transitionActive || !IsLoaded)
        {
            menu.Hide();
            return;
        }
        foreach (var other in openFlyouts.ToArray())
            if (!ReferenceEquals(other, menu)) other.Hide();
        CloseVolume();
        if (!openFlyouts.Contains(menu)) openFlyouts.Add(menu);
        openMenus = openFlyouts.Count;
        Activity();
    }

    private void OnMenuClosed(object? sender, object args)
    {
        if (sender is null) return;
        var menu = sender.As<FlyoutBase>();
        var removed = openFlyouts.Remove(menu);
        openMenus = openFlyouts.Count;
        // 选项的回调捕获着播放层，收起后不留在弹出层里。
        if (ReferenceEquals(menu, RateFlyout)) RatePanel.Clear();
        else if (ReferenceEquals(menu, VideoQualityFlyout)) VideoQualityPanel.Clear();
        else if (ReferenceEquals(menu, SubtitlesFlyout)) SubtitlesPanel.Clear();
        else if (ReferenceEquals(menu, AudioFlyout)) AudioPanel.Clear();
        if (disposed || presentationFrozen || closing || transitionActive || !IsLoaded) return;
        if (removed && openMenus == 0)
        {
            Focus(FocusState.Programmatic);
            lastActivity = Environment.TickCount64;
        }
    }

    private void HideMenus()
    {
        foreach (var menu in openFlyouts.ToArray()) menu.Hide();
        // Hide can animate asynchronously; only Closed removes an open instance.
    }

    private void DisposeMenus()
    {
        foreach (var flyout in panelFlyouts) flyout.Hide();
        openFlyouts.Clear();
        openMenus = 0;
        RatePanel.Clear();
        VideoQualityPanel.Clear();
        SubtitlesPanel.Clear();
        AudioPanel.Clear();
        // 离开可视树时只需收起；播放层冻结后才解除订阅，之后不会再打开。
        if (!presentationFrozen) return;
        foreach (var flyout in panelFlyouts)
        {
            flyout.Opened -= OnMenuOpened;
            flyout.Closed -= OnMenuClosed;
        }
        RateFlyout.Opening -= OnRateOpening;
        VideoQualityFlyout.Opening -= OnVideoQualityOpening;
        SubtitlesFlyout.Opening -= OnSubtitlesOpening;
        AudioFlyout.Opening -= OnAudioOpening;
        panelFlyouts = [];
    }
    private void Run(Func<Task> action, bool cancelSeekOnError = false) => lastCommand = RunAsync(action, cancelSeekOnError);

    private async Task RunAsync(Func<Task> action, bool cancelSeekOnError)
    {
        if (disposed || presentationFrozen || closing || transitionActive) return;
        try { await action(); }
        catch (OperationCanceledException)
        {
            if (cancelSeekOnError && !disposed && !presentationFrozen) ViewModel.CancelSeekPreview();
        }
        catch (AppException ex)
        {
            if (disposed || presentationFrozen) return;
            if (cancelSeekOnError) ViewModel.CancelSeekPreview();
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
    }
    private static bool IsWithin(DependencyObject? element, DependencyObject parent)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }
}
