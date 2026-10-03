namespace Mambo.App.ViewModels;

/// <summary>重建分页页面时逐页恢复目标偏移；末页或失败停止在可到达处。</summary>
internal static class PagedScrollRestorer
{
    public static async Task RestoreAsync(double target, Func<double> extent, Action<double> scroll,
        Func<CancellationToken, Task<bool>> loadMore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extent);
        ArgumentNullException.ThrowIfNull(scroll);
        ArgumentNullException.ThrowIfNull(loadMore);
        if (!double.IsFinite(target) || target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = Math.Max(0, extent());
            scroll(Math.Min(target, available));
            if (available + 0.5 >= target) return;
            if (!await loadMore(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scroll(Math.Min(target, Math.Max(0, extent())));
                return;
            }
        }
    }
}
