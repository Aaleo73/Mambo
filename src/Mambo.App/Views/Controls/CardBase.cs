using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Mambo.App.Themes;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 海报卡与横版卡共用：Item 依赖属性、点击进详情、悬停或键盘聚焦时整张卡上浮、按下回落、300ms 预取，
/// 以及"图片有了结果后文字和角标再一起淡入"。
/// </summary>
public partial class CardBase : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(MediaCardViewModel), typeof(CardBase),
        new PropertyMetadata(null));

    private DispatcherQueueTimer? prefetch;
    private UIElement[] revealed = [];
    private bool active;
    private bool pressed;

    public CardBase() => Unloaded += OnUnloaded;

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (prefetch is null) return;
        prefetch.Stop();
        prefetch.Tick -= OnPrefetch;
        prefetch = null;
    }

    private void OnPrefetch(DispatcherQueueTimer sender, object args)
    {
        if (Item is { } item) CardActions.Current?.Prefetch(item.Id);
    }

    public MediaCardViewModel? Item { get => (MediaCardViewModel?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    /// <summary>进详情：被点的封面留在原位放大淡出，盖在换入的详情页上。</summary>
    protected void HandleClick(FrameworkElement cover, RemoteImage picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        CoverTransition.Play(cover, picture.CurrentImage);
        if (Item is { } item) CardActions.Current?.Open(item.Id);
    }

    /// <summary>文字、角标、进度条在图片有结果之前保持隐藏，之后一起出现；图已在缓存里时不做淡入。</summary>
    protected void TrackReveal(RemoteImage picture, params UIElement[] targets)
    {
        ArgumentNullException.ThrowIfNull(picture);
        revealed = targets;
        SetRevealed(false, animate: false);
        picture.Pending += (_, _) => SetRevealed(false, animate: false);
        picture.Settled += (_, animate) => SetRevealed(true, animate);
    }

    private void SetRevealed(bool visible, bool animate)
    {
        foreach (var target in revealed)
        {
            target.OpacityTransition = animate && Motion.AnimationsEnabled ? new ScalarTransition { Duration = Motion.ImageReady } : null;
            target.Opacity = visible ? 1 : 0;
        }
    }

    /// <summary>悬停或键盘聚焦：描边加深，<paramref name="lifted"/> 上浮；离开时回落。</summary>
    protected void HandleHover(bool hover, params UIElement[] lifted)
    {
        ArgumentNullException.ThrowIfNull(lifted);
        active = hover;
        VisualStateManager.GoToState(this, hover ? "Hover" : "Rest", true);
        if (!pressed) foreach (var element in lifted) CardMotion.Lift(element, hover);
        if (!hover)
        {
            prefetch?.Stop();
            return;
        }
        if (prefetch is null)
        {
            prefetch = DispatcherQueue.CreateTimer();
            prefetch.Interval = TimeSpan.FromMilliseconds(300);
            prefetch.IsRepeating = false;
            prefetch.Tick += OnPrefetch;
        }
        prefetch.Start();
    }

    protected void HandlePress(bool down, params FrameworkElement[] lifted)
    {
        ArgumentNullException.ThrowIfNull(lifted);
        pressed = down;
        foreach (var element in lifted) CardMotion.Press(element, down, active);
    }
}
