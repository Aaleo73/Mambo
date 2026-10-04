using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Mambo.App.Views.Controls;

/// <summary>Two fixed text panels commit with the shared backdrop; only a committed slide is clickable.</summary>
public sealed partial class HeroCarousel : UserControl, IDisposable, IMotionParticipant
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);
    private readonly DispatcherQueueTimer timer;
    private readonly List<HeroSlideViewModel> slides = [];
    private WindowContext? window;
    private BrowseTransitionCoordinator? transitions;
    private WindowContrastObserver? contrastObserver;
    private WindowMotionObserver? motionObserver;
    private XamlRoot? observedRoot;
    private NavEntry? owner;
    private HeroSlideViewModel? displayedSlide;
    private HeroSlideViewModel? requestedSlide;
    private HeroSlideViewModel? restoredSlide;
    private HeroInfoPanel? displayedPanel;
    private CancellationTokenSource? loading;
    private CompositionScopedBatch? foregroundBatch;
    private TaskCompletionSource<bool>? foregroundCompletion;
    private int generation;
    private int requestedIndex = -1;
    private int requestedWidth;
    private long timerStartedAt;
    private bool requestPending;
    private bool hovering;
    private bool focused;
    private bool dotsHovering;
    private bool dotsFocused;
    private bool pageActive;
    private bool visibleEnough = true;
    private bool highContrast;
    private bool disposed;

    public HeroCarousel()
    {
        InitializeComponent();
        FirstInfo.Opacity = SecondInfo.Opacity = 0;
        SetPanelAccessible(FirstInfo, false);
        SetPanelAccessible(SecondInfo, false);
        Dots = new HeroDots();
        Dots.DotClicked += OnDotClicked;
        Dots.StepRequested += OnStepRequested;
        Dots.HoverChanged += OnDotsHoverChanged;
        Dots.FocusChanged += OnDotsFocusChanged;
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = Interval;
        timer.IsRepeating = false;
        timer.Tick += OnTimer;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public HeroDots Dots { get; }
    public int Count => Math.Max(slides.Count, restoredSlide is null ? 0 : 1);
    internal HeroSlideViewModel? DisplayedSlide => displayedSlide;
    internal HeroSlideViewModel? RequestedSlide => requestedSlide;
    internal int PresentedInfoCount => (FirstInfo.Visibility == Visibility.Visible && FirstInfo.Slide is not null ? 1 : 0)
        + (SecondInfo.Visibility == Visibility.Visible && SecondInfo.Slide is not null ? 1 : 0);
    internal bool IsTimerRunning => timer.IsRunning;
    internal bool IsRequestPending => requestPending;
    internal Task PendingTransition { get; private set; } = Task.CompletedTask;
    internal Task PendingForeground => foregroundCompletion?.Task ?? Task.CompletedTask;

    public void Initialize(WindowContext windowContext, BrowseTransitionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(windowContext);
        ArgumentNullException.ThrowIfNull(coordinator);
        if (window is not null) window.ActiveChanged -= OnWindowActiveChanged;
        if (transitions is not null) transitions.NavigationCompleted -= OnNavigationCompleted;
        contrastObserver?.Dispose();
        motionObserver?.Dispose();
        window = windowContext;
        transitions = coordinator;
        transitions.NavigationCompleted += OnNavigationCompleted;
        window.ActiveChanged += OnWindowActiveChanged;
        contrastObserver = new WindowContrastObserver(window, DispatcherQueue, ApplyContrast);
        motionObserver = new WindowMotionObserver(window, DispatcherQueue, ApplyMotion);
        ApplyContrast(contrastObserver.HighContrast);
        ApplyMotion(motionObserver.AnimationsEnabled);
    }

    internal void SetOwner(NavEntry entry)
    {
        CancelRequest();
        owner = entry;
        restoredSlide = transitions?.RestoredHero(entry);
        if (restoredSlide is not null) requestedSlide = restoredSlide;
    }

    public void SetSlides(IReadOnlyList<HeroSlideViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var count = Math.Min(8, items.Count);
        var unchanged = count == slides.Count;
        for (var i = 0; unchanged && i < count; i++) unchanged = items[i].HasSameContent(slides[i]);
        if (unchanged) return;
        slides.Clear();
        for (var i = 0; i < count; i++) slides.Add(items[i]);
        Dots.Build(count);
        Dots.SetActive(IndexOf(displayedSlide), animate: false);
        if (count == 0 && restoredSlide is null)
        {
            CancelRequest();
            displayedSlide = requestedSlide = null;
            displayedPanel = null;
            SettleForeground();
            FirstInfo.Clear();
            SecondInfo.Clear();
            Root.IsHitTestVisible = false;
            AutomationProperties.SetName(Root, "首页推荐");
            if (pageActive && owner is not null) RequestEmptyBackdrop(owner);
            return;
        }
        var next = restoredSlide ?? FindById(requestedSlide?.Id ?? displayedSlide?.Id) ?? slides.FirstOrDefault();
        if (next is not null) Request(next, animate: false);
        UpdateTimer();
    }

    public void SetPageActive(bool active)
    {
        pageActive = active;
        Root.IsHitTestVisible = active && displayedSlide is not null;
        Dots.SetInteractionActive(active);
        if (!active)
        {
            CancelRequest();
            requestedSlide = displayedSlide;
            SettleForeground();
            transitions?.SettleBackdrop();
            Dots.Settle();
            focused = dotsFocused = false;
        }
        else Resume();
        UpdateTimer();
    }

    public void SetVisibleFraction(double fraction)
    {
        visibleEnough = fraction >= 0.15;
        UpdateTimer();
    }

    private HeroSlideViewModel? FindById(string? id)
    {
        foreach (var slide in slides) if (slide.Id == id) return slide;
        return null;
    }
    private int IndexOf(HeroSlideViewModel? slide)
    {
        if (slide is not null)
            for (var i = 0; i < slides.Count; i++) if (slides[i].Id == slide.Id) return i;
        return -1;
    }
    private int PixelWidth => (int)Math.Ceiling(Math.Max(ActualWidth, 960) * (XamlRoot?.RasterizationScale ?? 1));
    private bool CanPresent => !disposed && pageActive && IsLoaded && owner is not null && transitions is not null && (window?.IsActive ?? true);
    private bool CanAnimate => CanPresent && !highContrast && (motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled)
        && Motion.IsActive(this) && !Motion.IsEntranceSuppressed(this);

    private void Resume()
    {
        if (!CanPresent) return;
        var slide = restoredSlide ?? FindById(requestedSlide?.Id ?? displayedSlide?.Id) ?? displayedSlide ?? slides.FirstOrDefault();
        if (slide is not null) Request(slide, animate: false, force: true);
    }

    private void Select(int index)
    {
        if (!pageActive || index < 0 || index >= slides.Count) return;
        restoredSlide = null;
        Request(slides[index], animate: true);
    }

    private void Request(HeroSlideViewModel slide, bool animate, bool force = false)
    {
        if (disposed) return;
        if (!force && requestedSlide is { } requested && slide.HasSameContent(requested)
            && (requestPending || displayedSlide is { } displayed && slide.HasSameContent(displayed))) return;
        requestedSlide = slide;
        requestedIndex = IndexOf(slide);
        if (!CanPresent) return;
        CancelRequest();
        requestedWidth = PixelWidth;
        var cancellation = new CancellationTokenSource();
        loading = cancellation;
        var request = new SlideRequest(slide, owner!, generation, animate && CanAnimate, requestedWidth, cancellation.Token);
        requestPending = true;
        if (displayedSlide is null)
        {
            FirstInfo.Show(slide, null, highContrast);
            FirstInfo.Visibility = Visibility.Visible;
            FirstInfo.Opacity = 1;
            ElementCompositionPreview.GetElementVisual(FirstInfo).Opacity = 1;
            SecondInfo.Visibility = Visibility.Collapsed;
        }
        timer.Stop();
        var logo = LoadLogoAsync(request);
        var presentation = PresentAsync(request);
        PendingTransition = presentation;
        ObserveRequest(Task.WhenAll(logo, presentation), request, cancellation);
    }

    private bool IsCurrent(SlideRequest request) => !disposed && pageActive && request.Generation == generation
        && ReferenceEquals(request.Owner, owner) && !request.Token.IsCancellationRequested;

    private async Task LoadLogoAsync(SlideRequest request)
    {
        if (highContrast || request.Slide.Logo is not { } logo || ImageLoader.Current is not { } loader) return;
        request.Logo = await loader.LoadAsync(logo, 360, XamlRoot?.RasterizationScale ?? 1, ImagePriority.Hero, request.Token);
        if (IsCurrent(request) && !highContrast && displayedSlide?.HasSameContent(request.Slide) == true)
            displayedPanel?.SetLogo(request.Logo);
    }

    private async Task PresentAsync(SlideRequest request)
    {
        PreparedBackdrop? prepared = null;
        try
        {
            prepared = await transitions!.PrepareBackdropAsync(request.Owner, request.Slide.Backdrop, request.DecodeWidth, request.Token);
            if (prepared is null) return;
            if (!IsCurrent(request)) return;
            // The background also gates third-image commits. This gate handles distinct titles sharing one image.
            if (foregroundBatch is not null && FindPanel(request.Slide) is null)
            {
                await PendingForeground.WaitAsync(request.Token);
                if (!IsCurrent(request)) return;
            }
            var adopted = prepared;
            prepared = null;
            var committed = await transitions.CommitBackdropAsync(request.Owner, adopted, request.Animate && CanAnimate, () =>
            {
                if (!IsCurrent(request)) return;
                CommitInfo(request);
            });
            if (!committed || !IsCurrent(request)) return;
            await PendingForeground.WaitAsync(request.Token);
        }
        finally
        {
            prepared?.Dispose();
            if (IsCurrent(request))
            {
                requestPending = false;
                UpdateTimer();
            }
        }
    }

    private async void ObserveRequest(Task task, SlideRequest request, CancellationTokenSource cancellation)
    {
        try { await task; }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (IsCurrent(request)) transitions?.ReportFailure(error);
        }
        finally
        {
            if (ReferenceEquals(loading, cancellation)) loading = null;
            cancellation.Dispose();
        }
    }

    private async void RequestEmptyBackdrop(NavEntry entry)
    {
        var requestGeneration = generation;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTransition = completion.Task;
        try
        {
            var prepared = await transitions!.PrepareBackdropAsync(entry, null, PixelWidth, CancellationToken.None);
            if (prepared is null) return;
            if (disposed || !pageActive || requestGeneration != generation) { prepared.Dispose(); return; }
            await transitions.CommitBackdropAsync(entry, prepared, animate: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!disposed && pageActive && requestGeneration == generation) transitions?.ReportFailure(error);
        }
        finally { completion.TrySetResult(true); }
    }

    private HeroInfoPanel? FindPanel(HeroSlideViewModel slide)
    {
        if (FirstInfo.Slide?.HasSameContent(slide) == true) return FirstInfo;
        if (SecondInfo.Slide?.HasSameContent(slide) == true) return SecondInfo;
        return null;
    }

    private void CommitInfo(SlideRequest request)
    {
        var panel = FindPanel(request.Slide);
        var previous = displayedPanel;
        var same = displayedSlide?.HasSameContent(request.Slide) == true;
        panel ??= ReferenceEquals(displayedPanel, FirstInfo) ? SecondInfo : FirstInfo;
        if (panel.Slide?.HasSameContent(request.Slide) != true) panel.Show(request.Slide, request.Logo, highContrast);
        else if (request.Logo is not null) panel.SetLogo(request.Logo);
        displayedSlide = request.Slide;
        displayedPanel = panel;
        transitions!.CommitHero(request.Owner, request.Slide);
        AutomationProperties.SetName(Root, request.Slide.Title);
        Root.IsHitTestVisible = pageActive;
        Dots.SetActive(IndexOf(displayedSlide), request.Animate && CanAnimate);
        SetPanelAccessible(FirstInfo, ReferenceEquals(panel, FirstInfo));
        SetPanelAccessible(SecondInfo, ReferenceEquals(panel, SecondInfo));
        if (same) return;
        if (!request.Animate || !CanAnimate || previous is null)
        {
            SettleForeground();
            return;
        }
        var wasVisible = panel.Visibility == Visibility.Visible;
        ReleaseForegroundBatch();
        panel.Visibility = Visibility.Visible;
        previous.Visibility = Visibility.Visible;
        var incoming = ElementCompositionPreview.GetElementVisual(panel);
        var outgoing = ElementCompositionPreview.GetElementVisual(previous);
        if (!wasVisible) incoming.Opacity = 0;
        Motion.SetEntranceSuppressed(InfoHost, true);
        var compositor = incoming.Compositor;
        foregroundBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foregroundBatch.Completed += OnForegroundCompleted;
        foregroundCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var easing = Motion.CreateEasing(compositor, Motion.EaseOut);
        using var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = Motion.Content;
        fadeIn.InsertKeyFrame(1, 1, easing);
        using var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = Motion.Content;
        fadeOut.InsertKeyFrame(1, 0, easing);
        incoming.StartAnimation("Opacity", fadeIn);
        outgoing.StartAnimation("Opacity", fadeOut);
        foregroundBatch.End();
    }

    private static void SetPanelAccessible(HeroInfoPanel panel, bool active) => panel.SetPresented(active);

    private void OnForegroundCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (ReferenceEquals(sender, foregroundBatch)) SettleForeground();
    }

    private void ReleaseForegroundBatch()
    {
        if (foregroundBatch is not null)
        {
            foregroundBatch.Completed -= OnForegroundCompleted;
            foregroundBatch.Dispose();
            foregroundBatch = null;
        }
        var completion = foregroundCompletion;
        foregroundCompletion = null;
        completion?.TrySetResult(true);
    }

    private void SettleForeground()
    {
        ReleaseForegroundBatch();
        SettlePanel(FirstInfo);
        SettlePanel(SecondInfo);
        Motion.SetEntranceSuppressed(InfoHost, false);
    }

    private void SettlePanel(HeroInfoPanel panel)
    {
        var visual = ElementCompositionPreview.GetElementVisual(panel);
        visual.StopAnimation("Opacity");
        var active = ReferenceEquals(panel, displayedPanel);
        panel.Opacity = 1;
        visual.Opacity = active ? 1 : 0;
        panel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        SetPanelAccessible(panel, active);
    }

    void IMotionParticipant.SettleMotion() { SettleForeground(); Dots.Settle(); UpdateTimer(); }

    private void CancelRequest()
    {
        generation++;
        loading?.Cancel();
        loading = null;
        requestPending = false;
        PendingTransition = Task.CompletedTask;
        timer.Stop();
    }

    private void ApplyContrast(bool value)
    {
        if (disposed) return;
        var changed = value != highContrast;
        highContrast = value;
        FirstInfo.SetContrast(value);
        SecondInfo.SetContrast(value);
        Dots.SetMotionEnabled(!value && (motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled));
        if (value)
        {
            CancelRequest();
            SettleForeground();
            Dots.Settle();
        }
        if (changed) Resume();
        UpdateTimer();
    }

    private void ApplyMotion(bool enabled)
    {
        Dots.SetMotionEnabled(enabled && !highContrast);
        if (!enabled) SettleForeground();
        UpdateTimer();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (observedRoot is not null) observedRoot.Changed -= OnXamlRootChanged;
        observedRoot = XamlRoot;
        if (observedRoot is not null) observedRoot.Changed += OnXamlRootChanged;
        Resume();
        UpdateTimer();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (observedRoot is not null) observedRoot.Changed -= OnXamlRootChanged;
        observedRoot = null;
        CancelRequest();
        SettleForeground();
        Dots.Settle();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpgradeResolution();
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpgradeResolution();
    private void UpgradeResolution()
    {
        if (CanPresent && !highContrast && requestedSlide is { } slide && PixelWidth > requestedWidth)
            Request(slide, animate: false, force: true);
    }
    private void OnNavigationCompleted(NavEntry entry)
    {
        if (ReferenceEquals(entry, owner)) UpdateTimer();
    }
    private void OnWindowActiveChanged(object? sender, EventArgs e)
    {
        if (window?.IsActive == false)
        {
            CancelRequest();
            SettleForeground();
            Dots.Settle();
        }
        else Resume();
        UpdateTimer();
    }

    private bool ShouldRunTimer => CanAnimate && (slides.Count > 1 || slides.Count > 0 && restoredSlide is not null && IndexOf(displayedSlide) < 0)
        && !requestPending && foregroundBatch is null && !(transitions?.Backdrop?.IsTransitioning ?? false)
        && !hovering && !focused && !dotsHovering && !dotsFocused && visibleEnough;

    private void UpdateTimer()
    {
        if (ShouldRunTimer)
        {
            if (timer.IsRunning) return;
            timerStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            timer.Interval = Interval;
            timer.Start();
        }
        else timer.Stop();
    }
    private void OnTimer(DispatcherQueueTimer sender, object args)
    {
        timer.Stop();
        if (!ShouldRunTimer) return;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(timerStartedAt);
        if (elapsed < Interval)
        {
            timer.Interval = Interval - elapsed;
            timer.Start();
            return;
        }
        var current = IndexOf(displayedSlide);
        Select((current + 1) % slides.Count);
    }
    private void OnDotClicked(object? sender, int index) => Select(index);
    private void OnStepRequested(object? sender, int step)
    {
        if (slides.Count > 1) Select(((requestPending ? requestedIndex : IndexOf(displayedSlide)) + step + slides.Count) % slides.Count);
    }
    private void OnDotsHoverChanged(object? sender, bool hover) { dotsHovering = hover; UpdateTimer(); }
    private void OnDotsFocusChanged(object? sender, bool focus) { dotsFocused = focus; UpdateTimer(); }
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) { hovering = true; UpdateTimer(); }
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) { hovering = false; UpdateTimer(); }
    private void OnFocusChanged(object sender, RoutedEventArgs e) { focused = Root.FocusState != FocusState.Unfocused; UpdateTimer(); }
    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (!disposed && pageActive && displayedSlide is { } slide) transitions?.OpenHero(slide);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelRequest();
        SettleForeground();
        timer.Tick -= OnTimer;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        if (window is not null) window.ActiveChanged -= OnWindowActiveChanged;
        if (observedRoot is not null) observedRoot.Changed -= OnXamlRootChanged;
        observedRoot = null;
        if (transitions is not null) transitions.NavigationCompleted -= OnNavigationCompleted;
        contrastObserver?.Dispose();
        motionObserver?.Dispose();
        Dots.DotClicked -= OnDotClicked;
        Dots.StepRequested -= OnStepRequested;
        Dots.HoverChanged -= OnDotsHoverChanged;
        Dots.FocusChanged -= OnDotsFocusChanged;
        Dots.Dispose();
        FirstInfo.Clear();
        SecondInfo.Clear();
        displayedSlide = requestedSlide = restoredSlide = null;
        owner = null;
        transitions = null;
        window = null;
    }

    private sealed class SlideRequest(HeroSlideViewModel slide, NavEntry owner, int generation, bool animate, int decodeWidth, CancellationToken token)
    {
        internal HeroSlideViewModel Slide { get; } = slide;
        internal NavEntry Owner { get; } = owner;
        internal int Generation { get; } = generation;
        internal CancellationToken Token { get; } = token;
        internal bool Animate { get; } = animate;
        internal int DecodeWidth { get; } = decodeWidth;
        internal ImageSource? Logo { get; set; }
    }
}

