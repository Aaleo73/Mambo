using Mambo.App.ViewModels;
using Mambo.App.Themes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 横向卡片行：竖向滚轮留给页面，横向滚轮或 Shift+滚轮滚动本行；鼠标可以按住拖动，松开后吸附到卡片起点。
/// 标题行右侧的两个箭头每次滚动行宽的 82%，滚到头的一侧禁用，内容不足一行时不显示。
/// </summary>
public sealed partial class CardRail : UserControl
{
    public static readonly DependencyProperty RailProperty = DependencyProperty.Register(nameof(Rail), typeof(RailViewModel), typeof(CardRail),
        new PropertyMetadata(null, (d, _) => ((CardRail)d).OnRailChanged()));

    private const double DragThreshold = 4;
    private static readonly ScrollingScrollOptions Instant = new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);
    private static readonly ScrollingScrollOptions Animated = new(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore);
    private double pitch;
    private bool pressed;
    private bool dragging;
    private double pressX;
    private double pressOffset;

    public CardRail()
    {
        InitializeComponent();
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        // 卡片是按钮，会把指针事件标记为已处理，所以这里连已处理的也要收。
        Scroller.AddHandler(PointerPressedEvent, new PointerEventHandler(OnDragPressed), true);
        Scroller.AddHandler(PointerMovedEvent, new PointerEventHandler(OnDragMoved), true);
        Scroller.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnDragEnded), true);
        Scroller.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnDragEnded), true);
        Scroller.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnDragCaptureLost), true);
    }

    public RailViewModel? Rail { get => (RailViewModel?)GetValue(RailProperty); set => SetValue(RailProperty, value); }

    private void OnRailChanged()
    {
        if (Rail is not { } rail) return;
        Repeater.ItemTemplate = XamlResources.Template(Resources, rail.IsLandscape ? "LandscapeTemplate" : "PosterTemplate");
        var resources = Application.Current.Resources;
        pitch = (double)resources[rail.IsLandscape ? "LandscapeWidth" : "PosterWidth"] + (double)resources["RailSpacing"];
        Skeleton.Children.Clear();
        for (var i = 0; i < 6; i++) Skeleton.Children.Add(CardSkeleton.Create(rail.IsLandscape));
    }

    private void OnViewChanged(ScrollView sender, object args) => UpdateArrows();
    private void OnExtentChanged(ScrollView sender, object args) => UpdateArrows();
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrows();

    private void UpdateArrows()
    {
        var scrollable = Scroller.ScrollableWidth > 1;
        Arrows.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
        LeftArrow.IsEnabled = scrollable && Scroller.HorizontalOffset > 1;
        RightArrow.IsEnabled = scrollable && Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1;
    }

    private void OnLeftClick(object sender, RoutedEventArgs e) => Page(-1);
    private void OnRightClick(object sender, RoutedEventArgs e) => Page(1);
    private void OnLinkClick(object sender, RoutedEventArgs e) => Rail?.OpenLink();
    private void OnRetryClick(object sender, RoutedEventArgs e) => Rail?.Retry();

    /// <summary>翻一页：滚动行宽的 82%，落点吸附到卡片起点，并保证至少前进一张卡。</summary>
    private void Page(int direction)
    {
        var current = Scroller.HorizontalOffset;
        var target = Snap(current + direction * Scroller.ViewportWidth * 0.82);
        if (pitch > 0 && Math.Abs(target - current) < 1) target = current + direction * pitch;
        Scroller.ScrollTo(Math.Clamp(target, 0, Scroller.ScrollableWidth), 0, Animated);
    }

    private double Snap(double offset)
    {
        var max = Scroller.ScrollableWidth;
        if (pitch <= 0) return Math.Clamp(offset, 0, max);
        // 末尾不足一个卡位时停在尽头，不往回吸。
        return offset >= max - pitch / 2 ? max : Math.Clamp(Math.Round(offset / pitch) * pitch, 0, max);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!properties.IsHorizontalMouseWheel && !shift) return;
        var delta = properties.MouseWheelDelta;
        Scroller.ScrollBy(properties.IsHorizontalMouseWheel ? delta : -delta, 0);
        e.Handled = true;
    }

    // 鼠标拖动：移动超过 4px 才算拖动。此时让按下的卡片放弃捕获（它就不会再触发点击），由本行接管指针。
    private void OnDragPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(Scroller);
        if (!point.Properties.IsLeftButtonPressed || Scroller.ScrollableWidth <= 1) return;
        pressed = true;
        dragging = false;
        pressX = point.Position.X;
        pressOffset = Scroller.HorizontalOffset;
    }

    private void OnDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        var delta = e.GetCurrentPoint(Scroller).Position.X - pressX;
        if (!dragging)
        {
            if (Math.Abs(delta) < DragThreshold) return;
            dragging = true;
            for (var source = e.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, Scroller); source = VisualTreeHelper.GetParent(source))
                if (source is ButtonBase button) { button.ReleasePointerCaptures(); break; }
            Scroller.CapturePointer(e.Pointer);
        }
        Scroller.ScrollTo(Math.Clamp(pressOffset - delta, 0, Scroller.ScrollableWidth), 0, Instant);
        e.Handled = true;
    }

    private void OnDragEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        var wasDragging = dragging;
        pressed = dragging = false;
        if (!wasDragging) return;
        Scroller.ReleasePointerCapture(e.Pointer);
        Scroller.ScrollTo(Snap(Scroller.HorizontalOffset), 0, Animated);
        e.Handled = true;
    }

    private void OnDragCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // 卡片放弃捕获时也会冒泡到这里；只有本行自己持有的捕获丢失才算拖动结束。
        if (!dragging || !ReferenceEquals(e.OriginalSource, Scroller)) return;
        pressed = dragging = false;
        Scroller.ScrollTo(Snap(Scroller.HorizontalOffset), 0, Animated);
    }
}
