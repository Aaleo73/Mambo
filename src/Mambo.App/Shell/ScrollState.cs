using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Shell;

/// <summary>等内容布局出足够高度后恢复滚动位置，内容不够时停在能到达的最远处。</summary>
public static class ScrollState
{
    public static IDisposable Restore(ScrollViewer scroller, double offset)
    {
        ArgumentNullException.ThrowIfNull(scroller);
        return new Restoration(scroller, Math.Max(0, offset));
    }

    private sealed class Restoration : IDisposable
    {
        private readonly ScrollViewer scroller;
        private readonly double offset;
        private readonly DispatcherQueueTimer deadline;
        private bool queued;
        private bool deadlineReached;
        private double? requestedOffset;
        private bool disposed;

        public Restoration(ScrollViewer scroller, double offset)
        {
            this.scroller = scroller;
            this.offset = offset;
            deadline = scroller.DispatcherQueue.CreateTimer();
            deadline.Interval = TimeSpan.FromSeconds(10);
            deadline.IsRepeating = false;
            if (offset <= 0) { disposed = true; return; }
            deadline.Tick += OnDeadline;
            scroller.LayoutUpdated += OnLayout;
            scroller.Loaded += OnLoaded;
            scroller.ViewChanged += OnViewChanged;
            scroller.Unloaded += OnUnloaded;
            scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnUserScroll), true);
            deadline.Start();
            QueueApply();
        }

        private void OnLayout(object? sender, object e)
        {
            // LayoutUpdated 仍在当前布局遍历内；滚动会使虚拟化网格重新布局。
            // 这里只排队，避免在一轮布局中反复 ChangeView 形成反馈环。
            QueueApply();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => QueueApply();

        private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            if (!args.IsIntermediate) requestedOffset = null;
            QueueApply();
        }

        private void QueueApply()
        {
            if (disposed || queued) return;
            queued = true;
            if (!scroller.DispatcherQueue.TryEnqueue(Apply)) Dispose();
        }

        private void Apply()
        {
            queued = false;
            if (disposed) return;
            if (!scroller.IsLoaded) { if (deadlineReached) Dispose(); return; }
            var target = Math.Min(offset, scroller.ScrollableHeight);
            // 相同的已接受请求在 ViewChanged 确认前不重复；到达位置后也不发空请求。
            if (Math.Abs(scroller.VerticalOffset - target) > 0.5 &&
                (!requestedOffset.HasValue || Math.Abs(requestedOffset.Value - target) > 0.5) &&
                scroller.ChangeView(null, target, null, true)) requestedOffset = target;
            if (deadlineReached || (scroller.ScrollableHeight + 0.5 >= offset && Math.Abs(scroller.VerticalOffset - offset) <= 0.5)) Dispose();
        }

        private void OnDeadline(DispatcherQueueTimer sender, object args)
        {
            deadlineReached = true;
            QueueApply();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args) => Dispose();
        private void OnUserScroll(object sender, PointerRoutedEventArgs args) => Dispose();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            deadline.Stop();
            deadline.Tick -= OnDeadline;
            scroller.LayoutUpdated -= OnLayout;
            scroller.Loaded -= OnLoaded;
            scroller.ViewChanged -= OnViewChanged;
            scroller.Unloaded -= OnUnloaded;
            scroller.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnUserScroll));
        }
    }
}
