using Mambo.App.Shell;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Themes;

/// <summary>动画 owner 的托管观察；释放不触及原生窗口订阅。</summary>
internal sealed class WindowMotionObserver : IDisposable
{
    private readonly WindowMotionSource source;
    private readonly Action<bool> changed;
    private bool disposed;

    public WindowMotionObserver(WindowContext window, DispatcherQueue queue, Action<bool> changed)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(changed);
        this.changed = changed;
        source = window.GetMotionSource(queue);
        source.Changed += OnChanged;
    }

    public bool AnimationsEnabled => source.AnimationsEnabled;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.Changed -= OnChanged;
    }

    private void OnChanged(bool enabled) { if (!disposed) changed(enabled); }
}
