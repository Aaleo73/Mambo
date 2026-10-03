using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Mambo.App.Themes;

/// <summary>
/// 原版用的两类阴影：
/// <list type="bullet">
/// <item>控件投影（<c>SoftShadow.Kind</c>）：只画在圆角矩形外侧，相当于 CSS 的 box-shadow。
/// 半透明的按钮底下不会被阴影压暗。宿主是模板里排在表面之前的一个空元素。</item>
/// <item>轮廓阴影（<see cref="AttachDrop"/>）：按元素实际画出来的形状投影，相当于 CSS 的 drop-shadow，用在 hero 的文字上。</item>
/// </list>
/// 系统关闭透明效果或处于高对比度时不画阴影。
/// </summary>
public static partial class SoftShadow
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(string), typeof(SoftShadow), new PropertyMetadata(null, OnKindChanged));

    public static string? GetKind(DependencyObject element) => (string?)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element, string? value) => element.SetValue(KindProperty, value);

    /// <summary>CSS 的 blur 是 2σ，Composition 的 BlurRadius 是 3σ。</summary>
    private const float CssBlurToRadius = 1.5f;

    private readonly record struct Spec(float Blur, float OffsetY, Color Light, Color Dark);

    private static Spec? Find(string? kind) => kind switch
    {
        // 0 8px 22px rgba(15,23,42,.08)：按钮、计数胶囊、排序胶囊
        "Control" => new Spec(22, 8, Color.FromArgb(20, 15, 23, 42), Color.FromArgb(71, 0, 0, 0)),
        // 0 5px 12px rgba(15,23,42,.08)：选中的筛选片、季胶囊
        "Chip" => new Spec(12, 5, Color.FromArgb(20, 15, 23, 42), Color.FromArgb(56, 0, 0, 0)),
        // 0 18px 44px rgba(15,23,42,.16)：对话框、通知、菜单
        "Popup" => new Spec(44, 18, Color.FromArgb(41, 15, 23, 42), Color.FromArgb(115, 0, 0, 0)),
        // 0 6px 18px rgba(0,0,0,.32)：卡片上的玻璃播放钮
        "Glass" => new Spec(18, 6, Color.FromArgb(82, 0, 0, 0), Color.FromArgb(82, 0, 0, 0)),
        // 0 8px 24px rgba(0,0,0,.45)：详情页 72px 播放钮
        "Play" => new Spec(24, 8, Color.FromArgb(115, 0, 0, 0), Color.FromArgb(115, 0, 0, 0)),
        // 0 16px 40px rgba(0,0,0,.5)：播放页中央播放钮
        "Center" => new Spec(40, 16, Color.FromArgb(128, 0, 0, 0), Color.FromArgb(128, 0, 0, 0)),
        // 0 4px 16px rgba(0,0,0,.35)：播放页白色播放钮
        "Solid" => new Spec(16, 4, Color.FromArgb(89, 0, 0, 0), Color.FromArgb(89, 0, 0, 0)),
        // 0 2px 8px rgba(0,0,0,.45)：进度条滑块
        "Thumb" => new Spec(8, 2, Color.FromArgb(115, 0, 0, 0), Color.FromArgb(115, 0, 0, 0)),
        // 0 1px 3px rgba(0,0,0,.55)：卡片上的进度条
        "Progress" => new Spec(3, 1, Color.FromArgb(140, 0, 0, 0), Color.FromArgb(140, 0, 0, 0)),
        _ => null,
    };

    private sealed class HostState
    {
        public Key? Key;
        public SpriteVisual? Visual;
    }

    private readonly record struct Key(string Kind, int Width, int Height, int Radius, bool Dark);

    private sealed class Shared(CompositionBrush brush, float pad, IDisposable[] owned)
    {
        public CompositionBrush Brush { get; } = brush;
        public float Pad { get; } = pad;
        public int References;
        public void Dispose()
        {
            foreach (var item in owned) item.Dispose();
        }
    }

    private static readonly ConditionalWeakTable<FrameworkElement, HostState> Hosts = [];
    private static readonly Dictionary<Key, Shared> Cache = [];

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host) return;
        if (!Hosts.TryGetValue(host, out _))
        {
            Hosts.Add(host, new HostState());
            host.SizeChanged += OnHostSizeChanged;
            host.ActualThemeChanged += OnHostThemeChanged;
            host.Loaded += OnHostLoaded;
            host.Unloaded += OnHostUnloaded;
        }
        Update(host);
    }

    private static void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);
    private static void OnHostThemeChanged(FrameworkElement sender, object args) => Update(sender);
    private static void OnHostLoaded(object sender, RoutedEventArgs e) => Update((FrameworkElement)sender);
    private static void OnHostUnloaded(object sender, RoutedEventArgs e) => Release((FrameworkElement)sender);

    private static void Release(FrameworkElement host)
    {
        if (!Hosts.TryGetValue(host, out var state)) return;
        ElementCompositionPreview.SetElementChildVisual(host, null);
        state.Visual?.Dispose();
        state.Visual = null;
        if (state.Key is { } key && Cache.TryGetValue(key, out var shared) && --shared.References <= 0)
        {
            Cache.Remove(key);
            shared.Dispose();
        }
        state.Key = null;
    }

    private static void Update(FrameworkElement host)
    {
        if (!Hosts.TryGetValue(host, out var state)) return;
        var kind = GetKind(host);
        var spec = Find(kind);
        var width = (int)Math.Round(host.ActualWidth);
        var height = (int)Math.Round(host.ActualHeight);
        if (spec is null || kind is null || !host.IsLoaded || width <= 0 || height <= 0 || !Enabled)
        {
            Release(host);
            return;
        }
        var radius = host is Border border ? (int)Math.Round(border.CornerRadius.TopLeft) : 0;
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
        var key = new Key(kind, width, height, radius, host.ActualTheme == ElementTheme.Dark);
        if (state.Key == key && state.Visual is not null) return;
        Release(host);

        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        if (!Cache.TryGetValue(key, out var shared))
        {
            shared = Build(compositor, key, spec.Value);
            Cache[key] = shared;
        }
        shared.References++;
        var visual = compositor.CreateSpriteVisual();
        visual.Brush = shared.Brush;
        visual.Size = new Vector2(width + shared.Pad * 2, height + shared.Pad * 2);
        visual.Offset = new Vector3(-shared.Pad, -shared.Pad, 0);
        ElementCompositionPreview.SetElementChildVisual(host, visual);
        state.Key = key;
        state.Visual = visual;
    }

    /// <summary>
    /// 投影先画进一张离屏表面，再用"外环"遮罩裁掉圆角矩形内部。外环是一条居中在放大轮廓上的粗描边，
    /// 内缘正好落在控件的圆角矩形上。
    /// </summary>
    private static Shared Build(Compositor compositor, Key key, Spec spec)
    {
        var size = new Vector2(key.Width, key.Height);
        var blur = spec.Blur * CssBlurToRadius;
        var pad = MathF.Ceiling(blur + MathF.Abs(spec.OffsetY));
        var full = size + new Vector2(pad * 2);
        var black = compositor.CreateColorBrush(Colors.Black);

        var casterShape = compositor.CreateShapeVisual();
        casterShape.Size = size;
        var casterGeometry = compositor.CreateRoundedRectangleGeometry();
        casterGeometry.Size = size;
        casterGeometry.CornerRadius = new Vector2(key.Radius);
        var casterSprite = compositor.CreateSpriteShape(casterGeometry);
        casterSprite.FillBrush = black;
        casterShape.Shapes.Add(casterSprite);
        var casterSurface = compositor.CreateVisualSurface();
        casterSurface.SourceVisual = casterShape;
        casterSurface.SourceSize = size;
        var casterBrush = compositor.CreateSurfaceBrush(casterSurface);

        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = blur;
        shadow.Offset = new Vector3(0, spec.OffsetY, 0);
        shadow.Color = key.Dark ? spec.Dark : spec.Light;
        shadow.Mask = casterBrush;
        var caster = compositor.CreateSpriteVisual();
        caster.Size = size;
        caster.Offset = new Vector3(pad, pad, 0);
        caster.Shadow = shadow;
        var stage = compositor.CreateContainerVisual();
        stage.Size = full;
        stage.Children.InsertAtTop(caster);
        var shadowSurface = compositor.CreateVisualSurface();
        shadowSurface.SourceVisual = stage;
        shadowSurface.SourceSize = full;
        var shadowBrush = compositor.CreateSurfaceBrush(shadowSurface);

        var ringShape = compositor.CreateShapeVisual();
        ringShape.Size = full;
        var ringGeometry = compositor.CreateRoundedRectangleGeometry();
        ringGeometry.Size = full;
        ringGeometry.CornerRadius = new Vector2(key.Radius + pad);
        var ringSprite = compositor.CreateSpriteShape(ringGeometry);
        ringSprite.StrokeBrush = black;
        ringSprite.StrokeThickness = pad * 2;
        ringShape.Shapes.Add(ringSprite);
        var ringSurface = compositor.CreateVisualSurface();
        ringSurface.SourceVisual = ringShape;
        ringSurface.SourceSize = full;
        var ringBrush = compositor.CreateSurfaceBrush(ringSurface);

        var masked = compositor.CreateMaskBrush();
        masked.Source = shadowBrush;
        masked.Mask = ringBrush;
        return new Shared(masked, pad,
        [
            masked, ringBrush, ringSurface, ringShape, ringSprite, ringGeometry, shadowBrush, shadowSurface, stage, caster, shadow,
            casterBrush, casterSurface, casterShape, casterSprite, casterGeometry, black,
        ]);
    }

    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    private static bool Enabled => !Accessibility.HighContrast;

    private static readonly ConditionalWeakTable<FrameworkElement, ContainerVisual> DropHosts = [];

    /// <summary>
    /// 给任意元素加轮廓阴影。<paramref name="host"/> 必须排在 <paramref name="source"/> 之前（画在它下面），
    /// 并与它处于同一个父容器；同一个宿主可以挂多个来源。<paramref name="blur"/>、<paramref name="offsetY"/> 按 CSS 的取值填写。
    /// 做入场动画时请动它们共同的父容器，阴影才会跟着走。
    /// </summary>
    public static void AttachDrop(FrameworkElement host, FrameworkElement source, float blur, float offsetY, float opacity)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(source);
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        if (!DropHosts.TryGetValue(host, out var root))
        {
            root = compositor.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(host, root);
            DropHosts.Add(host, root);
        }
        // 把来源元素画进一张离屏表面，用它的透明度当阴影的形状。
        var surface = compositor.CreateVisualSurface();
        surface.SourceVisual = ElementCompositionPreview.GetElementVisual(source);
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = blur * CssBlurToRadius;
        shadow.Offset = new Vector3(0, offsetY, 0);
        shadow.Color = Color.FromArgb((byte)Math.Round(opacity * 255), 0, 0, 0);
        shadow.Mask = compositor.CreateSurfaceBrush(surface);
        var visual = compositor.CreateSpriteVisual();
        visual.Shadow = shadow;
        visual.IsVisible = false;
        root.Children.InsertAtTop(visual);

        void Sync()
        {
            if (!source.IsLoaded || !host.IsLoaded || source.ActualWidth <= 0 || source.ActualHeight <= 0 ||
                source.Visibility != Visibility.Visible || !Enabled)
            {
                visual.IsVisible = false;
                return;
            }
            var size = new Vector2((float)source.ActualWidth, (float)source.ActualHeight);
            var origin = source.TransformToVisual(host).TransformPoint(default);
            surface.SourceSize = size;
            visual.Size = size;
            visual.Offset = new Vector3((float)origin.X, (float)origin.Y, 0);
            visual.IsVisible = true;
        }

        source.SizeChanged += (_, _) => Sync();
        source.Loaded += (_, _) => Sync();
        host.SizeChanged += (_, _) => Sync();
        host.Loaded += (_, _) => Sync();
        source.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Sync());
        Sync();
    }
}