/// <summary>Title-bar recommendation controls; focus, like hover, pauses a complete seven-second cycle.</summary>
public sealed partial class HeroDots : StackPanel, IDisposable
{
    private const double DotWidth = 6;
    private const double ActiveWidth = 22;
    private readonly List<(Grid Pill, Border Active, Button Button)> dots = [];
    private readonly List<Storyboard> animations = [];
    private readonly Button previous;
    private readonly Button next;
    private int activeIndex = -1;
    private int focusGeneration;
    private bool motionEnabled = true;
    private bool interactionActive = true;
    private bool disposed;

    public HeroDots()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;
        previous = Arrow("IconChevronLeft", "上一张推荐", -1);
        next = Arrow("IconChevronRight", "下一张推荐", 1);
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        GotFocus += OnFocusChanged;
        LostFocus += OnFocusChanged;
        Unloaded += OnUnloaded;
    }

    public event EventHandler<int>? DotClicked;
    public event EventHandler<int>? StepRequested;
    public event EventHandler<bool>? HoverChanged;
    public event EventHandler<bool>? FocusChanged;

    private Button Arrow(string icon, string name, int step)
    {
        var resources = Application.Current.Resources;
        var button = new Button
        {
            Style = XamlResources.Style(resources, "IconButtonStyle"),
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            Content = new LineIcon { Glyph = (string)resources[icon], Width = 13, Height = 13, StrokeWidth = 2.45 },
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { if (interactionActive) StepRequested?.Invoke(this, step); };
        return button;
    }

    public void Build(int count)
    {
        Settle();
        var resources = Application.Current.Resources;
        Children.Clear();
        dots.Clear();
        Children.Add(previous);
        for (var i = 0; i < count; i++)
        {
            var number = i;
            var active = new Border { Style = XamlResources.Style(resources, "HeroDotActiveStyle") };
            var pill = new Grid { Width = DotWidth, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            pill.Children.Add(new Border { Style = XamlResources.Style(resources, "HeroDotStyle") });
            pill.Children.Add(active);
            var button = new Button { Style = XamlResources.Style(resources, "HeroDotButtonStyle"), Content = pill, IsTabStop = interactionActive };
            AutomationProperties.SetName(button, $"切换到第 {i + 1} 个推荐");
            button.Click += (_, _) => { if (interactionActive) DotClicked?.Invoke(this, number); };
            Children.Add(button);
            dots.Add((pill, active, button));
        }
        Children.Add(next);
        Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SetActive(activeIndex, animate: false);
    }

    public void SetActive(int index, bool animate = true)
    {
        activeIndex = index;
        // Preserve each dependent animation's current width before detaching its clock.
        Span<double> widths = stackalloc double[dots.Count];
        for (var i = 0; i < dots.Count; i++) widths[i] = dots[i].Pill.ActualWidth > 0 ? dots[i].Pill.ActualWidth : dots[i].Pill.Width;
        StopAnimations();
        for (var i = 0; i < dots.Count; i++)
        {
            var (pill, active, _) = dots[i];
            var on = i == index;
            var width = on ? ActiveWidth : DotWidth;
            active.OpacityTransition = animate && motionEnabled && Motion.AnimationsEnabled && pill.IsLoaded
                ? new ScalarTransition { Duration = Motion.Feedback } : null;
            active.Opacity = on ? 1 : 0;
            if (!animate || !motionEnabled || !Motion.AnimationsEnabled || !pill.IsLoaded)
            {
                pill.Width = width;
                continue;
            }
            pill.Width = widths[i];
            var frames = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
            frames.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(Motion.Feedback), Value = width,
                KeySpline = new KeySpline { ControlPoint1 = new Point(0.2, 0.8), ControlPoint2 = new Point(0.2, 1) },
            });
            Storyboard.SetTarget(frames, pill);
            Storyboard.SetTargetProperty(frames, "Width");
            var storyboard = new Storyboard();
            storyboard.Children.Add(frames);
            animations.Add(storyboard);
            storyboard.Begin();
        }
    }

    internal void SetMotionEnabled(bool enabled) { motionEnabled = enabled; if (!enabled) Settle(); }
    internal void SetInteractionActive(bool active)
    {
        interactionActive = active;
        IsHitTestVisible = active;
        previous.IsTabStop = next.IsTabStop = active;
        foreach (var (_, _, button) in dots) button.IsTabStop = active;
        if (!active) { focusGeneration++; Settle(); }
    }
    private void StopAnimations()
    {
        foreach (var animation in animations) animation.Stop();
        animations.Clear();
    }
    internal void Settle()
    {
        StopAnimations();
        for (var i = 0; i < dots.Count; i++)
        {
            var (pill, active, _) = dots[i];
            active.OpacityTransition = null;
            active.Opacity = i == activeIndex ? 1 : 0;
            pill.Width = i == activeIndex ? ActiveWidth : DotWidth;
        }
    }
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => HoverChanged?.Invoke(this, true);
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => HoverChanged?.Invoke(this, false);
    private void OnFocusChanged(object sender, RoutedEventArgs e)
    {
        var generation = ++focusGeneration;
        if (e.OriginalSource is Control { FocusState: not FocusState.Unfocused }) FocusChanged?.Invoke(this, true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (disposed || generation != focusGeneration) return;
            DependencyObject? current = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (current is not null && !ReferenceEquals(current, this)) current = VisualTreeHelper.GetParent(current);
            FocusChanged?.Invoke(this, current is not null);
        });
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) { focusGeneration++; Settle(); FocusChanged?.Invoke(this, false); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        focusGeneration++;
        Settle();
        PointerEntered -= OnPointerEntered;
        PointerExited -= OnPointerExited;
        GotFocus -= OnFocusChanged;
        LostFocus -= OnFocusChanged;
        Unloaded -= OnUnloaded;
        Children.Clear();
        dots.Clear();
    }
}
