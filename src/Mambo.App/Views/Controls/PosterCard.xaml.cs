using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Mambo.App.Views.Controls;

/// <summary>海报卡 150×220：评分徽标、已看标记、进度条，标题与副标题。</summary>
public sealed partial class PosterCard : CardBase
{
    public PosterCard() => InitializeComponent();

    private void OnClick(object sender, RoutedEventArgs e) => HandleClick(Art);
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => HandleHover(true, Art);
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X < Root.ActualWidth && point.Y < Root.ActualHeight) return;
        HandleHover(false, Art);
    }
}
