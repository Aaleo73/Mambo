using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 未登录时首页的大字标题「前往连接你的 / Emby 服务器」。保留两行版式，字号随内容区宽度变化。
/// </summary>
public sealed partial class OnboardingTitle : StackPanel
{
    private static readonly string[][] Lines =
    [
        ["前往连接你的"],
        ["Emby", "服务器"],
    ];

    private readonly List<TextBlock> segments = [];
    private readonly List<StackPanel> rows = [];
    private readonly List<FrameworkElement> gaps = [];
    private double fontSize = 96;

    public OnboardingTitle()
    {
        var style = XamlResources.Style(Application.Current.Resources, "OnboardingTitleStyle");
        for (var line = 0; line < Lines.Length; line++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (var group = 0; group < Lines[line].Length; group++)
            {
                if (group > 0)
                {
                    var gap = new Border();
                    gaps.Add(gap);
                    row.Children.Add(gap);
                }
                var element = new TextBlock { Text = Lines[line][group], Style = style };
                segments.Add(element);
                row.Children.Add(element);
            }
            rows.Add(row);
            Children.Add(row);
        }
        ApplySize();
    }

    /// <summary>字号为内容宽度的 16%，限制在 64–208 之间。</summary>
    public void Fit(double contentWidth)
    {
        var size = Math.Clamp(Math.Round(contentWidth * 0.16), 64, 208);
        if (size == fontSize) return;
        fontSize = size;
        ApplySize();
    }

    private void ApplySize()
    {
        foreach (var segment in segments)
        {
            segment.FontSize = fontSize;
            segment.LineHeight = Math.Round(fontSize * 1.08);
        }
        foreach (var gap in gaps) gap.Width = Math.Round(fontSize * 0.3);
        if (rows.Count > 1) rows[1].Margin = new Thickness(0, Math.Round(fontSize * 0.22), 0, 0);
    }

}
