using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Mambo.App.Views.Controls;

/// <summary>首页与详情页 hero 共用的画法：底部渐隐遮罩、文字区压暗和高度规则。</summary>
internal static class HeroArt
{
    /// <summary>背景图向 hero 底边以下多画的高度，在这段里渐隐到透明。</summary>
    public const double FadeExtent = 96;

    // 图片在 58% 之前完全不透明，之后沿一条平缓的曲线淡出；分段少了底边会出现一条灰带。
    private static readonly (float Offset, byte Alpha)[] EdgeFadeStops =
    [
        (0f, 255), (0.58f, 255), (0.68f, 245), (0.75f, 222), (0.80f, 184), (0.84f, 133),
        (0.875f, 82), (0.91f, 43), (0.945f, 18), (0.975f, 5), (1f, 0),
    ];

    // 只压暗左下角标题所在的一块椭圆，向四周羽化；右半边保持原图。
    private static readonly (float Offset, float Alpha)[] ScrimStops =
    [
        (0f, 0.56f), (0.16f, 0.50f), (0.30f, 0.41f), (0.44f, 0.31f), (0.57f, 0.21f), (0.70f, 0.12f), (0.84f, 0.05f), (1f, 0f),
    ];

    public static CompositionLinearGradientBrush CreateEdgeFade(Compositor compositor)
    {
        var brush = compositor.CreateLinearGradientBrush();
        brush.StartPoint = new Vector2(0, 0);
        brush.EndPoint = new Vector2(0, 1);
        foreach (var (offset, alpha) in EdgeFadeStops)
            brush.ColorStops.Add(compositor.CreateColorGradientStop(offset, Color.FromArgb(alpha, 255, 255, 255)));
        return brush;
    }

    /// <summary>文字区压暗；与图片用同一张渐隐遮罩，所以会跟着图片一起在底边消失。</summary>
    public static SpriteVisual CreateCopyScrim(Compositor compositor, CompositionBrush edgeFade)
    {
        var radial = compositor.CreateRadialGradientBrush();
        radial.EllipseCenter = new Vector2(0.04f, 0.86f);
        radial.EllipseRadius = new Vector2(1f, 0.58f);
        foreach (var (offset, alpha) in ScrimStops)
            radial.ColorStops.Add(compositor.CreateColorGradientStop(offset, Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0)));
        var masked = compositor.CreateMaskBrush();
        masked.Source = radial;
        masked.Mask = edgeFade;
        var visual = compositor.CreateSpriteVisual();
        visual.Brush = masked;
        return visual;
    }

    /// <summary>
    /// 评分信息行：★ 评分、年份、类型、分级徽标，项之间只靠间距分开，没有圆点。
    /// 返回是否有内容。
    /// </summary>
    public static bool BuildMeta(Panel row, string rating, string year, string genres, string officialRating)
    {
        ArgumentNullException.ThrowIfNull(row);
        var resources = Application.Current.Resources;
        TextBlock Text(string value) => new() { Text = value, Style = XamlResources.Style(resources, "HeroMetaTextStyle") };
        row.Children.Clear();
        if (!string.IsNullOrEmpty(rating))
        {
            var star = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            star.Children.Add(new TextBlock { Text = "★", Style = XamlResources.Style(resources, "HeroStarTextStyle") });
            star.Children.Add(Text(rating));
            row.Children.Add(star);
        }
        foreach (var value in new[] { year, genres })
            if (!string.IsNullOrEmpty(value)) row.Children.Add(Text(value));
        if (!string.IsNullOrEmpty(officialRating))
        {
            row.Children.Add(new Border
            {
                Style = XamlResources.Style(resources, "HeroBadgeStyle"),
                Child = new TextBlock { Text = officialRating, Style = XamlResources.Style(resources, "HeroBadgeTextStyle") },
            });
        }
        return row.Children.Count > 0;
    }

    /// <summary>
    /// hero 高度：窗口高的 62%，不低于 500；上限随内容区变宽而升高（至少 600），
    /// 这样宽窗口下背景图不会被裁成一条细带。
    /// </summary>
    public static double Height(double windowHeight, double contentWidth)
    {
        var resources = Application.Current.Resources;
        var min = (double)resources["HeroMinHeight"];
        var max = Math.Max((double)resources["HeroMaxHeight"], Math.Round(contentWidth * (double)resources["HeroMaxWidthRatio"]));
        return Math.Clamp(Math.Round(windowHeight * (double)resources["HeroHeightRatio"]), min, max);
    }
}
