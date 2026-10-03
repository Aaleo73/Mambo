using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 海报卡：右下角评分角标、进度条，标题与副标题。卡片行里固定 150×220；
/// 资料库网格里由外部给宽度，图片保持 150:220。
/// </summary>
public sealed partial class PosterCard : CardBase
{
    public static readonly DependencyProperty IsAdaptiveProperty = DependencyProperty.Register(nameof(IsAdaptive), typeof(bool), typeof(PosterCard),
        new PropertyMetadata(false, (d, _) => ((PosterCard)d).ApplySizing()));

    private bool hovering;

    public PosterCard()
    {
        InitializeComponent();
        TrackReveal(Picture, Copy, RatingTab, Progress);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        Root.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        Root.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnReleased), true);
    }

    public bool IsAdaptive { get => (bool)GetValue(IsAdaptiveProperty); set => SetValue(IsAdaptiveProperty, value); }

    private void ApplySizing()
    {
        var width = (double)Application.Current.Resources["PosterWidth"];
        Width = IsAdaptive ? double.NaN : width;
        Picture.DecodeWidth = IsAdaptive ? 0 : width;
        UpdateArtHeight(IsAdaptive ? double.NaN : width);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 网格布局会单独测量第一个条目来估算行高，所以图片高度必须在测量子树之前就按宽度定下来。
        if (IsAdaptive) UpdateArtHeight(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void UpdateArtHeight(double width)
    {
        var baseWidth = (double)Application.Current.Resources["PosterWidth"];
        var baseHeight = (double)Application.Current.Resources["PosterHeight"];
        var height = double.IsFinite(width) && width > 0 ? Math.Round(width * baseHeight / baseWidth) : baseHeight;
        if (ArtRow.Height.GridUnitType != GridUnitType.Pixel || ArtRow.Height.Value != height)
            ArtRow.Height = new GridLength(height);
    }

    private void OnClick(object sender, RoutedEventArgs e) => HandleClick(Art);

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        hovering = true;
        HandleHover(true, Visual);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X < Root.ActualWidth && point.Y < Root.ActualHeight) return;
        hovering = false;
        HandleHover(Root.FocusState == FocusState.Keyboard, Visual);
    }

    private void OnFocusChanged(object sender, RoutedEventArgs e) => HandleHover(hovering || Root.FocusState == FocusState.Keyboard, Visual);
    private void OnPressed(object sender, PointerRoutedEventArgs e) => HandlePress(true, Visual);
    private void OnReleased(object sender, PointerRoutedEventArgs e) => HandlePress(false, Visual);
}
