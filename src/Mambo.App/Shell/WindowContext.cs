using Mambo.App.Themes;
using Microsoft.UI;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Shell;

/// <summary>页面需要窗口身份时使用（文件选择器等）；由 MainWindow 在创建后填写。</summary>
public sealed class WindowContext : IDisposable, IAsyncDisposable
{
    private WindowContrastSource? contrastSource;
    private WindowMotionSource? motionSource;
    private bool disposed;

    internal WindowContrastSource GetContrastSource(DispatcherQueue queue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(queue);
        if (!queue.HasThreadAccess) throw new InvalidOperationException("窗口主题必须在 UI 线程观察。");
        return contrastSource ??= new WindowContrastSource(WindowId, queue);
    }

    internal WindowMotionSource GetMotionSource(DispatcherQueue queue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(queue);
        if (!queue.HasThreadAccess) throw new InvalidOperationException("窗口动画设置必须在 UI 线程观察。");
        return motionSource ??= new WindowMotionSource(queue);
    }

    public void Dispose()
    {
        motionSource?.Dispose();
        contrastSource?.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        GC.SuppressFinalize(this);
        try
        {
            if (motionSource is not null) await motionSource.DisposeAsync();
        }
        finally
        {
            if (contrastSource is not null) await contrastSource.DisposeAsync();
        }
    }

    public WindowId WindowId { get; internal set; }
    public nint Handle { get; internal set; }
    public bool IsFullscreen { get; private set; }
    public bool IsMaximized { get; private set; }
    public event EventHandler? PresentationChanged;
    internal Action<bool>? FullscreenRequested { get; set; }
    internal Action? MaximizeRequested { get; set; }
    internal Action<bool>? PlaybackActiveRequested { get; set; }

    public void ToggleFullscreen() => FullscreenRequested?.Invoke(!IsFullscreen);
    public void ExitFullscreen() => FullscreenRequested?.Invoke(false);
    public void ToggleMaximize() => MaximizeRequested?.Invoke();
    public void SetPlaybackActive(bool active) => PlaybackActiveRequested?.Invoke(active);

    internal void SetPresentation(bool fullscreen, bool maximized)
    {
        if (IsFullscreen == fullscreen && IsMaximized == maximized) return;
        IsFullscreen = fullscreen;
        IsMaximized = maximized;
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>窗口处于前台；失焦或最小化时为 false（hero 轮播据此暂停）。</summary>
    public bool IsActive { get; private set; } = true;

    public event EventHandler? ActiveChanged;

    internal void SetActive(bool active)
    {
        if (active == IsActive) return;
        IsActive = active;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }
}
