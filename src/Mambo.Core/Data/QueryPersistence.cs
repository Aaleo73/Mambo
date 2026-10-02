using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;

namespace Mambo.Core.Data;

public sealed record QuerySnapshot
{
    public string Kind { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public ImmutableArray<MediaLibrary> Libraries { get; init; } = [];
    public ImmutableArray<MediaItem> Items { get; init; } = [];
    public int? TotalCount { get; init; }
    public bool HasMore { get; init; }
    public int? NextOffset { get; init; }
}
public sealed record QueryDocument(int Version, Dictionary<string, QuerySnapshot> Values);
[JsonSerializable(typeof(QueryDocument))]
internal sealed partial class QueryJsonContext : JsonSerializerContext;

public sealed class QueryPersistence(AppPaths paths, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private string PathFor(string scope)
    {
        if (scope.Length != 64 || scope.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("缓存作用域无效。", nameof(scope));
        return Path.Combine(paths.QueryCache, scope + ".json");
    }
    public Dictionary<string, QuerySnapshot> Load(string scope)
    {
        try
        {
            var path = PathFor(scope);
            if (clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7)) return new(StringComparer.Ordinal);
            var bytes = AtomicFile.Read(path);
            var document = bytes is null ? null : JsonSerializer.Deserialize(bytes, QueryJsonContext.Default.QueryDocument);
            if (document?.Version != 1 || document.Values is null) return new(StringComparer.Ordinal);
            return document.Values.Where(pair => Valid(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(StringComparer.Ordinal); }
    }
    private static bool Valid(QuerySnapshot? value) => value is not null &&
        !value.Items.IsDefault && !value.Libraries.IsDefault && value.Items.All(ValidItem) &&
        value.Libraries.All(library => library is not null && !string.IsNullOrWhiteSpace(library.Id) && library.Name is not null && Enum.IsDefined(library.Kind)) &&
        value.Kind is "libraries" or "items" or "page" && value.NextOffset is null or >= 0 && value.TotalCount is null or >= 0;
    private static bool ValidItem(MediaItem? item) => item is not null && !string.IsNullOrWhiteSpace(item.Id) && item.Name is not null && Enum.IsDefined(item.Kind) &&
        item.UserData is not null && !item.Images.IsDefault && !item.Genres.IsDefault && !item.People.IsDefault &&
        item.Genres.All(genre => genre is not null) &&
        item.Images.All(image => image is not null && !string.IsNullOrWhiteSpace(image.ItemId) && Enum.IsDefined(image.Kind) && image.Index >= 0) &&
        item.People.All(person => person is not null && !string.IsNullOrWhiteSpace(person.Id) && person.Name is not null && Enum.IsDefined(person.Kind));
    public async Task SaveAsync(string scope, Dictionary<string, QuerySnapshot> values, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new QueryDocument(1, values), QueryJsonContext.Default.QueryDocument);
        if (bytes.Length > 20 * 1024 * 1024) return;
        await AtomicFile.WriteAsync(PathFor(scope), bytes, cancellationToken: token).ConfigureAwait(false);
        try { Prune(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw StorageFailure(); }
    }
    public void Delete(string scope)
    {
        try { File.Delete(PathFor(scope)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw StorageFailure(); }
    }
    private static AppException StorageFailure() => new(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed,
        "无法清理本地查询缓存，请检查磁盘权限并重试。", true));
    private void Prune()
    {
        var files = new DirectoryInfo(paths.QueryCache).GetFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long size = 0;
        foreach (var file in files)
        {
            size += file.Length;
            if (size > 20 * 1024 * 1024 || clock.GetUtcNow() - file.LastWriteTimeUtc > TimeSpan.FromDays(7)) file.Delete();
        }
    }
}
