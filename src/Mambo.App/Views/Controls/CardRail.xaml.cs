using Mambo.App.ViewModels;
using Mambo.App.Themes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 横向卡片行：竖向滚轮留给页面，横向滚轮或 Shift+滚轮滚动本行；
/// 两侧箭头每次滚动行宽的 82%，悬停时出现。
/// </summary>
public sealed partial class CardRail : UserControl
{
    public static readonly DependencyProperty RailProperty = DependencyProperty.Register(nameof(Rail), typeof(RailViewModel), typeof(CardRail),
        new PropertyMetadata(null, (d, _) => ((CardRail)d).OnRailChanged()));

    private readonly ScalarTransition fade = new() { Duration = TimeSpan.FromMilliseconds(160) };
    private bool hovering;

    public CardRail()
    {
        InitializeComponent();
        LeftArrow.OpacityTransition = fade;
        RightArrow.OpacityTransition = fade;
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
    }

    public RailViewModel? Rail { get => (RailViewModel?)GetValue(RailProperty); set => SetValue(RailProperty, value); }

    private void OnRailChanged()
    {
        if (Rail is not { } rail) return;
        Repeater.ItemTemplate = XamlResources.Template(Resources, rail.IsLandscape ? "LandscapeTemplate" : "PosterTemplate");
        Skeleton.Children.Clear();
        for (var i = 0; i < 8; i++)
        {
            Skeleton.Children.Add(new Border
            {
                Style = XamlResources.Style(Application.Current.Resources, "SkeletonBlockStyle"),
                Width = rail.IsLandscape ? 300 : 150,
                Height = rail.IsLandscape ? 169 : 220,
            });
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        hovering = true;
        UpdateArrows();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        hovering = false;
        UpdateArrows();
    }

    private void OnViewChanged(ScrollView sender, object args) => UpdateArrows();

    private void UpdateArrows()
    {
        var canLeft = hovering && Scroller.HorizontalOffset > 1;
        var canRight = hovering && Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1;
        LeftArrow.Opacity = canLeft ? 1 : 0;
        LeftArrow.IsHitTestVisible = canLeft;
        RightArrow.Opacity = canRight ? 1 : 0;
        RightArrow.IsHitTestVisible = canRight;
    }

    private void OnLeftClick(object sender, RoutedEventArgs e) => Scroller.ScrollBy(-Scroller.ViewportWidth * 0.82, 0);
    private void OnRightClick(object sender, RoutedEventArgs e) => Scroller.ScrollBy(Scroller.ViewportWidth * 0.82, 0);
    private void OnLinkClick(object sender, RoutedEventArgs e) => Rail?.OpenLink();
    private void OnRetryClick(object sender, RoutedEventArgs e) => Rail?.Retry();

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!properties.IsHorizontalMouseWheel && !shift) return;
        var delta = properties.MouseWheelDelta;
        Scroller.ScrollBy(properties.IsHorizontalMouseWheel ? delta : -delta, 0);
        e.Handled = true;
    }
}
