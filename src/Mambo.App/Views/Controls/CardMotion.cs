using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Views.Controls;

/// <summary>Card feedback: hover/focus −4 DIP, press −2 DIP, release with Feedback/EaseOut. Rails keep 8 DIP of headroom above the cards.</summary>
internal static class CardMotion
{
    private const float HoverLift = -4;
    private const float PressLift = -2;

    public static void Lift(UIElement element, bool up) => Move(element, up ? HoverLift : 0, Motion.Feedback);

    public static void Press(UIElement element, bool pressed, bool hovering) =>
        Move(element, pressed ? PressLift : hovering ? HoverLift : 0, pressed ? Motion.Press : Motion.Feedback);

    public static void Reset(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded) return;
        visual.Properties.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private static void Move(UIElement element, float y, TimeSpan duration)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded)
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var target = new Vector3(0, y, 0);
        if (!Motion.AnimationsEnabled || !Motion.IsActive(element) || Motion.IsEntranceSuppressed(element))
        {
            Reset(element);
            return;
        }
        var compositor = visual.Compositor;
        using var animation = compositor.CreateVector3KeyFrameAnimation();
        using var easing = Motion.CreateEasing(compositor, Motion.EaseOut);
        animation.InsertKeyFrame(1, target, easing);
        animation.Duration = duration;
        visual.Properties.StartAnimation("Translation", animation);
    }
}
