using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;

namespace Mambo.Core.Images;

public sealed record ImageRequestKey(string Server, string ItemId, ImageKind Type, string? Tag,
    int MaxWidth, int MaxHeight = 0, int Index = 0, int Quality = 90)
{
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"{Server}|{ItemId}|{Type}|{Tag}|{MaxWidth}|{MaxHeight}|{Index}|{Quality}")))).ToLowerInvariant();
    public override string ToString() => $"ImageRequestKey {{ {Hash} }}";
}

public sealed record ImageCacheLimits(long MemoryBytes = 64 * 1024 * 1024, int MaxEntryBytes = 16 * 1024 * 1024,
    long DiskBytes = 512 * 1024 * 1024, long DiskCheckBytes = 32 * 1024 * 1024);

/// <summary>Compressed-byte LRU and a versioned, tag-only disk cache.</summary>
public sealed class ImageByteCache : IDisposable, IAsyncDisposable
{
    private static ReadOnlySpan<byte> Magic => "MAMBOIMG"u8;
    private const int HeaderSize = 16;
    private readonly string directory;
    private readonly TimeProvider clock;
    private readonly ImageCacheLimits limits;
    private readonly object gate = new();
    private readonly SemaphoreSlim diskGate = new(1, 1);
    private readonly Dictionary<string, LinkedListNode<MemoryEntry>> memory = new(StringComparer.Ordinal);
    private readonly LinkedList<MemoryEntry> lru = new();
    private long memoryBytes;
    private long writtenSinceCheck;
    private int generation;
    private bool disposed;
    private Task? cleanupTask;

    public ImageByteCache(AppPaths paths, TimeProvider? timeProvider = null, ImageCacheLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        directory = paths.Images;
        clock = timeProvider ?? TimeProvider.System;
        this.limits = limits ?? new();
        if (this.limits.MemoryBytes < 0 || this.limits.DiskBytes < 0 || this.limits.MaxEntryBytes <= 0 || this.limits.DiskCheckBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public int Generation => Volatile.Read(ref generation);
    public int MaxEntryBytes => limits.MaxEntryBytes;
    public long MemoryBytes { get { lock (gate) return memoryBytes; } }

    public async Task<ReadOnlyMemory<byte>?> TryGetAsync(ImageRequestKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (memory.TryGetValue(key.Hash, out var node))
            {
                lru.Remove(node);
                lru.AddFirst(node);
                return node.Value.Bytes;
            }
        }
        if (string.IsNullOrEmpty(key.Tag)) return null;
        await diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            byte[] data;
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                if (file.Length < HeaderSize || file.Length > (long)limits.MaxEntryBytes + HeaderSize) return null;
                data = new byte[(int)file.Length];
                await file.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            }
            if (data.Length < HeaderSize || data.Length > limits.MaxEntryBytes + HeaderSize ||
                !data.AsSpan(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)) != 1 ||
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)) != data.Length - HeaderSize)
            {
                File.Delete(path);
                return null;
            }
            var bytes = data.AsSpan(HeaderSize).ToArray();
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
            Remember(key.Hash, bytes);
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        finally { diskGate.Release(); }
    }

    public async Task StoreAsync(ImageRequestKey key, ReadOnlyMemory<byte> bytes,
        int? expectedGeneration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        if (bytes.Length == 0 || bytes.Length > limits.MaxEntryBytes) return;
        await diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedGeneration is not null && expectedGeneration != Generation) return;
            Remember(key.Hash, bytes.ToArray());
            if (string.IsNullOrEmpty(key.Tag) || limits.DiskBytes == 0) return;
            var data = new byte[HeaderSize + bytes.Length];
            Magic.CopyTo(data);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), bytes.Length);
            bytes.CopyTo(data.AsMemory(HeaderSize));
            await AtomicFile.WriteAsync(PathFor(key), data, cancellationToken: cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(PathFor(key), clock.GetUtcNow().UtcDateTime);
            writtenSinceCheck += data.Length;
            if (writtenSinceCheck >= limits.DiskCheckBytes)
            {
                writtenSinceCheck = 0;
                TrimDisk();
            }
        }
        catch (AppException error) when (error.Error.Kind == AppErrorKind.Persistence) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { diskGate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        await diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref generation);
            lock (gate) { memory.Clear(); lru.Clear(); memoryBytes = 0; }
            foreach (var file in Directory.EnumerateFiles(directory, "*.img", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(file);
            }
            writtenSinceCheck = 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AppException(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed, "无法清除图片缓存，请检查磁盘权限。", true)); }
        finally { diskGate.Release(); }
    }

    private void Remember(string hash, byte[] bytes)
    {
        lock (gate)
        {
            if (bytes.Length > limits.MemoryBytes) return;
            if (memory.Remove(hash, out var previous)) { lru.Remove(previous); memoryBytes -= previous.Value.Bytes.Length; }
            var node = lru.AddFirst(new MemoryEntry(hash, bytes));
            memory.Add(hash, node);
            memoryBytes += bytes.Length;
            while (memoryBytes > limits.MemoryBytes && lru.Last is { } last)
            {
                memory.Remove(last.Value.Hash);
                lru.RemoveLast();
                memoryBytes -= last.Value.Bytes.Length;
            }
        }
    }

    private string PathFor(ImageRequestKey key) => Path.Combine(directory, key.Hash[..2], key.Hash + ".img");

    private void TrimDisk()
    {
        var files = Directory.EnumerateFiles(directory, "*.img", SearchOption.AllDirectories).Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.Ordinal).ToArray();
        var total = files.Sum(file => file.Length);
        if (total <= limits.DiskBytes) return;
        var target = (long)(limits.DiskBytes * 0.9);
        foreach (var file in files)
        {
            var length = file.Length;
            file.Delete();
            total -= length;
            if (total <= target) break;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cleanupTask = CleanupAsync();
        }
    }

    private async Task CleanupAsync()
    {
        await diskGate.WaitAsync().ConfigureAwait(false);
        diskGate.Release();
        diskGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task cleanup;
        lock (gate) cleanup = cleanupTask!;
        await cleanup.ConfigureAwait(false);
    }
    private sealed record MemoryEntry(string Hash, byte[] Bytes);
}
