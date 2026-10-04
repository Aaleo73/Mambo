using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Mambo.App.Images;

/// <summary>
/// 从 IImageService 取压缩字节，按显示尺寸（DIP）解码成 BitmapImage；
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

    public BitmapImage? TryGetDecoded(ImageRef image, int decodeWidth)
    {
        var value = decoded.Get(image, decodeWidth);
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
        var decodeWidth = Math.Max(1, (int)Math.Ceiling(dipWidth));
        if (TryGetDecoded(image, decodeWidth) is { } hit) return hit;
        using var stream = await FetchStreamAsync(image, (int)Math.Ceiling(dipWidth * scale), priority, token);
        if (stream is null) return null;
        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = decodeWidth };
        try { await bitmap.SetSourceAsync(stream); }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
        token.ThrowIfCancellationRequested();
        if (!decoded.Set(image, decodeWidth, bitmap, generation)) return null;
        return bitmap;
    }

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
