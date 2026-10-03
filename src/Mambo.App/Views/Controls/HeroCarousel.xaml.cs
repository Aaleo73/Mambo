using System.Numerics;
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

/// <summary>
/// 首页 hero：背景图用 Composition 绘制，渐变遮罩让底部向下多延伸 96px 并渐隐，不叠纯色；
/// 两层交叉淡化，7 秒自动切换。悬停、获得焦点、滚出视口、窗口失焦、离开首页、
/// 悬停在标题栏分页点上或系统关闭动画时暂停。
/// </summary>
public sealed partial class HeroCarousel : UserControl, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);
    private readonly DispatcherQueueTimer timer;
    private readonly List<HeroSlideViewModel> slides = [];
    private ContainerVisual? container;
    private ContainerVisual? images;
    private SpriteVisual? scrim;
    private SpriteVisual? front;
    private SpriteVisual? back;
    private CompositionLinearGradientBrush? mask;
    private CancellationTokenSource? loading;
    private WindowContext? window;
    private WindowContrastObserver? contrastObserver;
    private HeroSlideViewModel? shownSlide;
    private (string Id, ImageSource? Bitmap)? loadedLogo;
    private int infoVersion;
    private int index = -1;
    private bool hovering;
    private bool focused;
    private bool dotsHovering;
    private bool pageActive = true;
    private bool visibleEnough = true;
    private bool disposed;
    private bool highContrast;

    public HeroCarousel()
    {
        InitializeComponent();
        // 原版的文字阴影：标题 0 2px 10px .65，评分信息 0 1px 3px .65，简介 0 1px 4px .6。
        SoftShadow.AttachDrop(TitleShadow, TitleText, 10, 2, 0.65f);
        SoftShadow.AttachDrop(MetaShadow, MetaRow, 3, 1, 0.65f);
        SoftShadow.AttachDrop(OverviewShadow, OverviewText, 4, 1, 0.6f);
        Dots = new HeroDots();
        Dots.DotClicked += (_, i) => GoTo(i);
        Dots.StepRequested += (_, step) => { if (slides.Count > 1) GoTo((index + step + slides.Count) % slides.Count); };
        Dots.HoverChanged += (_, hover) => { dotsHovering = hover; UpdateTimer(); };
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = Interval;
        timer.Tick += OnTimer;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateLayerSize();
    }

    /// <summary>标题栏中间的分页点，幻灯片少于 2 张时不显示。</summary>
    public HeroDots Dots { get; }
    private void OnTimer(DispatcherQueueTimer sender, object args) => GoTo((index + 1) % Math.Max(1, slides.Count));
    public int Count => slides.Count;

    public void Initialize(WindowContext windowContext)
    {
        ArgumentNullException.ThrowIfNull(windowContext);
        if (window is not null) window.ActiveChanged -= OnWindowActiveChanged;
        contrastObserver?.Dispose();
        contrastObserver = null;
        window = windowContext;
        window.ActiveChanged += OnWindowActiveChanged;
        if (IsLoaded) AttachContrastObserver();
    }

    public void SetSlides(IReadOnlyList<HeroSlideViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var limited = items.Take(8).ToList();
        if (limited.Count == slides.Count && limited.Where((slide, i) => !slide.HasSameContent(slides[i])).Any() == false) return;
        var currentId = index >= 0 && index < slides.Count ? slides[index].Id : null;
        slides.Clear();
        slides.AddRange(limited);
        Dots.Build(slides.Count);
        index = -1;
        if (slides.Count > 0) GoTo(Math.Max(0, slides.FindIndex(s => s.Id == currentId)));
        UpdateTimer();
    }

    public void SetPageActive(bool active)
    {
        pageActive = active;
        if (!active) CancelLoading();
        else if (IsLoaded && index >= 0) _ = ShowAsync(index, animate: false);
        UpdateTimer();
    }

    /// <summary>hero 在视口内不足 15% 时暂停。</summary>
    public void SetVisibleFraction(double fraction)
    {
        visibleEnough = fraction >= 0.15;
        UpdateTimer();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        infoVersion++;
        timer.Stop();
        timer.Tick -= OnTimer;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        contrastObserver?.Dispose();
        contrastObserver = null;
        CancelLoading();
        if (window is not null) window.ActiveChanged -= OnWindowActiveChanged;
        if (front?.Brush is CompositionMaskBrush frontMask) ((frontMask.Source as CompositionSurfaceBrush)?.Surface as LoadedImageSurface)?.Dispose();
        if (back?.Brush is CompositionMaskBrush backMask) ((backMask.Source as CompositionSurfaceBrush)?.Surface as LoadedImageSurface)?.Dispose();
        front?.Dispose(); back?.Dispose(); scrim?.Dispose(); container?.Dispose();
    }

    private void CancelLoading()
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        timer.Stop();
        CancelLoading();
        contrastObserver?.Dispose();
        contrastObserver = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed) return;
        AttachContrastObserver();
        if (container is not null) { UpdateTimer(); if (index >= 0) _ = ShowAsync(index, animate: false); return; }
        var compositor = ElementCompositionPreview.GetElementVisual(ArtHost).Compositor;
        mask = HeroArt.CreateEdgeFade(compositor);
        container = compositor.CreateContainerVisual();
        images = compositor.CreateContainerVisual();
        back = CreateLayer(compositor);
        front = CreateLayer(compositor);
        images.Children.InsertAtTop(back);
        images.Children.InsertAtTop(front);
        container.Children.InsertAtTop(images);
        scrim = HeroArt.CreateCopyScrim(compositor, mask);
        container.Children.InsertAtTop(scrim);
        ElementCompositionPreview.SetElementChildVisual(ArtHost, container);
        UpdateLayerSize();
        if (index >= 0) _ = ShowAsync(index, animate: false);
        UpdateTimer();
    }

    private void AttachContrastObserver()
    {
        if (window is null || contrastObserver is not null || disposed) return;
        contrastObserver = new WindowContrastObserver(window, DispatcherQueue, ApplyContrast);
        ApplyContrast(contrastObserver.HighContrast);
    }

    private void ApplyContrast(bool value)
    {
        if (disposed) return;
        var changed = highContrast != value;
        highContrast = value;
        ArtHost.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        LogoImage.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        if (highContrast)
        {
            CancelLoading();
            LogoImage.Source = null;
            TitleText.Visibility = Visibility.Visible;
            ClearImageLayers();
        }
        else if (changed && index >= 0)
        {
            ShowInfo(slides[index], animate: false);
            _ = ShowAsync(index, animate: false);
        }
        UpdateTimer();
    }

    private void ClearImageLayers()
    {
        foreach (var layer in new[] { front, back })
        {
            if (layer is null) continue;
            layer.StopAnimation("Opacity");
            layer.StopAnimation("Scale");
            layer.Opacity = 0;
            if (layer.Brush is not CompositionMaskBrush masked) continue;
            var brush = masked.Source as CompositionSurfaceBrush;
            masked.Source = null;
            if (brush is not null)
            {
                (brush.Surface as LoadedImageSurface)?.Dispose();
                brush.Dispose();
            }
        }
    }

    private SpriteVisual CreateLayer(Compositor compositor)
    {
        var maskBrush = compositor.CreateMaskBrush();
        maskBrush.Mask = mask;
        var layer = compositor.CreateSpriteVisual();
        layer.Brush = maskBrush;
        layer.Opacity = 0;
        return layer;
    }

    private void UpdateLayerSize()
    {
        if (front is null || back is null || scrim is null) return;
        var size = new Vector2((float)ActualWidth, (float)(ActualHeight + HeroArt.FadeExtent));
        front.Size = size;
        back.Size = size;
        scrim.Size = size;
        front.CenterPoint = back.CenterPoint = new Vector3(size / 2, 0);
    }

    private void GoTo(int target)
    {
        if (target < 0 || target >= slides.Count) return;
        var animate = index >= 0;
        index = target;
        Dots.SetActive(index);
        ShowInfo(slides[index], animate);
        _ = ShowAsync(index, animate);
        if (timer.IsRunning) { timer.Stop(); timer.Start(); }
    }

    /// <summary>换幻灯片时，旧文字先上移淡出（180ms），再换成新内容入场。</summary>
    private void ShowInfo(HeroSlideViewModel slide, bool animate)
    {
        var version = ++infoVersion;
        AutomationProperties.SetName(Root, slide.Title);
        var visual = ElementCompositionPreview.GetElementVisual(Info);
        if (!animate || !Motion.AnimationsEnabled || highContrast)
        {
            ApplyInfo(slide);
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            return;
        }
        ElementCompositionPreview.SetIsTranslationEnabled(Info, true);
        var compositor = visual.Compositor;
        var easing = Motion.CreateEasing(compositor, Motion.Exit);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0, easing);
        fade.Duration = Motion.HeroExit;
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1, new Vector3(0, -4, 0), easing);
        move.Duration = Motion.HeroExit;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", move);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (disposed || version != infoVersion) return;
            ApplyInfo(slide);
            Enter();
        };
    }

    private void ApplyInfo(HeroSlideViewModel slide)
    {
        shownSlide = slide;
        TitleText.Text = slide.Title;
        OverviewText.Text = slide.Overview;
        BuildMeta(slide);
        ApplyLogo();
    }

    /// <summary>有 logo 时显示 logo；没有，或 logo 加载失败时显示文字标题。</summary>
    private void ApplyLogo()
    {
        if (shownSlide is not { } slide) return;
        var ready = loadedLogo is { } logo && logo.Id == slide.Id;
        LogoImage.Source = ready && !highContrast ? loadedLogo!.Value.Bitmap : null;
        var failed = ready && loadedLogo!.Value.Bitmap is null;
        TitleText.Visibility = highContrast || slide.Logo is null || failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildMeta(HeroSlideViewModel slide) =>
        MetaArea.Visibility = HeroArt.BuildMeta(MetaRow, slide.RatingText, slide.Year, slide.Genres, slide.OfficialRating)
            ? Visibility.Visible : Visibility.Collapsed;
    private async Task ShowAsync(int slideIndex, bool animate)
    {
        if (disposed || highContrast || !IsLoaded || !pageActive || front is null || back is null || ImageLoader.Current is not { } loader || XamlRoot is null) return;
        CancelLoading();
        var cts = new CancellationTokenSource();
        loading = cts;
        var token = cts.Token;
        var slide = slides[slideIndex];
        var scale = XamlRoot.RasterizationScale;
        try
        {
            if (slide.Logo is { } logo)
            {
                var bitmap = await loader.LoadAsync(logo, 360, scale, ImagePriority.Hero, token);
                if (cts.IsCancellationRequested || highContrast) return;
                loadedLogo = (slide.Id, bitmap);
                // 文字还在退场时先不换；退场结束后 ApplyInfo 会用到这张图。
                if (shownSlide?.Id == slide.Id) ApplyLogo();
            }
            var surface = slide.Backdrop is { } backdrop ? await LoadSurfaceAsync(loader, backdrop, scale, ImagePriority.Hero, token) : null;
            if (token.IsCancellationRequested || highContrast) { surface?.Dispose(); return; }
            Present(surface, animate);
            if (slides.Count > 1 && slides[(slideIndex + 1) % slides.Count].Backdrop is { } next)
                (await loader.FetchStreamAsync(next, PixelWidth(scale), ImagePriority.Prefetch, token))?.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private int PixelWidth(double scale) => (int)Math.Ceiling(Math.Max(ActualWidth, 960) * scale);

    private async Task<LoadedImageSurface?> LoadSurfaceAsync(ImageLoader loader, ImageRef image, double scale, ImagePriority priority, CancellationToken token)
    {
        var width = PixelWidth(scale);
        using var stream = await loader.FetchStreamAsync(image, width, priority, token);
        if (stream is null) return null;
        var completion = new TaskCompletionSource<bool>();
        var surface = LoadedImageSurface.StartLoadFromStream(stream, new Size(width, width * 9 / 16.0));
        void OnCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args) => completion.TrySetResult(args.Status == LoadedImageSourceLoadStatus.Success);
        surface.LoadCompleted += OnCompleted;
        try
        {
            bool ok;
            try { ok = await completion.Task.WaitAsync(token); }
            finally { surface.LoadCompleted -= OnCompleted; }
            token.ThrowIfCancellationRequested();
            if (ok) return surface;
            surface.Dispose();
            return null;
        }
        catch { surface.Dispose(); throw; }
    }

    /// <summary>新图一边淡入一边从 1.04 落回 1（500ms，settle 缓动），旧图只淡出（300ms）。</summary>
    private void Present(LoadedImageSurface? surface, bool animate)
    {
        if (front is null || back is null) return;
        var compositor = back.Compositor;
        var brush = compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.UniformToFill;
        var old = ((CompositionMaskBrush)back.Brush).Source as CompositionSurfaceBrush;
        ((CompositionMaskBrush)back.Brush).Source = brush;
        (old?.Surface as LoadedImageSurface)?.Dispose();
        old?.Dispose();
        (front, back) = (back, front);
        images?.Children.Remove(front);
        images?.Children.InsertAtTop(front);
        if (!animate || !Motion.AnimationsEnabled)
        {
            front.Opacity = 1;
            front.Scale = Vector3.One;
            back.Opacity = 0;
            return;
        }
        var settle = Motion.CreateEasing(compositor, Motion.Settle);
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0, 0);
        fadeIn.InsertKeyFrame(1, 1, settle);
        fadeIn.Duration = Motion.Settling;
        var zoom = compositor.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(0, new Vector3(1.04f, 1.04f, 1));
        zoom.InsertKeyFrame(1, Vector3.One, settle);
        zoom.Duration = Motion.Settling;
        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1, 0, Motion.CreateEasing(compositor, Motion.Fluid));
        fadeOut.Duration = TimeSpan.FromMilliseconds(300);
        front.StartAnimation("Opacity", fadeIn);
        front.StartAnimation("Scale", zoom);
        back.StartAnimation("Opacity", fadeOut);
    }

    /// <summary>入场：整块从下方 8px 淡入（320ms），里面三块再各自上浮 5px（240ms），依次错开 40ms。</summary>
    private void Enter()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Info);
        var compositor = visual.Compositor;
        var easing = Motion.CreateEasing(compositor, Motion.Fluid);
        Animate(Info, 8, Motion.HeroContent, TimeSpan.Zero, easing);
        var delay = TimeSpan.Zero;
        foreach (var block in new FrameworkElement[] { LogoArea, MetaArea, OverviewArea })
        {
            if (block.Visibility != Visibility.Visible) continue;
            Animate(block, 5, Motion.Normal, delay, easing);
            delay += TimeSpan.FromMilliseconds(40);
        }
    }

    private static void Animate(UIElement element, float fromY, TimeSpan duration, TimeSpan delay, CompositionEasingFunction easing)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.Duration = duration;
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, new Vector3(0, fromY, 0));
        offset.InsertKeyFrame(1, Vector3.Zero, easing);
        offset.Duration = duration;
        offset.DelayTime = delay;
        offset.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", offset);
    }

    private void UpdateTimer()
    {
        var run = !disposed && !highContrast && IsLoaded && slides.Count > 1 && !hovering && !focused && !dotsHovering && pageActive && visibleEnough
            && (window?.IsActive ?? true) && Motion.AnimationsEnabled;
        if (run && !timer.IsRunning) timer.Start();
        else if (!run && timer.IsRunning) timer.Stop();
    }

    private void OnWindowActiveChanged(object? sender, EventArgs e) => UpdateTimer();

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        hovering = true;
        UpdateTimer();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        hovering = false;
        UpdateTimer();
    }

    private void OnFocusChanged(object sender, RoutedEventArgs e)
    {
        focused = Root.FocusState != FocusState.Unfocused;
        UpdateTimer();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (index >= 0 && index < slides.Count) CardActions.Current?.Open(slides[index].Id);
    }
}

