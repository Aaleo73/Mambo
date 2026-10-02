using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views.Controls;

/// <summary>海报卡与横版卡共用：Item 依赖属性、点击进详情、悬停上浮与 300ms 预取。</summary>
public partial class CardBase : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(MediaCardViewModel), typeof(CardBase),
        new PropertyMetadata(null));

    private DispatcherQueueTimer? prefetch;

    public MediaCardViewModel? Item { get => (MediaCardViewModel?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    protected void HandleClick()
    {
        if (Item is { } item) CardActions.Current?.Open(item.Id);
    }

    protected void HandleHover(bool hover, UIElement lifted)
    {
        VisualStateManager.GoToState(this, hover ? "Hover" : "Rest", true);
        CardMotion.Lift(lifted, hover);
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
            prefetch.Tick += (_, _) => { if (Item is { } item) CardActions.Current?.Prefetch(item.Id); };
        }
        prefetch.Start();
    }
}
