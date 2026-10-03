using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;

namespace Mambo.App.Themes;

/// <summary>
/// 当前窗口的一次原生主题订阅。由 WindowContext 强引用，并在服务容器释放时、
/// 最终 Window.Close 之前于 UI 线程退订；页面卸载仅退订托管观察。
/// </summary>
internal sealed class WindowContrastSource : IDisposable, IAsyncDisposable
{
    private readonly ThemeSettings settings;
    private readonly DispatcherQueue queue;
    private readonly object closeGate = new();
    private Task? closing;
    private bool disposed;

    public WindowContrastSource(WindowId windowId, DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (!queue.HasThreadAccess) throw new InvalidOperationException("窗口主题必须在 UI 线程初始化。");
        this.queue = queue;
        settings = ThemeSettings.CreateForWindowId(windowId);
        HighContrast = settings.HighContrast;
        settings.Changed += OnChanged;
    }

    public bool HighContrast { get; private set; }
    public event Action<bool>? Changed;

    public void Dispose()
    {
        if (!queue.HasThreadAccess) throw new InvalidOperationException("窗口主题必须在 UI 线程释放。");
        lock (closeGate)
        {
            DisposeCore();
            closing ??= Task.CompletedTask;
        }
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (closeGate)
        {
            if (closing is not null) return new ValueTask(closing);
            if (queue.HasThreadAccess)
            {
                DisposeCore();
                closing = Task.CompletedTask;
                return ValueTask.CompletedTask;
            }
            // DI 的异步释放可能在其它异步服务之后转到线程池；等待原 UI 队列完成退订，
            // 服务容器返回之后 MainWindow 才允许最终关闭，不能只 fire-and-forget。
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closing = completion.Task;
            if (!queue.TryEnqueue(() =>
            {
                try { DisposeCore(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            })) completion.TrySetException(new InvalidOperationException("窗口主题释放无法返回 UI 线程。"));
            return new ValueTask(closing);
        }
    }

    private void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        Changed = null;
        // 仅撤销我们自己的订阅，不 Dispose 可能被 WinRT 工厂共享的 ThemeSettings 包装器。
        settings.Changed -= OnChanged;
    }

    private void OnChanged(ThemeSettings sender, object args) => queue.TryEnqueue(() =>
    {
        if (disposed) return;
        HighContrast = settings.HighContrast;
        Changed?.Invoke(HighContrast);
    });
}
