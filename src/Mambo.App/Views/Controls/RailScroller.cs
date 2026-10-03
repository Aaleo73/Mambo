using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 横向卡片行的滚动行为，首页卡片行与详情页的集、演职人员共用：
/// 竖向滚轮留给页面，横向滚轮或 Shift+滚轮滚动本行；鼠标按住拖动超过 4px 开始滚动，松开后吸附到卡片起点；
/// 箭头每次翻行宽的 82%，滚到头的一侧禁用，内容不足一行时不显示。
/// </summary>
internal sealed class RailScroller
{
    private const double DragThreshold = 4;
    private readonly UIElement surface;
    private readonly Func<double> offset;
    private readonly Func<double> max;
    private readonly Func<double> viewport;
    private readonly Action<double, bool> scrollTo;
    private bool pressed;
    private bool dragging;
    private double pressX;
    private double pressOffset;

    /// <param name="surface">接收指针事件的滚动区域。</param>
    /// <param name="offset">当前横向偏移。</param>
    /// <param name="max">最大可滚动偏移。</param>
    /// <param name="viewport">可见宽度。</param>
    /// <param name="scrollTo">滚到指定偏移；第二个参数表示是否带动画。</param>
    public RailScroller(UIElement surface, Func<double> offset, Func<double> max, Func<double> viewport, Action<double, bool> scrollTo)
    {
        this.surface = surface;
        this.offset = offset;
        this.max = max;
        this.viewport = viewport;
        this.scrollTo = scrollTo;
        // 卡片是按钮，会把指针事件标记为已处理，所以连已处理的也要收。
        surface.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnEnded), true);
        surface.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnEnded), true);
        surface.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), true);
    }

    /// <summary>卡片宽度加间距；大于 0 时落点吸附到它的整数倍。</summary>
    public double Pitch { get; set; }

    /// <summary>是否允许横向或 Shift+滚轮；关闭后滚轮留给外层页面。</summary>
    public bool IsWheelEnabled { get; init; } = true;

    /// <summary>按当前位置更新箭头：不能滚动时整组隐藏，到头的一侧禁用。</summary>
    public void UpdateArrows(FrameworkElement group, Control left, Control right)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var limit = max();
        var scrollable = limit > 1;
        group.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
        left.IsEnabled = scrollable && offset() > 1;
        right.IsEnabled = scrollable && offset() < limit - 1;
    }

    /// <summary>翻一页：行宽的 82%，落点吸附到卡片起点，并保证至少前进一张卡。</summary>
    public void Page(int direction)
    {
        var current = offset();
        var target = Snap(current + direction * viewport() * 0.82);
        if (Pitch > 0 && Math.Abs(target - current) < 1) target = current + direction * Pitch;
        scrollTo(Math.Clamp(target, 0, max()), true);
    }

    private double Snap(double value)
    {
        var limit = max();
        if (Pitch <= 0) return Math.Clamp(value, 0, limit);
        // 末尾不足一个卡位时停在尽头，不往回吸。
        return value >= limit - Pitch / 2 ? limit : Math.Clamp(Math.Round(value / Pitch) * Pitch, 0, limit);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!IsWheelEnabled) return;
        var properties = e.GetCurrentPoint(surface).Properties;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!properties.IsHorizontalMouseWheel && !shift) return;
        var delta = properties.IsHorizontalMouseWheel ? properties.MouseWheelDelta : -properties.MouseWheelDelta;
        scrollTo(Math.Clamp(offset() + delta, 0, max()), false);
        e.Handled = true;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(surface);
        if (!point.Properties.IsLeftButtonPressed || max() <= 1) return;
        pressed = true;
        dragging = false;
        pressX = point.Position.X;
        pressOffset = offset();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        var delta = e.GetCurrentPoint(surface).Position.X - pressX;
        if (!dragging)
        {
            if (Math.Abs(delta) < DragThreshold) return;
            dragging = true;
            // 让按下的卡片放弃捕获，它就不会再触发点击；之后由本行接管指针。
            for (var source = e.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, surface); source = VisualTreeHelper.GetParent(source))
                if (source is ButtonBase button) { button.ReleasePointerCaptures(); break; }
            surface.CapturePointer(e.Pointer);
        }
        scrollTo(Math.Clamp(pressOffset - delta, 0, max()), false);
        e.Handled = true;
    }

    private void OnEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        var wasDragging = dragging;
        pressed = dragging = false;
        if (!wasDragging) return;
        surface.ReleasePointerCapture(e.Pointer);
        scrollTo(Snap(offset()), true);
        e.Handled = true;
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // 卡片放弃捕获时也会冒泡到这里；只有本行自己持有的捕获丢失才算拖动结束。
        if (!dragging || !ReferenceEquals(e.OriginalSource, surface)) return;
        pressed = dragging = false;
        scrollTo(Snap(offset()), true);
    }
}
