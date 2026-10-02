using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Views.Controls;

/// <summary>卡片悬停上浮 4px（220ms，fluid 缓动），离开时回落。</summary>
internal static class CardMotion
{
    public static void Lift(UIElement element, bool up)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var target = new Vector3(0, up ? -4 : 0, 0);
        if (!Motion.AnimationsEnabled)
        {
            visual.Properties.InsertVector3("Translation", target);
            return;
        }
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, target, Motion.CreateEasing(compositor, Motion.Fluid));
        animation.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Translation", animation);
    }
}
