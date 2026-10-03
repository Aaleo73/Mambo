using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views.Controls;

/// <summary>卡片形状的加载占位：一块与图片同尺寸同圆角的底，加两条文字条。静止，不做扫光。</summary>
internal static class CardSkeleton
{
    public static FrameworkElement Create(bool landscape, double width = double.NaN)
    {
        var resources = Application.Current.Resources;
        var baseWidth = (double)resources[landscape ? "LandscapeWidth" : "PosterWidth"];
        var baseHeight = (double)resources[landscape ? "LandscapeHeight" : "PosterHeight"];
        var artWidth = double.IsFinite(width) && width > 0 ? width : baseWidth;
        var style = XamlResources.Style(resources, "SkeletonBlockStyle");
        var panel = new StackPanel { Width = artWidth };
        panel.Children.Add(new Border
        {
            Style = style,
            Height = Math.Round(artWidth * baseHeight / baseWidth),
            CornerRadius = (CornerRadius)resources[landscape ? "LandscapeCornerRadius" : "PosterCornerRadius"],
        });
        // 文字区与卡片一致：图下 6，标题条 14 高、宽 3/4；副标题条 12 高、宽 1/2。
        var copy = new StackPanel { Spacing = 8, Margin = new Thickness(landscape ? 0 : 4, 8, landscape ? 0 : 4, 0), Height = landscape ? 45 : 48 };
        copy.Children.Add(new Border { Style = style, Height = 14, Width = Math.Round(artWidth * 0.7), CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left });
        copy.Children.Add(new Border { Style = style, Height = 12, Width = Math.Round(artWidth * 0.45), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(copy);
        return panel;
    }
}
