using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;

namespace Mambo.Core.BulletChat;

/// <summary>
/// 一次匹配的记忆。季级记忆保存位置偏移（EpisodeId 为空），同一季各集沿用；
/// 条目级记忆保存确切的 EpisodeId，用于电影、特别篇和手动指定。
/// </summary>
public sealed record BulletChatMemory(string AnimeId, string AnimeTitle, int Offset, bool Manual)
{
    public string? EpisodeId { get; init; }
    public string? EpisodeTitle { get; init; }
    public long UsedUtcTicks { get; init; }
}

public sealed record BulletChatHistoryDocument
{
    public int Version { get; init; } = 1;
    public Dictionary<string, BulletChatMemory> Entries { get; init; } = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(BulletChatHistoryDocument))]
public sealed partial class BulletChatHistoryJsonContext : JsonSerializerContext;

/// <summary>匹配记忆的本地存储。键里的账号范围是哈希，不含服务器地址或用户名。</summary>
public sealed class BulletChatHistory : IDisposable
{
    private const int Capacity = 2000;
    private readonly AppPaths paths;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, BulletChatMemory> entries;

    public BulletChatHistory(AppPaths paths, TimeProvider? clock = null)
    {
        this.paths = paths;
        this.clock = clock ?? TimeProvider.System;
        entries = Load(paths.BulletChatHistory);
    }

    public static string SeasonKey(string scope, string seriesId, int? season) => scope + "|season|" + seriesId + "|" + (season ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string ItemKey(string scope, string itemId) => scope + "|item|" + itemId;

    public BulletChatMemory? Find(string key)
    {
        lock (gate) return entries.GetValueOrDefault(key);
    }

    /// <summary>自动匹配的结果不会覆盖用户手动指定的记忆。</summary>
    public async Task RememberAsync(string key, BulletChatMemory memory, CancellationToken cancellationToken)
    {
        byte[] bytes;
        lock (gate)
        {
            if (!memory.Manual && entries.GetValueOrDefault(key) is { Manual: true }) return;
            var stamped = memory with { UsedUtcTicks = clock.GetUtcNow().UtcTicks };
            if (entries.GetValueOrDefault(key) is { } existing && existing with { UsedUtcTicks = 0 } == stamped with { UsedUtcTicks = 0 }) return;
            entries[key] = stamped;
            if (entries.Count > Capacity)
                foreach (var stale in entries.OrderBy(pair => pair.Value.UsedUtcTicks).Take(entries.Count - Capacity).Select(pair => pair.Key).ToArray())
                    entries.Remove(stale);
            bytes = JsonSerializer.SerializeToUtf8Bytes(new BulletChatHistoryDocument { Entries = new(entries, StringComparer.Ordinal) },
                BulletChatHistoryJsonContext.Default.BulletChatHistoryDocument);
        }
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicFile.WriteAsync(paths.BulletChatHistory, bytes, cancellationToken: cancellationToken).ConfigureAwait(false); }
        // 记忆只是省去下次搜索；写不进去不影响本次播放。
        catch (AppException) { }
        finally { writer.Release(); }
    }

    private static Dictionary<string, BulletChatMemory> Load(string path)
    {
        var result = new Dictionary<string, BulletChatMemory>(StringComparer.Ordinal);
        try
        {
            if (AtomicFile.Read(path) is not { } bytes) return result;
            var document = JsonSerializer.Deserialize(bytes, BulletChatHistoryJsonContext.Default.BulletChatHistoryDocument);
            if (document is not { Version: 1, Entries: not null }) return result;
            foreach (var (key, memory) in document.Entries)
                if (!string.IsNullOrWhiteSpace(key) && memory is { AnimeId.Length: > 0, AnimeTitle: not null } && Math.Abs((long)memory.Offset) < 10000)
                    result[key] = memory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { result.Clear(); }
        return result;
    }

    public void Dispose() => writer.Dispose();
}
