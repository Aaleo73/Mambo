using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mambo.App.Shell;

/// <summary>
/// 从卡片进详情时的过渡：被点的封面留在原位，一边放大到 1.035 一边淡出（220ms），
/// 盖在正在换入的详情页上面。只是一张几何快照，不参与布局。
/// </summary>
public static class CoverTransition
{
    private static Canvas? layer;

    /// <summary>外壳提供一层盖在页面之上、不接收指针的画布。</summary>
    public static void Attach(Canvas canvas) => layer = canvas;

    public static void Detach(Canvas canvas)
    {
        if (ReferenceEquals(layer, canvas)) layer = null;
    }

    public static void Play(FrameworkElement source, ImageSource? image)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (layer is not { IsLoaded: true } canvas || !Motion.AnimationsEnabled || !source.IsLoaded || source.ActualWidth <= 0) return;
        var bounds = source.TransformToVisual(canvas).TransformBounds(new Rect(0, 0, source.ActualWidth, source.ActualHeight));
        var radius = new CornerRadius(8);
        var cover = new Grid { Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false };
        var shadow = new Border { CornerRadius = radius };
        SoftShadow.SetKind(shadow, "Popup");
        cover.Children.Add(shadow);
        cover.Children.Add(new Border
        {
            CornerRadius = radius,
            Background = image is null
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x80, 0x80, 0x80))
                : new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill },
        });
        Canvas.SetLeft(cover, bounds.X);
        Canvas.SetTop(cover, bounds.Y);
        canvas.Children.Add(cover);

        var visual = ElementCompositionPreview.GetElementVisual(cover);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)bounds.Width / 2, (float)bounds.Height / 2, 0);
        var easing = Motion.CreateEasing(compositor, Motion.Standard);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0, easing);
        fade.Duration = Motion.Cover;
        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(1, new Vector3(1.035f, 1.035f, 1), easing);
        grow.Duration = Motion.Cover;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", grow);
        batch.End();
        batch.Completed += (_, _) => canvas.Children.Remove(cover);
    }

}
