using System.ComponentModel;
using Mambo.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 网格的增量加载与历史滚动恢复；距末尾不足 1.5 屏时加载下一页。
/// </summary>
internal sealed class GridLoader : IDisposable
{
    private readonly ScrollViewer scroller;
    private readonly Func<PagedCards> cards;
    private CancellationTokenSource presentation = new();
    private TaskCompletionSource sourceChanged = NewSourceSignal();
    private Task pendingRestore = Task.CompletedTask;
    private CancellationTokenSource? restoration;
    private bool checkQueued;
    private bool active = true;
    private bool disposed;

    public GridLoader(ScrollViewer scroller, Func<PagedCards> cards)
    {
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

    internal Task PendingRestore => pendingRestore;

    public void NotifySourceChanged()
    {
        var previous = sourceChanged;
        sourceChanged = NewSourceSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSourceSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task WaitForPresentationAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, presentation.Token);
        var token = operation.Token;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var source = cards();
            var changed = sourceChanged.Task;
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var readiness = WaitForReadyAsync(source, ready.Token, waitForMore: false);
                try
                {
                    if (await Task.WhenAny(readiness, changed).WaitAsync(token) == changed) continue;
                    await readiness;
                }
                finally
                {
                    ready.Cancel();
                    try { await readiness; }
                    catch (OperationCanceledException) when (ready.IsCancellationRequested) { }
                }
            }
            var restoring = pendingRestore;
            await restoring.WaitAsync(token);
            await NextRenderAsync(token);
            if (ReferenceEquals(source, cards()) && ReferenceEquals(restoring, pendingRestore)) return;
        }
    }

    public void SetActive(bool value)
    {
        if (disposed || active == value) return;
        active = value;
        if (value)
        {
            presentation.Dispose();
            presentation = new CancellationTokenSource();
            Check();
        }
        else
        {
            presentation.Cancel();
            CancelRestore();
        }
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
        if (offset <= 0 || disposed || !active) return pendingRestore = Task.CompletedTask;
        restoration = new CancellationTokenSource();
        return pendingRestore = RestoreCoreAsync(offset, restoration);
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

    private static async Task WaitForReadyAsync(PagedCards source, CancellationToken token, bool waitForMore = true)
    {
        if ((source.IsInitialized || source.HasError) && (!waitForMore || !source.IsLoadingMore)) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if ((source.IsInitialized || source.HasError) && (!waitForMore || !source.IsLoadingMore)) ready.TrySetResult();
        }
        source.PropertyChanged += OnChanged;
        using var canceled = token.Register(() =>
        {
            source.PropertyChanged -= OnChanged;
            ready.TrySetCanceled(token);
        });
        try { OnChanged(null, new PropertyChangedEventArgs(null)); await ready.Task; }
        finally { source.PropertyChanged -= OnChanged; }
    }

    internal static async Task NextRenderAsync(CancellationToken token)
    {
        // 等实际布局/渲染帧，不在查询的 PropertyChanged 或布局事件内同步 UpdateLayout。
        token.ThrowIfCancellationRequested();
        var queue = DispatcherQueue.GetForCurrentThread();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Unsubscribe() => CompositionTarget.Rendering -= OnRendering;
        void OnRendering(object? sender, object args)
        {
            Unsubscribe();
            completion.TrySetResult();
        }
        CompositionTarget.Rendering += OnRendering;
        using var canceled = token.Register(() =>
        {
            if (queue.HasThreadAccess) Unsubscribe();
            else queue.TryEnqueue(Unsubscribe);
            completion.TrySetCanceled(token);
        });
        try { await completion.Task; }
        finally { Unsubscribe(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        presentation.Cancel();
        presentation.Dispose();
        CancelRestore();
        scroller.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnUserScroll));
    }

    private void OnUserScroll(object sender, PointerRoutedEventArgs e) => CancelRestore();

}
