using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mambo.App.Themes;

namespace Mambo.App.Views.Controls;

/// <summary>
/// 海报卡与横版卡共用：Item 依赖属性、点击进详情、悬停或键盘聚焦时整张卡上浮、按下回落、300ms 预取，
/// 以及图片有了结果后同步显示文字和角标，不叠加另一层淡入。
/// </summary>
public partial class CardBase : UserControl, IMotionParticipant
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(MediaCardViewModel), typeof(CardBase),
        new PropertyMetadata(null, (d, _) => ((CardBase)d).SettleMotion()));

    private DispatcherQueueTimer? prefetch;
    private UIElement[] revealed = [];
    private bool active;
    private bool pressed;
    private UIElement? motionTarget;
    private int focusGeneration;
    protected bool Hovering { get; set; }
    protected virtual bool IsCardFocused() => false;
    internal bool IsMotionPressed => pressed;
    internal bool IsMotionHovered => Hovering;
    internal UIElement? MotionTarget => motionTarget;

    public CardBase()
    {
        Loaded += (_, _) => SettleMotion();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        SettleMotion();
        if (prefetch is null) return;
        prefetch.Stop();
        prefetch.Tick -= OnPrefetch;
        prefetch = null;
    }

    private void OnPrefetch(DispatcherQueueTimer sender, object args)
    {
        if (IsLoaded && Motion.IsActive(this) && Item is { } item) CardActions.Current?.Prefetch(item.Id);
    }

    public MediaCardViewModel? Item { get => (MediaCardViewModel?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    /// <summary>整卡只发起轻量导航，详情前景由 PageHost 统一呈现。</summary>
    protected void HandleClick()
    {
        if (Item is { } item) CardActions.Current?.Open(item.Id);
    }

    /// <summary>文字、角标、进度条跟随图片的 Pending/Settled 状态直接显示。</summary>
    protected void TrackReveal(RemoteImage picture, params UIElement[] targets)
    {
        ArgumentNullException.ThrowIfNull(picture);
        revealed = targets;
        SetRevealed(false);
        picture.Pending += (_, _) => SetRevealed(false);
        picture.Settled += (_, _) => SetRevealed(true);
    }

    private void SetRevealed(bool visible)
    {
        foreach (var target in revealed)
        {
            target.Opacity = visible ? 1 : 0;
        }
    }

    protected void TrackMotion(UIElement target) => motionTarget = target;
    protected void HandleCancel() => SettleMotion();

    void IMotionParticipant.SettleMotion() => SettleMotion();

    private void SettleMotion()
    {
        focusGeneration++;
        Hovering = pressed = false;
        active = IsCardFocused();
        prefetch?.Stop();
        if (motionTarget is null) return;
        CardMotion.Reset(motionTarget);
        VisualStateManager.GoToState(this, active ? "Hover" : "Rest", false);
    }

    protected void QueueFocusUpdate()
    {
        var generation = ++focusGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == focusGeneration && IsLoaded && Motion.IsActive(this))
                HandleHover(Hovering || IsCardFocused());
        });
    }

    /// <summary>Hover and actual keyboard focus share one card feedback root.</summary>
    protected void HandleHover(bool hover)
    {
        if (!IsLoaded || !Motion.IsActive(this)) { SettleMotion(); return; }
        active = hover;
        VisualStateManager.GoToState(this, hover ? "Hover" : "Rest", true);
        if (!pressed && motionTarget is not null) CardMotion.Lift(motionTarget, hover);
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

    protected void HandlePress(bool down)
    {
        if (!IsLoaded || !Motion.IsActive(this)) { SettleMotion(); return; }
        pressed = down;
        if (motionTarget is not null) CardMotion.Press(motionTarget, down, active);
    }
}
