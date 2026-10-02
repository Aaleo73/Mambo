using Mambo.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 横版卡：卡片行里固定 300×169；网格里由外部给宽度，图片保持 16:9。
/// 悬停时显示"从这里播放"。
/// </summary>
public sealed partial class LandscapeCard : CardBase
{
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
        UpdateArtHeight(IsAdaptive ? Layout.ActualWidth : Width);
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsAdaptive) UpdateArtHeight(e.NewSize.Width);
    }

    /// <summary>播放钮放在图片右下角（距右 10、距底 18），按图片高度直接定位。</summary>
    private void UpdateArtHeight(double width)
    {
        if (width <= 0) return;
        var height = Math.Round(width * 9 / 16);
        ArtRow.Height = new GridLength(height);
        PlayButton.Margin = new Thickness(0, height - 18 - PlayButton.Height, 10, 0);
    }

    private void OnClick(object sender, RoutedEventArgs e) => HandleClick();

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Item is { } item && CardActions.Current is { } actions) await actions.PlayAsync(item.Id);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        HandleHover(true, Art);
        CardMotion.Lift(PlayButton, true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 子元素的离开事件会冒泡上来；指针仍在卡片范围内（例如移到播放钮上）时不算离开。
        var point = e.GetCurrentPoint(Layout).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X < Layout.ActualWidth && point.Y < Layout.ActualHeight) return;
        HandleHover(false, Art);
        CardMotion.Lift(PlayButton, false);
    }
}
