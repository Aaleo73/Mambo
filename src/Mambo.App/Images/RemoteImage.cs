using Mambo.App.Themes;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Images;

/// <summary>
/// 远程图片：只有冷图就绪时淡入；父级交接、缓存与非活动页面直接落终态。
/// 换绑和卸载使旧加载失效，不为每张卡片订阅原生设置。
/// </summary>
public sealed partial class RemoteImage : Grid, IMotionParticipant
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(object), typeof(RemoteImage),
        new PropertyMetadata(null, (d, _) => ((RemoteImage)d).Reload()));
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(nameof(DecodeWidth), typeof(double), typeof(RemoteImage),
        new PropertyMetadata(0d));
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.Register(nameof(Priority), typeof(ImagePriority), typeof(RemoteImage),
        new PropertyMetadata(ImagePriority.Visible));
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(RemoteImage),
        new PropertyMetadata(Stretch.UniformToFill, (d, e) => ((RemoteImage)d).brush.Stretch = (Stretch)e.NewValue));

    private readonly Border picture = new() { Opacity = 0 };
    private readonly ImageBrush brush = new() { Stretch = Stretch.UniformToFill };
    private CompositionScopedBatch? fadeBatch;
    private CancellationTokenSource? loading;
    private int generation;

    public RemoteImage()
    {
        picture.Background = brush;
        Children.Add(picture);
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => { Cancel(); brush.ImageSource = null; SettleMotion(); };
        SizeChanged += (_, e) => { if (e.PreviousSize.Width <= 0 && DecodeWidth <= 0) Reload(); };
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => picture.CornerRadius = CornerRadius);
    }

    /// <summary>ImageRef；用 object 类型，避免 XAML 元数据为带 init 属性的 record 生成 setter。</summary>
    public object? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double DecodeWidth { get => (double)GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }
    public ImagePriority Priority { get => (ImagePriority)GetValue(PriorityProperty); set => SetValue(PriorityProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    /// <summary>图片不铺满时（Uniform）在水平方向靠哪边，默认居中。</summary>
    public AlignmentX ImageAlignmentX { get => brush.AlignmentX; set => brush.AlignmentX = value; }

    /// <summary>当前显示的位图；还没有图时为 null。</summary>
    public ImageSource? CurrentImage => brush.ImageSource;

    /// <summary>图片已显示（用于需要在图片到位后再开始的动画）。</summary>
    public event EventHandler? ImageOpened;
    /// <summary>开始换图：叠在图上的文字应先藏起来。</summary>
    public event EventHandler? Pending;
    /// <summary>这张图有了结果（显示出来、没有图或加载失败）；参数表示是否需要淡入。</summary>
    public event EventHandler<bool>? Settled;

    private void Cancel()
    {
        generation++;
        SettleMotion();
        loading?.Cancel();
        loading?.Dispose();
        loading = null;
    }

    private async void Reload()
    {
        Cancel();
        picture.Opacity = 0;
        brush.ImageSource = null;
        Pending?.Invoke(this, EventArgs.Empty);
        if (!IsLoaded || XamlRoot is null) return;
        if (Source is not ImageRef image || ImageLoader.Current is not { } loader)
        {
            Settled?.Invoke(this, false);
            return;
        }
        var width = DecodeWidth > 0 ? DecodeWidth : ActualWidth;
        if (width <= 0) return;
        var decodeWidth = (int)Math.Ceiling(width);
        if (loader.TryGetDecoded(image, decodeWidth) is { } cached)
        {
            Show(cached, animate: false);
            return;
        }
        var cts = new CancellationTokenSource();
        var version = generation;
        var token = cts.Token;
        loading = cts;
        try
        {
            var bitmap = await loader.LoadAsync(image, width, XamlRoot?.RasterizationScale ?? 1, Priority, token);
            if (token.IsCancellationRequested || version != generation || !IsLoaded) return;
            if (bitmap is not null) Show(bitmap, animate: true);
            else Settled?.Invoke(this, false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(loading, cts)) loading = null;
            cts.Dispose();
        }
    }

    private void Show(ImageSource source, bool animate)
    {
        brush.ImageSource = source;
        var reveal = animate && IsLoaded && Motion.IsActive(this) && Motion.AnimationsEnabled && !Motion.IsEntranceSuppressed(this);
        picture.Opacity = 1;
        var visual = ElementCompositionPreview.GetElementVisual(picture);
        if (reveal)
        {
            visual.Opacity = 0;
            var compositor = visual.Compositor;
            using var easing = Motion.CreateEasing(compositor, Motion.EaseOut);
            using var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1, 1, easing);
            animation.Duration = Motion.Feedback;
            fadeBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            fadeBatch.Completed += OnFadeCompleted;
            visual.StartAnimation("Opacity", animation);
            fadeBatch.End();
        }
        else visual.Opacity = 1;
        ImageOpened?.Invoke(this, EventArgs.Empty);
        Settled?.Invoke(this, reveal);
    }

    internal bool IsRevealing => fadeBatch is not null;
    internal bool IsLoading => loading is not null;

    public void SettleMotion()
    {
        if (fadeBatch is { } batch)
        {
            fadeBatch = null;
            batch.Completed -= OnFadeCompleted;
            batch.Dispose();
        }
        var visual = ElementCompositionPreview.GetElementVisual(picture);
        visual.StopAnimation("Opacity");
        picture.Opacity = brush.ImageSource is null ? 0 : 1;
        visual.Opacity = (float)picture.Opacity;
    }

    private void OnFadeCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (ReferenceEquals(sender, fadeBatch)) SettleMotion();
    }
}
