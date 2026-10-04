using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace Mambo.App.Themes;

/// <summary>每个窗口仅一次原生订阅；最终关窗前等待 UI 线程完成退订。</summary>
internal sealed class WindowMotionSource : IDisposable, IAsyncDisposable
{
    private readonly UISettings settings;
    private readonly DispatcherQueue queue;
    private readonly object closeGate = new();
    private Task? closing;
    private bool disposed;

    public WindowMotionSource(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (!queue.HasThreadAccess) throw new InvalidOperationException("窗口动画设置必须在 UI 线程初始化。");
        this.queue = queue;
        settings = new UISettings();
        AnimationsEnabled = settings.AnimationsEnabled;
        settings.AnimationsEnabledChanged += OnChanged;
    }

    public bool AnimationsEnabled { get; private set; }
    public event Action<bool>? Changed;

    public void Dispose()
    {
        if (!queue.HasThreadAccess) throw new InvalidOperationException("窗口动画设置必须在 UI 线程释放。");
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
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closing = completion.Task;
            if (!queue.TryEnqueue(() =>
            {
                try { DisposeCore(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            })) completion.TrySetException(new InvalidOperationException("窗口动画设置释放无法返回 UI 线程。"));
            return new ValueTask(closing);
        }
    }

    private void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        Changed = null;
        settings.AnimationsEnabledChanged -= OnChanged;
    }

    private void OnChanged(UISettings sender, object args) => queue.TryEnqueue(() =>
    {
        if (disposed) return;
        AnimationsEnabled = settings.AnimationsEnabled;
        Changed?.Invoke(AnimationsEnabled);
    });
}
