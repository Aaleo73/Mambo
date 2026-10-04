using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Views.Controls;

/// <summary>A whole Hero foreground; its owner animates the panel, never its children.</summary>
public sealed partial class HeroInfoPanel : UserControl
{
    private ImageSource? logo;
    private bool highContrast;
    private bool presented;

    public HeroInfoPanel()
    {
        InitializeComponent();
        SoftShadow.AttachDrop(TitleShadow, TitleText, 10, 2, 0.65f);
        SoftShadow.AttachDrop(MetaShadow, MetaRow, 3, 1, 0.65f);
        SoftShadow.AttachDrop(OverviewShadow, OverviewText, 4, 1, 0.6f);
        Loaded += (_, _) => ApplyAccessibility(this, presented);
    }

    internal HeroSlideViewModel? Slide { get; private set; }
    internal void Show(HeroSlideViewModel slide, ImageSource? image, bool contrast)
    {
        Slide = slide;
        highContrast = contrast;
        logo = image;
        TitleText.Text = slide.Title;
        OverviewText.Text = slide.Overview;
        MetaArea.Visibility = HeroArt.BuildMeta(MetaRow, slide.RatingText, slide.Year, slide.Genres, slide.OfficialRating)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyLogo();
    }

    internal void SetPresented(bool active)
    {
        presented = active;
        ApplyAccessibility(this, active);
    }

    private static void ApplyAccessibility(DependencyObject element, bool active)
    {
        AutomationProperties.SetAccessibilityView(element, active ? AccessibilityView.Content : AccessibilityView.Raw);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            ApplyAccessibility(VisualTreeHelper.GetChild(element, i), active);
    }

    internal void SetLogo(ImageSource? image) { logo = image; ApplyLogo(); }
    internal void SetContrast(bool contrast)
    {
        highContrast = contrast;
        if (contrast) logo = null;
        ApplyLogo();
    }
    private void ApplyLogo()
    {
        LogoImage.Source = highContrast ? null : logo;
        LogoImage.Visibility = !highContrast && logo is not null ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = highContrast || logo is null ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void Clear()
    {
        Slide = null;
        logo = null;
        LogoImage.Source = null;
        TitleText.Text = "";
        OverviewText.Text = "";
        MetaRow.Children.Clear();
    }
}
