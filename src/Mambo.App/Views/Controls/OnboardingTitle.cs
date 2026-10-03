using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 未登录时首页的大字标题「前往连接你的 / Emby 服务器」。字号随内容区宽度变化；
/// 鼠标划过时，被指到的字从左下角弹起放大，左右相邻的字被推开，"Emby" 作为一个整体起伏。
/// 系统关闭动画时只是静止的文字。
/// </summary>
public sealed partial class OnboardingTitle : StackPanel
{
    private sealed record Segment(TextBlock Element, int Line, int Group, int Index, bool Unit);

    private static readonly (string Text, bool Unit)[][] Lines =
    [
        [("前往连接你的", false)],
        [("Emby", true), ("服务器", false)],
    ];

    private readonly List<Segment> segments = [];
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
                var (text, unit) = Lines[line][group];
                if (group > 0)
                {
                    var gap = new Border();
                    gaps.Add(gap);
                    row.Children.Add(gap);
                }
                var pieces = unit ? [text] : text.Select(c => c.ToString()).ToArray();
                for (var index = 0; index < pieces.Length; index++)
                {
                    var element = new TextBlock { Text = pieces[index], Style = style };
                    var segment = new Segment(element, line, group, index, unit);
                    element.PointerEntered += (_, _) => Hover(segment);
                    element.PointerExited += (_, _) => Hover(null);
                    segments.Add(segment);
                    row.Children.Add(element);
                }
            }
            rows.Add(row);
            Children.Add(row);
        }
        Loaded += (_, _) => Enter();
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
            segment.Element.FontSize = fontSize;
            segment.Element.LineHeight = Math.Round(fontSize * 1.08);
        }
        foreach (var gap in gaps) gap.Width = Math.Round(fontSize * 0.3);
        if (rows.Count > 1) rows[1].Margin = new Thickness(0, Math.Round(fontSize * 0.22), 0, 0);
    }

    /// <summary>两行依次从下方 16px 弹入。</summary>
    private void Enter()
    {
        if (!Motion.AnimationsEnabled) return;
        for (var i = 0; i < rows.Count; i++)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(rows[i], true);
            var visual = ElementCompositionPreview.GetElementVisual(rows[i]);
            var compositor = visual.Compositor;
            var delay = TimeSpan.FromMilliseconds(120 * i);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, Motion.CreateEasing(compositor, Motion.Enter));
            fade.Duration = Motion.HeroContent;
            fade.DelayTime = delay;
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Opacity", fade);
            visual.Properties.InsertVector3("Translation", new Vector3(0, 16, 0));
            visual.StartAnimation("Translation", Spring(compositor, Vector3.Zero, 0.8f, 260, delay));
        }
    }

    private void Hover(Segment? hovered)
    {
        if (!Motion.AnimationsEnabled) return;
        foreach (var segment in segments)
        {
            var sameGroup = hovered is not null && segment.Line == hovered.Line && segment.Group == hovered.Group;
            var distance = sameGroup ? Math.Abs(segment.Index - hovered!.Index) : int.MaxValue;
            var lifted = distance == 0;
            var push = distance == 1 ? (segment.Index < hovered!.Index ? -8f : 8f) : 0f;
            var scale = lifted ? (segment.Unit ? 1.08f : 1.1f) : 1f;
            ElementCompositionPreview.SetIsTranslationEnabled(segment.Element, true);
            var visual = ElementCompositionPreview.GetElementVisual(segment.Element);
            var compositor = visual.Compositor;
            // 从左下角放大，字才像是从基线上长出来。
            visual.CenterPoint = new Vector3(0, (float)segment.Element.ActualHeight, 0);
            var delay = TimeSpan.FromMilliseconds(35 * (sameGroup ? Math.Min(distance, 4) : 0));
            var damping = lifted ? 0.72f : 0.86f;
            var period = lifted ? 150 : 220;
            visual.StartAnimation("Translation", Spring(compositor, new Vector3(push, lifted ? -10 : 0, 0), damping, period, delay));
            visual.StartAnimation("Scale", Spring(compositor, new Vector3(scale, scale, 1), damping, period, delay));
        }
    }

    private static SpringVector3NaturalMotionAnimation Spring(Compositor compositor, Vector3 target, float damping, int periodMs, TimeSpan delay)
    {
        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = target;
        spring.DampingRatio = damping;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);
        spring.DelayTime = delay;
        return spring;
    }
}
