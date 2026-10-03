using Mambo.App.Shell;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Themes;

/// <summary>页面级托管观察；卸载不触及原生窗口主题事件。</summary>
internal sealed class WindowContrastObserver : IDisposable
{
    private readonly WindowContrastSource source;
    private readonly Action<bool> changed;
    private bool disposed;

    public WindowContrastObserver(WindowContext window, DispatcherQueue queue, Action<bool> changed)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(changed);
        this.changed = changed;
        source = window.GetContrastSource(queue);
        source.Changed += OnChanged;
    }

    public bool HighContrast => source.HighContrast;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.Changed -= OnChanged;
    }

    private void OnChanged(bool highContrast) { if (!disposed) changed(highContrast); }
}
