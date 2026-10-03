using Mambo.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 横版卡：卡片行里固定 300×169；网格里由外部给宽度，图片保持 16:9。
/// 悬停时显示"从这里播放"。
/// </summary>
public sealed partial class LandscapeCard : CardBase
{
    private bool hovering;
    public static readonly DependencyProperty ShowPlayButtonProperty = DependencyProperty.Register(nameof(ShowPlayButton), typeof(bool), typeof(LandscapeCard),
        new PropertyMetadata(true));
    public static readonly DependencyProperty IsAdaptiveProperty = DependencyProperty.Register(nameof(IsAdaptive), typeof(bool), typeof(LandscapeCard),
        new PropertyMetadata(false, (d, _) => ((LandscapeCard)d).ApplySizing()));

    public LandscapeCard()
    {
        InitializeComponent();
        ApplySizing();
    }

    public bool ShowPlayButton { get => (bool)GetValue(ShowPlayButtonProperty); set => SetValue(ShowPlayButtonProperty, value); }
    public bool IsAdaptive { get => (bool)GetValue(IsAdaptiveProperty); set => SetValue(IsAdaptiveProperty, value); }

    private void ApplySizing()
    {
        Width = IsAdaptive ? double.NaN : (double)Application.Current.Resources["LandscapeWidth"];
        Picture.DecodeWidth = IsAdaptive ? 0 : Width;
        UpdateArtHeight(IsAdaptive ? double.NaN : Width);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // UniformGridLayout 会在深滚动时单独创建、测量、回收第一个条目来估算行高。
        // 必须在测量子树前确定图片高度；等 SizeChanged/Arrange 后再写会反复改变估算。
        if (IsAdaptive) UpdateArtHeight(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    /// <summary>播放钮放在图片右下角（距右 10、距底 18），按图片高度直接定位。</summary>
    private void UpdateArtHeight(double width)
    {
        var height = double.IsFinite(width) && width > 0 ? Math.Round(width * (9d / 16))
            : (double)Application.Current.Resources["LandscapeHeight"];
        if (ArtRow.Height.GridUnitType != GridUnitType.Pixel || ArtRow.Height.Value != height)
            ArtRow.Height = new GridLength(height);
        var margin = new Thickness(0, height - 18 - PlayButton.Height, 10, 0);
        if (PlayButton.Margin != margin) PlayButton.Margin = margin;
    }

    private void OnClick(object sender, RoutedEventArgs e) => HandleClick(Picture);

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Item is { } item && CardActions.Current is { } actions) await actions.PlayAsync(item.Id);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        hovering = true;
        HandleHover(true, Art);
        CardMotion.Lift(PlayButton, true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 子元素的离开事件会冒泡上来；指针仍在卡片范围内（例如移到播放钮上）时不算离开。
        var point = e.GetCurrentPoint(Layout).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X < Layout.ActualWidth && point.Y < Layout.ActualHeight) return;
        hovering = false;
        HandleHover(IsCardFocused(), Art);
        CardMotion.Lift(PlayButton, false);
    }

    private bool IsCardFocused() => Root.FocusState != FocusState.Unfocused || PlayButton.FocusState != FocusState.Unfocused;
    private void OnFocusChanged(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => HandleHover(hovering || IsCardFocused(), Art));
}
