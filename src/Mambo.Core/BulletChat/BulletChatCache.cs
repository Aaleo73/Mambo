using System.Security.Cryptography;
using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;

namespace Mambo.Core.BulletChat;

/// <summary>弹幕接口响应的磁盘缓存，减少对弹幕服务器的请求。读写失败只当作未命中。</summary>
public sealed class BulletChatCache(AppPaths paths, TimeProvider? clock = null)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private int pruned;

    public byte[]? TryRead(string key, TimeSpan lifetime)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var age = clock.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(path);
            return age < TimeSpan.Zero || age >= lifetime ? null : AtomicFile.Read(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            var path = PathFor(key);
            await AtomicFile.WriteAsync(path, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            // 以注入的时钟记录写入时间，过期判断与测试时钟一致。
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
            if (Interlocked.Exchange(ref pruned, 1) == 0) Prune();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException) { }
    }

    public void Clear()
    {
        try { foreach (var file in Directory.EnumerateFiles(paths.BulletChatCache, "*.json")) Delete(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // 每个进程清一次早已过期的文件，避免缓存目录只增不减。
    private void Prune()
    {
        try
        {
            var limit = clock.GetUtcNow().UtcDateTime - Retention;
            foreach (var file in Directory.EnumerateFiles(paths.BulletChatCache, "*.json"))
                if (File.GetLastWriteTimeUtc(file) < limit) Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void Delete(string file)
    {
        try { File.Delete(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // 文件名只含键的哈希，磁盘上看不出搜索过的片名。
    private string PathFor(string key) => Path.Combine(paths.BulletChatCache,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 16).ToLowerInvariant() + ".json");
}
