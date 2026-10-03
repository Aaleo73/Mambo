using System.Numerics;
using System.ComponentModel;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 网格的增量加载与首屏错开淡入：距末尾不足 1.5 屏时加载下一页；
/// 首屏（前 24 张）在首次加载和条件改变后依次上浮淡入。
/// </summary>
internal sealed class GridLoader : IDisposable
{
    private static readonly TimeSpan RevealWindow = TimeSpan.FromMilliseconds(800);
    private readonly ScrollViewer scroller;
    private readonly Func<PagedCards> cards;
    private DateTime revealUntil = DateTime.UtcNow + RevealWindow;
    private CancellationTokenSource? restoration;
    private bool checkQueued;
    private bool active = true;
    private bool disposed;

    public GridLoader(ScrollViewer scroller, ItemsRepeater grid, Func<PagedCards> cards)
    {
        ArgumentNullException.ThrowIfNull(grid);
        this.scroller = scroller;
        this.cards = cards;
        scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnUserScroll), true);
    }

    public void Check()
    {
        if (disposed || checkQueued) return;
        checkQueued = true;
        // SizeChanged/ViewChanged 可以在布局内触发；分页命令留到布局回调返回后。
        if (!scroller.DispatcherQueue.TryEnqueue(CheckCore)) checkQueued = false;
    }

    private void CheckCore()
    {
        checkQueued = false;
        if (disposed || !active || restoration is not null || !scroller.IsLoaded) return;
        var source = cards();
        if (!source.HasMore || source.IsLoadingMore || source.HasMoreError || !source.IsInitialized) return;
        var remaining = scroller.ExtentHeight - scroller.VerticalOffset - scroller.ViewportHeight;
        if (remaining < scroller.ViewportHeight * 1.5) _ = source.LoadMoreAsync();
    }

    public void Reveal() => revealUntil = DateTime.UtcNow + RevealWindow;

    public void SetActive(bool value)
    {
        active = value;
        if (!value) CancelRestore();
    }

    public void CancelRestore()
    {
        restoration?.Cancel();
        restoration?.Dispose();
        restoration = null;
    }

    public Task RestoreAsync(double offset)
    {
        CancelRestore();
        if (offset <= 0 || disposed) return Task.CompletedTask;
        restoration = new CancellationTokenSource();
        return RestoreCoreAsync(offset, restoration);
    }

    private async Task RestoreCoreAsync(double offset, CancellationTokenSource operation)
    {
        var token = operation.Token;
        try
        {
            await WaitForReadyAsync(cards(), token);
            await NextRenderAsync(token);
            await PagedScrollRestorer.RestoreAsync(offset,
                () => scroller.ScrollableHeight,
                value => { if (Math.Abs(scroller.VerticalOffset - value) > 0.5) scroller.ChangeView(null, value, null, true); },
                async cancellationToken =>
                {
                    var source = cards();
                    await WaitForReadyAsync(source, cancellationToken);
                    await NextRenderAsync(cancellationToken);
                    if (scroller.ScrollableHeight + 0.5 >= offset || !source.HasMore || source.HasError || source.HasMoreError) return false;
                    var count = source.Items.Count;
                    await source.LoadMoreAsync().WaitAsync(cancellationToken);
                    await WaitForReadyAsync(source, cancellationToken);
                    await NextRenderAsync(cancellationToken);
                    return source.Items.Count > count;
                }, token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(restoration, operation))
            {
                restoration = null;
                operation.Dispose();
                Check();
            }
        }
    }

    private static async Task WaitForReadyAsync(PagedCards source, CancellationToken token)
    {
        if ((source.IsInitialized || source.HasError) && !source.IsLoadingMore) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if ((source.IsInitialized || source.HasError) && !source.IsLoadingMore) ready.TrySetResult();
        }
        source.PropertyChanged += OnChanged;
        try { OnChanged(null, new PropertyChangedEventArgs(null)); await ready.Task.WaitAsync(token); }
        finally { source.PropertyChanged -= OnChanged; }
    }

    private static async Task NextRenderAsync(CancellationToken token)
    {
        // 等实际布局/渲染帧，不在查询的 PropertyChanged 或布局事件内同步 UpdateLayout。
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args) => completion.TrySetResult();
        CompositionTarget.Rendering += OnRendering;
        try { await completion.Task.WaitAsync(token); }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelRestore();
        scroller.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnUserScroll));
    }

    private void OnUserScroll(object sender, PointerRoutedEventArgs e) => CancelRestore();

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
