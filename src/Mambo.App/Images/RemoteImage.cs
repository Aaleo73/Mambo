using Mambo.App.Themes;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Images;

/// <summary>
/// 远程图片：占位底色上淡入（140ms）；图片铺满并跟随圆角。卸载或换图时取消加载，
/// 列表回收容器时同样会因 Source 改变而取消。
/// </summary>
public sealed partial class RemoteImage : Grid
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
    private readonly ScalarTransition fade = new();
    private CancellationTokenSource? loading;

    public RemoteImage()
    {
        picture.Background = brush;
        Children.Add(picture);
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => { Cancel(); brush.ImageSource = null; picture.Opacity = 0; };
        SizeChanged += (_, e) => { if (e.PreviousSize.Width <= 0 && DecodeWidth <= 0) Reload(); };
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => picture.CornerRadius = CornerRadius);
    }

    /// <summary>ImageRef；用 object 类型，避免 XAML 元数据为带 init 属性的 record 生成 setter。</summary>
    public object? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double DecodeWidth { get => (double)GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }
    public ImagePriority Priority { get => (ImagePriority)GetValue(PriorityProperty); set => SetValue(PriorityProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    /// <summary>图片已显示（用于需要在图片到位后再开始的动画）。</summary>
    public event EventHandler? ImageOpened;
    /// <summary>开始换图：叠在图上的文字应先藏起来。</summary>
    public event EventHandler? Pending;
    /// <summary>这张图有了结果（显示出来、没有图或加载失败）；参数表示是否需要淡入。</summary>
    public event EventHandler<bool>? Settled;

    private void Cancel()
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = null;
    }

    private async void Reload()
    {
        Cancel();
        picture.OpacityTransition = null;
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
        loading = cts;
        try
        {
            var bitmap = await loader.LoadAsync(image, width, XamlRoot?.RasterizationScale ?? 1, Priority, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (bitmap is not null) Show(bitmap, animate: true);
            else Settled?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(ImageSource source, bool animate)
    {
        brush.ImageSource = source;
        if (animate && Motion.AnimationsEnabled)
        {
            fade.Duration = TimeSpan.FromMilliseconds(140);
            picture.OpacityTransition = fade;
        }
        picture.Opacity = 1;
        ImageOpened?.Invoke(this, EventArgs.Empty);
        Settled?.Invoke(this, animate);
    }
}
