using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;

namespace Mambo.Core.Persistence;

public sealed record SettingsDocument
{
    public int Version { get; init; } = 1;
    [JsonRequired]
    public AppSettings Settings { get; init; } = new() { DeviceId = Guid.NewGuid() };
    public ConnectionDefaults Connection { get; init; } = new();
    public Dictionary<string, LibraryQuery> Preferences { get; init; } = new(StringComparer.Ordinal);
}

public sealed class SettingsStore : ISettingsService, IDisposable
{
    private readonly AppPaths paths;
    private readonly IUiScheduler scheduler;
    private readonly SemaphoreSlim writer = new(1, 1);
    private SettingsDocument document;
    private bool disposed;
    public SettingsStore(AppPaths paths, IUiScheduler scheduler)
    {
        this.paths = paths; this.scheduler = scheduler;
        var primary = Load(paths.Settings, out var repaired);
        document = primary ?? Load(paths.Settings + ".bak", out _) ?? new SettingsDocument();
        // 恢复备份或首次生成设备标识后修复主文件；不能用损坏的主文件覆盖有效备份。
        if (primary is null || repaired) AtomicFile.WriteAsync(paths.Settings, Serialize(document)).GetAwaiter().GetResult();
    }
    public AppSettings Current => Volatile.Read(ref document).Settings;
    public ConnectionDefaults ConnectionDefaults => Volatile.Read(ref document).Connection;
    public ExternalPlayerStatus ExternalPlayerStatus => Current.PlaybackMode == PlaybackMode.Embedded ? ExternalPlayerStatus.UsingEmbedded : ExternalPlayerStatus.Invalid;
    public event EventHandler? Changed;
    public Task UpdateAsync(AppSettings settings, CancellationToken cancellationToken = default) => UpdateAsync(_ => settings, cancellationToken);
    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) => MutateAsync(current =>
    {
        var value = update(current.Settings);
        Validate(value);
        if (value.DeviceId != current.Settings.DeviceId) throw Invalid("设备标识不可修改。");
        return current with { Settings = value };
    }, cancellationToken);
    public Task SaveConnectionDefaultsAsync(ConnectionDefaults defaults, CancellationToken cancellationToken = default) => MutateAsync(current =>
    {
        if (defaults.UserName.Length > 256 || System.Text.Encoding.UTF8.GetByteCount(defaults.ServerAddress) > 2048) throw Invalid("服务器表单输入过长。");
        return current with { Connection = defaults };
    }, cancellationToken);
    public Task ValidateExternalPlayerAsync(string path, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); scheduler.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty)); return Task.CompletedTask; }
    internal Func<CancellationToken, Task>? CacheClearer { get; set; }
    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return CacheClearer?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }
    internal LibraryQuery Preference(string key) => Volatile.Read(ref document).Preferences.GetValueOrDefault(key) ?? new();
    internal Task SavePreferenceAsync(string key, LibraryQuery? query, CancellationToken token) => MutateAsync(current =>
    {
        var preferences = new Dictionary<string, LibraryQuery>(current.Preferences, StringComparer.Ordinal);
        if (query is null) preferences.Remove(key); else preferences[key] = query;
        return current with { Preferences = preferences };
    }, token);
    private async Task MutateAsync(Func<SettingsDocument, SettingsDocument> update, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var value = update(document);
            await AtomicFile.WriteAsync(paths.Settings, Serialize(value), true, token).ConfigureAwait(false);
            Volatile.Write(ref document, value);
        }
        finally { writer.Release(); }
        scheduler.TryEnqueue(() => { if (!disposed) Changed?.Invoke(this, EventArgs.Empty); });
    }
    private static byte[] Serialize(SettingsDocument value) => JsonSerializer.SerializeToUtf8Bytes(value, StorageJsonContext.Default.SettingsDocument);
    private static SettingsDocument? Load(string path, out bool repaired)
    {
        repaired = false;
        try
        {
            var bytes = AtomicFile.Read(path);
            if (bytes is null) return null;
            // ImmutableArray 是值类型；JSON null 在反序列化时就会失败，先修复这类可选筛选数组。
            var root = JsonNode.Parse(bytes);
            var arraysRepaired = RepairNullPreferenceArrays(root);
            var value = arraysRepaired ? JsonSerializer.Deserialize(root!.ToJsonString(), StorageJsonContext.Default.SettingsDocument) :
                JsonSerializer.Deserialize(bytes, StorageJsonContext.Default.SettingsDocument);
            if (value is null || value.Version != 1) return null;
            Validate(value.Settings);
            // 非关键字段损坏只丢弃对应输入，不重置有效的设备标识与其他偏好。
            var connection = NormalizeConnection(value.Connection, out var connectionRepaired);
            var preferences = NormalizePreferences(value.Preferences, out var preferencesRepaired);
            repaired = arraysRepaired || connectionRepaired || preferencesRepaired;
            return value with
            {
                Connection = connection,
                Preferences = preferences,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AppException) { return null; }
    }
    private static void Validate(AppSettings? value)
    {
        if (value is null || value.DeviceId == Guid.Empty || !double.IsFinite(value.Volume) || value.Volume is < 0 or > 100 ||
            !Enum.IsDefined(value.PlaybackMode) || !Enum.IsDefined(value.HdrMode) || !Enum.IsDefined(value.HardwareDecoding)) throw Invalid("播放器设置无效。");
    }

    private static ConnectionDefaults NormalizeConnection(ConnectionDefaults? value, out bool repaired)
    {
        repaired = value is null || value.ServerAddress is null || value.UserName is null;
        var address = value?.ServerAddress ?? "";
        var userName = value?.UserName ?? "";
        if (Encoding.UTF8.GetByteCount(address) > 2048) { address = ""; repaired = true; }
        if (userName.Length > 256) { userName = ""; repaired = true; }
        return new ConnectionDefaults(address, userName);
    }

    private static bool RepairNullPreferenceArrays(JsonNode? root)
    {
        if (root is not JsonObject document || document.FirstOrDefault(static property =>
            property.Key.Equals(nameof(SettingsDocument.Preferences), StringComparison.OrdinalIgnoreCase)).Value is not JsonObject preferences) return false;
        var repaired = false;
        foreach (var (_, value) in preferences)
        {
            if (value is not JsonObject query) continue;
            foreach (var (name, array) in query.ToArray())
            {
                if (array is not null || !(name.Equals(nameof(LibraryQuery.Genres), StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(nameof(LibraryQuery.Years), StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(nameof(LibraryQuery.OfficialRatings), StringComparison.OrdinalIgnoreCase))) continue;
                query[name] = new JsonArray();
                repaired = true;
            }
        }
        return repaired;
    }

    private static Dictionary<string, LibraryQuery> NormalizePreferences(Dictionary<string, LibraryQuery>? values, out bool repaired)
    {
        repaired = values is null;
        var result = new Dictionary<string, LibraryQuery>(StringComparer.Ordinal);
        if (values is null) return result;
        foreach (var (key, query) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || query is null || !Enum.IsDefined(query.Sort) || !Enum.IsDefined(query.Direction))
            { repaired = true; continue; }
            var genres = NormalizeArray(query.Genres, static value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256, out var genresRepaired);
            var years = NormalizeArray(query.Years, static value => value is >= 1 and <= 9999, out var yearsRepaired);
            var ratings = NormalizeArray(query.OfficialRatings, static value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256, out var ratingsRepaired);
            repaired |= genresRepaired || yearsRepaired || ratingsRepaired;
            result[key] = query with
            {
                Genres = genres,
                Years = years,
                OfficialRatings = ratings,
            };
        }
        return result;
    }

    private static ImmutableArray<T> NormalizeArray<T>(ImmutableArray<T> values, Func<T, bool> valid, out bool repaired)
    {
        if (values.IsDefault) { repaired = true; return []; }
        var filtered = values.Where(valid).Distinct().ToImmutableArray();
        repaired = filtered.Length != values.Length;
        return repaired ? filtered : values;
    }
    private static AppException Invalid(string text) => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, text, false));
    public void Dispose() { disposed = true; Changed = null; CacheClearer = null; }
}
