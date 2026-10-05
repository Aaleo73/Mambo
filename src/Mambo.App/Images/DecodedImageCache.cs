using Mambo.Core.Contracts;

namespace Mambo.App.Images;

/// <summary>以物理像素宽度区分的 UI 线程弱引用缓存；换账号或清缓存会取消旧代读取并拒绝旧代回填。</summary>
internal sealed class DecodedImageCache<T>(int capacity = 512) : IDisposable where T : class
{
    private readonly Dictionary<(ImageRef Image, int Width), Entry> items = [];
    private CancellationTokenSource lifetime = new();
    private long order;
    private bool disposed;
    public int Generation { get; private set; }
    public CancellationToken Token => lifetime.Token;
    public int Count => items.Count;

    public T? Get(ImageRef image, int width)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!items.TryGetValue((image, width), out var entry)) return null;
        if (!entry.Image.TryGetTarget(out var value)) { items.Remove((image, width)); return null; }
        entry.Order = ++order;
        return value;
    }

    public bool Set(ImageRef image, int width, T value, int generation)
    {
        if (disposed || generation != Generation || capacity <= 0) return false;
        items[(image, width)] = new(new WeakReference<T>(value), ++order);
        while (items.Count > capacity) items.Remove(items.MinBy(pair => pair.Value.Order).Key);
        return true;
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var previous = lifetime;
        lifetime = new CancellationTokenSource();
        Generation++;
        items.Clear();
        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        items.Clear();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private sealed class Entry(WeakReference<T> image, long entryOrder)
    {
        public WeakReference<T> Image { get; } = image;
        public long Order { get; set; } = entryOrder;
    }
}