/// <summary>
/// 标题栏中间的 hero 分页：两侧是上一张、下一张箭头，中间是 6px 圆点，当前项拉长到 22px 并加深（240ms）。
/// </summary>
public sealed partial class HeroDots : StackPanel
{
    private const double DotWidth = 6;
    private const double ActiveWidth = 22;
    private readonly List<(Grid Pill, Border Active)> dots = [];
    private readonly Button previous;
    private readonly Button next;

    public HeroDots()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;
        previous = Arrow("IconChevronLeft", "上一张推荐", -1);
        next = Arrow("IconChevronRight", "下一张推荐", 1);
        PointerEntered += (_, _) => HoverChanged?.Invoke(this, true);
        PointerExited += (_, _) => HoverChanged?.Invoke(this, false);
    }

    public event EventHandler<int>? DotClicked;
    public event EventHandler<int>? StepRequested;
    public event EventHandler<bool>? HoverChanged;

    private Button Arrow(string icon, string name, int step)
    {
        var resources = Application.Current.Resources;
        var button = new Button
        {
            Style = XamlResources.Style(resources, "IconButtonStyle"),
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            IsTabStop = false,
            Content = new LineIcon { Glyph = (string)resources[icon], Width = 13, Height = 13, StrokeWidth = 2.45 },
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => StepRequested?.Invoke(this, step);
        return button;
    }

    public void Build(int count)
    {
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
            var button = new Button { Style = XamlResources.Style(resources, "HeroDotButtonStyle"), Content = pill };
            AutomationProperties.SetName(button, $"切换到第 {i + 1} 个推荐");
            button.Click += (_, _) => DotClicked?.Invoke(this, number);
            Children.Add(button);
            dots.Add((pill, active));
        }
        Children.Add(next);
        Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetActive(int index)
    {
        for (var i = 0; i < dots.Count; i++)
        {
            var (pill, active) = dots[i];
            var on = i == index;
            var width = on ? ActiveWidth : DotWidth;
            if (!Motion.AnimationsEnabled || !pill.IsLoaded)
            {
                active.OpacityTransition = null;
                active.Opacity = on ? 1 : 0;
                pill.Width = width;
                continue;
            }
            active.OpacityTransition = new ScalarTransition { Duration = Motion.Normal };
            active.Opacity = on ? 1 : 0;
            if (pill.Width == width) continue;
            // 宽度参与布局，只能用依赖动画；一共不超过 8 个点，开销可以忽略。
            var frames = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
            frames.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(Motion.Normal),
                Value = width,
                KeySpline = new KeySpline { ControlPoint1 = new Point(0.2, 0.8), ControlPoint2 = new Point(0.2, 1) },
            });
            Storyboard.SetTarget(frames, pill);
            Storyboard.SetTargetProperty(frames, "Width");
            var storyboard = new Storyboard();
            storyboard.Children.Add(frames);
            storyboard.Begin();
        }
    }
}
