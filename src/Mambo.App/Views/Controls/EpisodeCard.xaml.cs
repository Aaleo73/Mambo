using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 详情页剧集行里的一集：300×169 剧照、进度条、选中环，右下角常显播放钮。
/// 点卡片是选中这一集，点播放钮才开始播放。
/// </summary>
public sealed partial class EpisodeCard : UserControl
{
    public static readonly DependencyProperty EpisodeProperty = DependencyProperty.Register(nameof(Episode), typeof(DetailEpisodeViewModel), typeof(EpisodeCard),
        new PropertyMetadata(null));

    private bool hovering;
    private bool pressed;
    private bool active;

    public EpisodeCard()
    {
        InitializeComponent();
        SetRevealed(false, animate: false);
        Picture.Pending += (_, _) => SetRevealed(false, animate: false);
        Picture.Settled += (_, animate) => SetRevealed(true, animate);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        Root.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        Root.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnReleased), true);
    }

    public DetailEpisodeViewModel? Episode { get => (DetailEpisodeViewModel?)GetValue(EpisodeProperty); set => SetValue(EpisodeProperty, value); }

    /// <summary>点了卡片本身：选中这一集。</summary>
    public event RoutedEventHandler? Click;
    /// <summary>点了播放钮：播放这一集。</summary>
    public event RoutedEventHandler? PlayClick;

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
    private void OnPlayClick(object sender, RoutedEventArgs e) => PlayClick?.Invoke(this, e);

    /// <summary>文字和进度条等剧照有了结果再一起出现。</summary>
    private void SetRevealed(bool visible, bool animate)
    {
        foreach (var target in new UIElement[] { Copy, Progress })
        {
            target.OpacityTransition = animate && Motion.AnimationsEnabled ? new ScalarTransition { Duration = Motion.ImageReady } : null;
            target.Opacity = visible ? 1 : 0;
        }
    }

    private void SetActive(bool value)
    {
        active = value;
        VisualStateManager.GoToState(this, value ? "Hover" : "Rest", true);
        if (pressed) return;
        CardMotion.Lift(Visual, value);
        CardMotion.Lift(PlayButton, value);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        hovering = true;
        SetActive(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 子元素的离开事件会冒泡上来；指针仍在卡片范围内（例如移到播放钮上）时不算离开。
        var point = e.GetCurrentPoint(Layout).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X < Layout.ActualWidth && point.Y < Layout.ActualHeight) return;
        hovering = false;
        SetActive(IsCardFocused());
    }

    private bool IsCardFocused() => Root.FocusState == FocusState.Keyboard || PlayButton.FocusState == FocusState.Keyboard;
    private void OnFocusChanged(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => SetActive(hovering || IsCardFocused()));

    private void OnPressed(object sender, PointerRoutedEventArgs e) => Press(true);
    private void OnReleased(object sender, PointerRoutedEventArgs e) => Press(false);

    private void Press(bool down)
    {
        pressed = down;
        CardMotion.Press(Visual, down, active);
        CardMotion.Press(PlayButton, down, active);
    }
}
