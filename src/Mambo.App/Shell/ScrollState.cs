using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Shell;

/// <summary>等内容布局出足够高度后恢复滚动位置，内容不够时停在能到达的最远处。</summary>
public static class ScrollState
{
    public static void Restore(ScrollViewer scroller, double offset)
    {
        ArgumentNullException.ThrowIfNull(scroller);
        if (offset <= 0) return;
        var attempts = 0;
        void Apply(object? sender, object e)
        {
            attempts++;
            if (scroller.ScrollableHeight + 0.5 >= offset || attempts > 20)
            {
                scroller.LayoutUpdated -= Apply;
                scroller.ChangeView(null, Math.Min(offset, scroller.ScrollableHeight), null, true);
            }
        }
        scroller.LayoutUpdated += Apply;
    }
}
