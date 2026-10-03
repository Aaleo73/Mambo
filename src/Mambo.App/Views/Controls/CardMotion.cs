using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 卡片的上浮与按下：悬停或键盘聚焦上移 6px（220ms，fluid 缓动），按下回落到上移 2px（80ms）。
/// 原版按下时还有 .995 的缩放，300px 的卡片上不到 2px，这里不做。
/// </summary>
internal static class CardMotion
{
    private const float HoverLift = -6;
    private const float PressLift = -2;

    public static void Lift(UIElement element, bool up) => Move(element, up ? HoverLift : 0, TimeSpan.FromMilliseconds(220));

    public static void Press(UIElement element, bool pressed, bool hovering) =>
        Move(element, pressed ? PressLift : hovering ? HoverLift : 0, pressed ? Motion.Micro : TimeSpan.FromMilliseconds(220));

    private static void Move(UIElement element, float y, TimeSpan duration)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var target = new Vector3(0, y, 0);
        if (!Motion.AnimationsEnabled)
        {
            visual.Properties.InsertVector3("Translation", target);
            return;
        }
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, target, Motion.CreateEasing(compositor, Motion.Fluid));
        animation.Duration = duration;
        visual.StartAnimation("Translation", animation);
    }
}
