using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Views.Controls;

/// <summary>页头：眉标、标题；右侧放操作，最右是"N 项"计数胶囊，数字变化时轻轻翻一下。</summary>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(nameof(Count), typeof(string), typeof(PageHeader), new PropertyMetadata("", OnCountChanged));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));
    public static readonly DependencyProperty HasCountProperty = DependencyProperty.Register(nameof(HasCount), typeof(bool), typeof(PageHeader), new PropertyMetadata(false));

    public PageHeader() => InitializeComponent();

    public string Eyebrow { get => (string)GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Count { get => (string)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public bool HasCount { get => (bool)GetValue(HasCountProperty); private set => SetValue(HasCountProperty, value); }

    private static void OnCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var header = (PageHeader)d;
        var had = header.HasCount;
        header.HasCount = !string.IsNullOrEmpty(e.NewValue as string);
        if (had && header.HasCount) header.Flip();
    }

    /// <summary>计数变化：新数字从下方 6px 淡入（280ms）。</summary>
    private void Flip()
    {
        if (!Motion.AnimationsEnabled || !CountLabel.IsLoaded) return;
        ElementCompositionPreview.SetIsTranslationEnabled(CountLabel, true);
        var visual = ElementCompositionPreview.GetElementVisual(CountLabel);
        var compositor = visual.Compositor;
        var easing = Motion.CreateEasing(compositor, Motion.Fluid);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = Motion.Route;
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, 6, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, easing);
        rise.Duration = Motion.Route;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", rise);
    }
}
