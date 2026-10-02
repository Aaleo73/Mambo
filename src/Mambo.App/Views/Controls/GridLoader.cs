using System.Numerics;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 网格的增量加载与首屏错开淡入：距末尾不足 1.5 屏时加载下一页；
/// 首屏（前 24 张）在首次加载和条件改变后依次上浮淡入。
/// </summary>
internal sealed class GridLoader
{
    private static readonly TimeSpan RevealWindow = TimeSpan.FromMilliseconds(800);
    private readonly ScrollViewer scroller;
    private readonly Func<PagedCards> cards;
    private DateTime revealUntil = DateTime.UtcNow + RevealWindow;

    public GridLoader(ScrollViewer scroller, ItemsRepeater grid, Func<PagedCards> cards)
    {
        ArgumentNullException.ThrowIfNull(grid);
        this.scroller = scroller;
        this.cards = cards;
    }

    public void Check()
    {
        var source = cards();
        if (!source.HasMore || source.IsLoadingMore || source.HasMoreError || !source.IsInitialized) return;
        var remaining = scroller.ExtentHeight - scroller.VerticalOffset - scroller.ViewportHeight;
        if (remaining < scroller.ViewportHeight * 1.5) _ = source.LoadMoreAsync();
    }

    public void Reveal() => revealUntil = DateTime.UtcNow + RevealWindow;

    public void Prepare(ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index >= 24 || DateTime.UtcNow > revealUntil || !Motion.AnimationsEnabled) return;
        var element = args.Element;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easing = Motion.CreateEasing(compositor, Motion.Settle);
        var delay = TimeSpan.FromMilliseconds(args.Index * 18);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.Duration = Motion.Normal;
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, new Vector3(0, 10, 0));
        offset.InsertKeyFrame(1, Vector3.Zero, easing);
        offset.Duration = Motion.Normal;
        offset.DelayTime = delay;
        offset.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", offset);
    }
}
