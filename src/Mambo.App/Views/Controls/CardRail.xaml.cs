using Mambo.App.ViewModels;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views.Controls;

/// <summary>首页的一行卡片；滚动、拖动和箭头的行为见 <see cref="RailScroller"/>。</summary>
public sealed partial class CardRail : UserControl
{
    public static readonly DependencyProperty RailProperty = DependencyProperty.Register(nameof(Rail), typeof(RailViewModel), typeof(CardRail),
        new PropertyMetadata(null, (d, _) => ((CardRail)d).OnRailChanged()));

    private static readonly ScrollingScrollOptions Instant = new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);
    private static readonly ScrollingScrollOptions Animated = new(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore);
    private readonly RailScroller rail;

    public CardRail()
    {
        InitializeComponent();
        rail = new RailScroller(Scroller, () => Scroller.HorizontalOffset, () => Scroller.ScrollableWidth, () => Scroller.ViewportWidth,
            (offset, animate) => Scroller.ScrollTo(offset, 0, animate ? Animated : Instant));
    }

    public RailViewModel? Rail { get => (RailViewModel?)GetValue(RailProperty); set => SetValue(RailProperty, value); }

    private void OnRailChanged()
    {
        if (Rail is not { } model) return;
        Repeater.ItemTemplate = XamlResources.Template(Resources, model.IsLandscape ? "LandscapeTemplate" : "PosterTemplate");
        var resources = Application.Current.Resources;
        rail.Pitch = (double)resources[model.IsLandscape ? "LandscapeWidth" : "PosterWidth"] + (double)resources["RailSpacing"];
        Skeleton.Children.Clear();
        for (var i = 0; i < 6; i++) Skeleton.Children.Add(CardSkeleton.Create(model.IsLandscape));
    }

    private void OnViewChanged(ScrollView sender, object args) => UpdateArrows();
    private void OnExtentChanged(ScrollView sender, object args) => UpdateArrows();
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrows();
    private void UpdateArrows() => rail.UpdateArrows(Arrows, LeftArrow, RightArrow);

    private void OnLeftClick(object sender, RoutedEventArgs e) => rail.Page(-1);
    private void OnRightClick(object sender, RoutedEventArgs e) => rail.Page(1);
    private void OnLinkClick(object sender, RoutedEventArgs e) => Rail?.OpenLink();
    private void OnRetryClick(object sender, RoutedEventArgs e) => Rail?.Retry();
}
