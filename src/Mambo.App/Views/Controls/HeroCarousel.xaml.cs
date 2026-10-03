using System.Numerics;
using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 首页 hero：背景图用 Composition 绘制，渐变遮罩让底部向下多延伸 96px 并渐隐，不叠纯色；
/// 两层交叉淡化，7 秒自动切换。悬停、获得焦点、滚出视口、窗口失焦、离开首页、
/// 悬停在标题栏分页点上或系统关闭动画时暂停。
/// </summary>
public sealed partial class HeroCarousel : UserControl, IDisposable
{
    public const double FadeExtent = 96;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);
    private readonly DispatcherQueueTimer timer;
    private readonly List<HeroSlideViewModel> slides = [];
    private ContainerVisual? container;
    private ContainerVisual? images;
    private SpriteVisual? scrimLinear;
    private SpriteVisual? scrimRadial;
    private SpriteVisual? front;
    private SpriteVisual? back;
    private CompositionLinearGradientBrush? mask;
    private CancellationTokenSource? loading;
    private WindowContext? window;
    private WindowContrastObserver? contrastObserver;
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
        Dots = new HeroDots();
        Dots.DotClicked += (_, i) => GoTo(i);
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
        front?.Dispose(); back?.Dispose(); container?.Dispose();
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
        var host = ElementCompositionPreview.GetElementVisual(ArtHost);
        var compositor = host.Compositor;
        mask = compositor.CreateLinearGradientBrush();
        mask.StartPoint = new Vector2(0, 0);
        mask.EndPoint = new Vector2(0, 1);
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0f, Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0.56f, Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0.76f, Windows.UI.Color.FromArgb(140, 255, 255, 255)));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
        container = compositor.CreateContainerVisual();
        images = compositor.CreateContainerVisual();
        back = CreateLayer(compositor);
        front = CreateLayer(compositor);
        images.Children.InsertAtTop(back);
        images.Children.InsertAtTop(front);
        container.Children.InsertAtTop(images);
        // 左侧压暗让文字可读；与图片一样向下渐隐，避免在 hero 底边留下一道硬边。
        var scrimMask = compositor.CreateLinearGradientBrush();
        scrimMask.StartPoint = new Vector2(0, 0);
        scrimMask.EndPoint = new Vector2(0, 1);
        scrimMask.ColorStops.Add(compositor.CreateColorGradientStop(0f, Colors.White));
        scrimMask.ColorStops.Add(compositor.CreateColorGradientStop(0.56f, Colors.White));
        scrimMask.ColorStops.Add(compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
        var linear = compositor.CreateLinearGradientBrush();
        linear.StartPoint = new Vector2(0, 0);
        linear.EndPoint = new Vector2(1, 0);
        linear.ColorStops.Add(compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(158, 0, 0, 0)));
        linear.ColorStops.Add(compositor.CreateColorGradientStop(0.34f, Windows.UI.Color.FromArgb(87, 0, 0, 0)));
        linear.ColorStops.Add(compositor.CreateColorGradientStop(0.62f, Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        var radial = compositor.CreateRadialGradientBrush();
        radial.EllipseCenter = new Vector2(0.1f, 0.78f);
        radial.EllipseRadius = new Vector2(0.7f, 0.6f);
        radial.ColorStops.Add(compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(115, 0, 0, 0)));
        radial.ColorStops.Add(compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        scrimLinear = CreateScrim(compositor, linear, scrimMask);
        scrimRadial = CreateScrim(compositor, radial, scrimMask);
        container.Children.InsertAtTop(scrimLinear);
        container.Children.InsertAtTop(scrimRadial);
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

    private static SpriteVisual CreateScrim(Compositor compositor, CompositionBrush source, CompositionBrush mask)
    {
        var brush = compositor.CreateMaskBrush();
        brush.Source = source;
        brush.Mask = mask;
        var visual = compositor.CreateSpriteVisual();
        visual.Brush = brush;
        return visual;
    }

    private void UpdateLayerSize()
    {
        if (front is null || back is null || scrimLinear is null || scrimRadial is null) return;
        var size = new Vector2((float)ActualWidth, (float)(ActualHeight + FadeExtent));
        front.Size = size;
        back.Size = size;
        scrimLinear.Size = size;
        scrimRadial.Size = size;
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

    private void ShowInfo(HeroSlideViewModel slide, bool animate)
    {
        AutomationProperties.SetName(Root, slide.Title);
        TitleText.Text = slide.Title;
        TitleText.Visibility = highContrast || slide.Logo is null ? Visibility.Visible : Visibility.Collapsed;
        LogoImage.Source = null;
        OverviewText.Text = slide.Overview;
        OverviewText.Visibility = slide.Overview.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildMeta(slide);
        if (animate) Rise();
    }

    private void BuildMeta(HeroSlideViewModel slide)
    {
        MetaRow.Children.Clear();
        void Separator()
        {
            if (MetaRow.Children.Count > 0)
                MetaRow.Children.Add(new Border { Style = XamlResources.Style(Application.Current.Resources, "HeroSeparatorStyle") });
        }
        if (slide.RatingText.Length > 0)
        {
            var star = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            star.Children.Add(new FontIcon { Glyph = "", Style = XamlResources.Style(Application.Current.Resources, "HeroRatingIconStyle") });
            star.Children.Add(MetaText(slide.RatingText));
            MetaRow.Children.Add(star);
        }
        foreach (var text in new[] { slide.Year, slide.Genres })
        {
            if (text.Length == 0) continue;
            Separator();
            MetaRow.Children.Add(MetaText(text));
        }
        if (slide.OfficialRating.Length > 0)
        {
            Separator();
            MetaRow.Children.Add(new Border
            {
                Style = XamlResources.Style(Resources, "ContrastHeroBadgeStyle"),
                Child = new TextBlock { Text = slide.OfficialRating, Style = XamlResources.Style(Application.Current.Resources, "HeroBadgeTextStyle") },
            });
        }
    }

    private static TextBlock MetaText(string text) => new() { Text = text, Style = XamlResources.Style(Application.Current.Resources, "HeroMetaTextStyle") };

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
                var bitmap = await loader.LoadAsync(logo, 400, scale, ImagePriority.Hero, token);
                if (cts.IsCancellationRequested || highContrast) return;
                LogoImage.Source = bitmap;
                TitleText.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
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

    private void Present(LoadedImageSurface? surface, bool animate)
    {
        if (front is null || back is null) return;
        var compositor = back.Compositor;
        var brush = compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.UniformToFill;
        brush.VerticalAlignmentRatio = 0.25f;
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
        fadeIn.Duration = Motion.Hero;
        var zoom = compositor.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(0, new Vector3(1.04f, 1.04f, 1));
        zoom.InsertKeyFrame(1, Vector3.One, settle);
        zoom.Duration = TimeSpan.FromMilliseconds(500);
        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1, 0, settle);
        fadeOut.Duration = Motion.Hero;
        front.StartAnimation("Opacity", fadeIn);
        front.StartAnimation("Scale", zoom);
        back.StartAnimation("Opacity", fadeOut);
    }

    /// <summary>文字区各行依次上浮淡入，间隔 40ms。</summary>
    private void Rise()
    {
        if (!Motion.AnimationsEnabled || highContrast) return;
        var delay = TimeSpan.Zero;
        foreach (var child in Info.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            ElementCompositionPreview.SetIsTranslationEnabled(child, true);
            var visual = ElementCompositionPreview.GetElementVisual(child);
            var compositor = visual.Compositor;
            var easing = Motion.CreateEasing(compositor, Motion.Settle);
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(1, 1, easing);
            opacity.Duration = Motion.Hero;
            opacity.DelayTime = delay;
            opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.InsertKeyFrame(0, new Vector3(0, 10, 0));
            offset.InsertKeyFrame(1, Vector3.Zero, easing);
            offset.Duration = Motion.Hero;
            offset.DelayTime = delay;
            offset.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Opacity", opacity);
            visual.StartAnimation("Translation", offset);
            delay += TimeSpan.FromMilliseconds(40);
        }
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

/// <summary>标题栏中间的 hero 分页点：6px 圆点，当前项拉长到 22px。</summary>
public sealed partial class HeroDots : StackPanel
{
    public HeroDots()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;
        PointerEntered += (_, _) => HoverChanged?.Invoke(this, true);
        PointerExited += (_, _) => HoverChanged?.Invoke(this, false);
    }

    public event EventHandler<int>? DotClicked;
    public event EventHandler<bool>? HoverChanged;

    public void Build(int count)
    {
        Children.Clear();
        for (var i = 0; i < count; i++)
        {
            var number = i;
            var button = new Button
            {
                Style = XamlResources.Style(Application.Current.Resources, "HeroDotButtonStyle"),
                Content = new Border { Style = XamlResources.Style(Application.Current.Resources, "HeroDotStyle") },
            };
            AutomationProperties.SetName(button, $"第 {i + 1} 张");
            button.Click += (_, _) => DotClicked?.Invoke(this, number);
            Children.Add(button);
        }
        Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetActive(int index)
    {
        for (var i = 0; i < Children.Count; i++)
            if (Children[i] is Button { Content: Border dot })
                dot.Style = XamlResources.Style(Application.Current.Resources, i == index ? "HeroDotActiveStyle" : "HeroDotStyle");
    }
}
