using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Mambo.App.Images;

/// <summary>
/// 从 IImageService 取压缩字节，按显示尺寸 × XamlRoot 缩放率的物理像素解码成 BitmapImage；
/// 解码结果以弱引用缓存，回到页面时不必重新解码。只在 UI 线程使用。
/// </summary>
public sealed class ImageLoader : IDisposable
{
    private readonly IImageService images;
    private readonly DecodedImageCache<BitmapImage> decoded = new();
    private long fetchStartedCount;
    private long decodedHitCount;
    private int activeFetchCount;

    public ImageLoader(IImageService images)
    {
        this.images = images;
        Current = this;
    }

    /// <summary>XAML 中直接声明的 RemoteImage 无法注入服务，经此取得。</summary>
    public static ImageLoader? Current { get; internal set; }

    internal long FetchStartedCount => Interlocked.Read(ref fetchStartedCount);
    internal long DecodedHitCount => Interlocked.Read(ref decodedHitCount);
    internal int ActiveFetchCount => Volatile.Read(ref activeFetchCount);

    /// <summary>缓存键使用物理像素宽度，避免不同 DPI 下复用低分辨率位图。</summary>
    public BitmapImage? TryGetDecoded(ImageRef image, int pixelWidth)
    {
        var value = decoded.Get(image, pixelWidth);
        if (value is not null) Interlocked.Increment(ref decodedHitCount);
        return value;
    }

    /// <summary>账号变化时在清理旧页面前调用；清缓存也可调用。</summary>
    public void ClearDecodedCache() => decoded.Clear();

    public void Dispose()
    {
        decoded.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    public async Task<BitmapImage?> LoadAsync(ImageRef image, double dipWidth, double scale, ImagePriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, decoded.Token);
        var generation = decoded.Generation;
        var token = scope.Token;
        token.ThrowIfCancellationRequested();
        var pixelWidth = GetPixelWidth(dipWidth, scale);
        if (pixelWidth == 0) return null;
        if (TryGetDecoded(image, pixelWidth) is { } hit) return hit;
        using var stream = await FetchStreamAsync(image, pixelWidth, priority, token);
        if (stream is null) return null;
        // 位图从流创建时尚未挂到 XamlRoot；显式使用物理像素，不依赖隐式的 DPI 推断。
        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = pixelWidth };
        try { await bitmap.SetSourceAsync(stream); }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
        token.ThrowIfCancellationRequested();
        if (!decoded.Set(image, pixelWidth, bitmap, generation)) return null;
        return bitmap;
    }

    internal static int GetPixelWidth(double dipWidth, double scale) =>
        double.IsFinite(dipWidth) && dipWidth > 0 && double.IsFinite(scale) && scale > 0
            ? (int)Math.Min(int.MaxValue, Math.Ceiling(dipWidth * scale)) : 0;

    /// <summary>取图失败（缺图、网络）返回 null，占位保持不变；主动取消照常抛出。</summary>
    public async Task<IRandomAccessStream?> FetchStreamAsync(ImageRef image, int pixelWidth, ImagePriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, decoded.Token);
        var token = scope.Token;
        ReadOnlyMemory<byte> bytes;
        Interlocked.Increment(ref fetchStartedCount);
        Interlocked.Increment(ref activeFetchCount);
        try
        {
            bytes = await images.FetchAsync(image, Math.Max(1, pixelWidth), priority, token);
        }
        catch (AppException)
        {
            return null;
        }
        finally { Interlocked.Decrement(ref activeFetchCount); }
        var buffer = MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Array is not null
            ? segment.Array.AsBuffer(segment.Offset, segment.Count)
            : bytes.ToArray().AsBuffer();
        var stream = new InMemoryRandomAccessStream();
        try
        {
            await stream.WriteAsync(buffer).AsTask(token);
            token.ThrowIfCancellationRequested();
            stream.Seek(0);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
