using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;

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
    private readonly IExternalPlayerValidator? externalPlayerValidator;
    private readonly SemaphoreSlim writer = new(1, 1);
    private SettingsDocument document;
    private volatile bool disposed;
    private int externalPlayerStatus;
    private long validationGeneration;
    private CancellationTokenSource? validationCancellation;
    public SettingsStore(AppPaths paths, IUiScheduler scheduler, IExternalPlayerValidator? externalPlayerValidator = null)
    {
        this.paths = paths; this.scheduler = scheduler;
        this.externalPlayerValidator = externalPlayerValidator;
        var primary = Load(paths.Settings, out var repaired);
        document = primary ?? Load(paths.Settings + ".bak", out _) ?? new SettingsDocument();
        // 恢复备份或首次生成设备标识后修复主文件；不能用损坏的主文件覆盖有效备份。
        if (primary is null || repaired) AtomicFile.WriteAsync(paths.Settings, Serialize(document)).GetAwaiter().GetResult();
        externalPlayerStatus = (int)StatusFor(document.Settings);
    }
    public AppSettings Current => Volatile.Read(ref document).Settings;
    public ConnectionDefaults ConnectionDefaults => Volatile.Read(ref document).Connection;
    public ExternalPlayerStatus ExternalPlayerStatus => (ExternalPlayerStatus)Volatile.Read(ref externalPlayerStatus);
    public string LogDirectory => Path.Combine(paths.Root, "logs");
    public event EventHandler? Changed;
    public Task UpdateAsync(AppSettings settings, CancellationToken cancellationToken = default) => UpdateAsync(_ => settings, cancellationToken);
    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) => MutateAsync(current =>
    {
        ArgumentNullException.ThrowIfNull(update);
        var value = update(current.Settings);
        Validate(value);
        if (value.DeviceId != current.Settings.DeviceId) throw Invalid("设备标识不可修改。");
        // 指纹只允许由验证器产生；表单更新不能替换它。修改路径会撤销原批准。
        if (value.ExternalMpvApproval is not null && value.ExternalMpvApproval != current.Settings.ExternalMpvApproval)
            throw ApprovalRequired("请通过验证按钮批准外部播放器。");
        if (!SamePath(value.ExternalMpvPath, current.Settings.ExternalMpvPath))
            value = value with { PlaybackMode = PlaybackMode.Embedded, ExternalMpvApproval = null };
        if (value.PlaybackMode == PlaybackMode.External && !MatchesApproval(value))
            throw ApprovalRequired("请先验证外部播放器，再启用外部播放。");
        return current with { Settings = value };
    }, cancellationToken);
    public Task SaveConnectionDefaultsAsync(ConnectionDefaults defaults, CancellationToken cancellationToken = default) => MutateAsync(current =>
    {
        if (defaults.UserName.Length > 256 || System.Text.Encoding.UTF8.GetByteCount(defaults.ServerAddress) > 2048) throw Invalid("服务器表单输入过长。");
        return current with { Connection = defaults };
    }, cancellationToken);
    public async Task ValidateExternalPlayerAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long generation;
        ExternalPlayerStatus previous;
        await writer.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            validationCancellation?.Cancel();
            generation = ++validationGeneration;
            validationCancellation = cancellation;
            previous = ExternalPlayerStatus == ExternalPlayerStatus.Validating ? StatusFor(document.Settings) : ExternalPlayerStatus;
            SetStatus(ExternalPlayerStatus.Validating);
        }
        finally { writer.Release(); }
        Publish();

        try
        {
            if (externalPlayerValidator is null) throw ApprovalRequired("外部播放器验证服务尚未就绪。");
            var approval = await externalPlayerValidator.ValidateAsync(path, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            await writer.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (disposed || generation != validationGeneration) return;
                var settings = document.Settings with { ExternalMpvPath = approval.Path, ExternalMpvApproval = approval };
                if (!MatchesApproval(settings)) throw Invalid("外部播放器验证结果无效。");
                // 基于此刻的设置合并，保留验证期间音量、主题等独立更新。
                await SaveLockedAsync(document with { Settings = settings }, cancellation.Token).ConfigureAwait(false);
                SetStatus(ExternalPlayerStatus.Approved);
            }
            finally { writer.Release(); }
            Publish();
        }
        catch (OperationCanceledException)
        {
            await RestoreStatusAsync(generation, previous).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is AppException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            try { await InvalidateApprovalAsync(generation, cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                await RestoreStatusAsync(generation, previous).ConfigureAwait(false);
                throw;
            }
            if (exception is AppException) throw;
            throw Invalid("无法验证外部播放器，请重新选择有效的 mpv 程序。");
        }
        finally
        {
            await writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { if (ReferenceEquals(validationCancellation, cancellation)) validationCancellation = null; }
            finally { writer.Release(); }
        }
    }

    /// <summary>创建外部引擎前复核已批准指纹；文件变动只能通过用户再次验证批准。</summary>
    public async Task<ExternalMpvApproval?> GetApprovedExternalPlayerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var settings = Current;
        if (settings.PlaybackMode != PlaybackMode.External) return null;
        var approval = settings.ExternalMpvApproval;
        var generation = Volatile.Read(ref validationGeneration);
        var valid = approval is not null && MatchesApproval(settings) && externalPlayerValidator is not null &&
            await externalPlayerValidator.VerifyAsync(approval, cancellationToken).ConfigureAwait(false);
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = document.Settings;
            if (generation != validationGeneration || current.PlaybackMode != PlaybackMode.External || current.ExternalMpvApproval != approval ||
                !SamePath(current.ExternalMpvPath, settings.ExternalMpvPath)) return null;
            if (valid) return approval;
            await SaveLockedAsync(document with { Settings = current with { PlaybackMode = PlaybackMode.Embedded, ExternalMpvApproval = null } }, cancellationToken).ConfigureAwait(false);
            ++validationGeneration;
            validationCancellation?.Cancel();
            SetStatus(ExternalPlayerStatus.Invalid);
        }
        finally { writer.Release(); }
        Publish();
        return null;
    }

    public Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => MeasureCache(cancellationToken), cancellationToken);
    }
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
            ObjectDisposedException.ThrowIf(disposed, this);
            var value = update(document);
            var pathChanged = !SamePath(value.Settings.ExternalMpvPath, document.Settings.ExternalMpvPath);
            var approvalRemoved = document.Settings.ExternalMpvApproval is not null && value.Settings.ExternalMpvApproval is null;
            await SaveLockedAsync(value, token).ConfigureAwait(false);
            if (pathChanged || approvalRemoved)
            {
                ++validationGeneration;
                validationCancellation?.Cancel();
                SetStatus(string.IsNullOrEmpty(value.Settings.ExternalMpvPath) ? ExternalPlayerStatus.UsingEmbedded : ExternalPlayerStatus.Invalid);
            }
            else if (ExternalPlayerStatus != ExternalPlayerStatus.Validating && ExternalPlayerStatus != ExternalPlayerStatus.Invalid)
                SetStatus(StatusFor(value.Settings));
        }
        finally { writer.Release(); }
        Publish();
    }
    private async Task SaveLockedAsync(SettingsDocument value, CancellationToken token)
    {
        await AtomicFile.WriteAsync(paths.Settings, Serialize(value), true, token).ConfigureAwait(false);
        Volatile.Write(ref document, value);
    }
    private async Task RestoreStatusAsync(long generation, ExternalPlayerStatus previous)
    {
        await writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { if (!disposed && generation == validationGeneration) SetStatus(previous); }
        finally { writer.Release(); }
        Publish();
    }
    private async Task InvalidateApprovalAsync(long generation, CancellationToken token)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (disposed || generation != validationGeneration) return;
            await SaveLockedAsync(document with { Settings = document.Settings with { PlaybackMode = PlaybackMode.Embedded, ExternalMpvApproval = null } }, token).ConfigureAwait(false);
            SetStatus(ExternalPlayerStatus.Invalid);
        }
        finally { writer.Release(); }
        Publish();
    }
    private void SetStatus(ExternalPlayerStatus status) => Volatile.Write(ref externalPlayerStatus, (int)status);
    private void Publish() => scheduler.TryEnqueue(() => { if (!disposed) Changed?.Invoke(this, EventArgs.Empty); });
    private long MeasureCache(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            // 不调用 AppPaths 的目录创建属性，也不扫描 outbox、日志或用户选择的目录。
            if (!IsOrdinaryDirectory(paths.Root)) return 0;
            var total = MeasureDirectory(Path.Combine(paths.Root, "cache"), token);
            var mpvRoot = Path.Combine(paths.Root, "mpv");
            if (IsOrdinaryDirectory(mpvRoot)) total = checked(total + MeasureDirectory(Path.Combine(mpvRoot, "shader-cache"), token));
            token.ThrowIfCancellationRequested();
            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new AppException(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed, "无法读取缓存占用，请稍后重试。", true));
        }
    }
    private static long MeasureDirectory(string root, CancellationToken token)
    {
        if (!IsOrdinaryDirectory(root)) return 0;
        var boundary = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var directories = new Stack<string>();
        directories.Push(root);
        long total = 0;
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (!IsOrdinaryDirectory(directory)) continue;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Path.GetFullPath(entry).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                        else total = checked(total + new FileInfo(entry).Length);
                    }
                    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
        }
        return total;
    }
    private static bool IsOrdinaryDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    private static bool SamePath(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
    private static bool MatchesApproval(AppSettings value) => value.ExternalMpvApproval is { } approval &&
        SamePath(value.ExternalMpvPath, approval.Path) && !string.IsNullOrWhiteSpace(approval.Path) && Path.IsPathFullyQualified(approval.Path) && approval.Size > 0 &&
        approval.LastWriteTimeUtcTicks > 0 && approval.LastWriteTimeUtcTicks <= DateTime.MaxValue.Ticks && !string.IsNullOrWhiteSpace(approval.Version) && approval.Version.Length <= 96 &&
        approval.Sha256 is { Length: 64 } && approval.Sha256.All(static character => char.IsAsciiHexDigit(character));
    private static ExternalPlayerStatus StatusFor(AppSettings value) => MatchesApproval(value) ? ExternalPlayerStatus.Approved :
        string.IsNullOrEmpty(value.ExternalMpvPath) ? ExternalPlayerStatus.UsingEmbedded : ExternalPlayerStatus.Invalid;
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
            if (value is null || value.Version != 1 || value.Settings is null) return null;
            var settings = value.Settings;
            // 旧文档没有弹幕设置：缺失的对象反序列化为 null、缺失的数值为 0，逐项落回默认值。
            var bulletChat = NormalizeBulletChat(settings.BulletChat, out var bulletChatRepaired);
            if (bulletChatRepaired) settings = settings with { BulletChat = bulletChat };
            Validate(settings);
            // 源生成反序列化会为缺失的 init-only 布尔成员写入 false；显式迁移旧文档，
            // 不能靠属性初始化器，也不能覆盖用户已保存的列表选择。
            var episodeLayoutRepaired = root is JsonObject document &&
                document.FirstOrDefault(static property => property.Key.Equals(nameof(SettingsDocument.Settings), StringComparison.OrdinalIgnoreCase)).Value is JsonObject storedSettings &&
                !storedSettings.Any(static property => property.Key.Equals(nameof(AppSettings.UseEpisodeGrid), StringComparison.OrdinalIgnoreCase));
            if (episodeLayoutRepaired) settings = settings with { UseEpisodeGrid = true };
            var externalRepaired = settings.ExternalMpvApproval is not null && !MatchesApproval(settings) ||
                settings.PlaybackMode == PlaybackMode.External && !MatchesApproval(settings);
            if (externalRepaired) settings = settings with { PlaybackMode = PlaybackMode.Embedded, ExternalMpvApproval = null };
            // 非关键字段损坏只丢弃对应输入，不重置有效的设备标识与其他偏好。
            var connection = NormalizeConnection(value.Connection, out var connectionRepaired);
            var preferences = NormalizePreferences(value.Preferences, out var preferencesRepaired);
            repaired = arraysRepaired || connectionRepaired || preferencesRepaired || externalRepaired || episodeLayoutRepaired || bulletChatRepaired;
            return value with
            {
                Settings = settings,
                Connection = connection,
                Preferences = preferences,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AppException) { return null; }
    }
    private static void Validate(AppSettings? value)
    {
        if (value is null || value.DeviceId == Guid.Empty || !double.IsFinite(value.Volume) || value.Volume is < 0 or > 100 ||
            !Enum.IsDefined(value.PlaybackMode) || !Enum.IsDefined(value.HdrMode) || !Enum.IsDefined(value.HardwareDecoding) ||
            !Enum.IsDefined(value.ThemeMode)) throw Invalid("播放器设置无效。");
        if (value.BulletChat is not { } bulletChat || NormalizeBulletChat(bulletChat, out _) != bulletChat) throw Invalid("弹幕设置无效。");
    }

    private static BulletChatSettings NormalizeBulletChat(BulletChatSettings? value, out bool repaired)
    {
        var defaults = new BulletChatSettings();
        if (value is null) { repaired = true; return defaults; }
        static double Keep(double stored, double minimum, double maximum, double fallback) =>
            double.IsFinite(stored) && stored >= minimum && stored <= maximum ? stored : fallback;
        var result = value with
        {
            Opacity = Keep(value.Opacity, 0.2, 1, defaults.Opacity),
            FontScale = Keep(value.FontScale, 0.6, 2, defaults.FontScale),
            ScrollSeconds = Keep(value.ScrollSeconds, 5, 30, defaults.ScrollSeconds),
            Area = Keep(value.Area, 0.1, 1, defaults.Area),
        };
        if (result.DefaultsVersion != BulletChatSettings.CurrentDefaults)
        {
            // 第 1 版把显示区域的默认值从 0.85 改为 0.25。只迁移仍是旧默认值的设置，用户自己选的保留；
            // 记下版本后，以后再选 0.85 不会被改回去。
            if (result.DefaultsVersion < 1 && result.Area == 0.85) result = result with { Area = defaults.Area };
            result = result with { DefaultsVersion = BulletChatSettings.CurrentDefaults };
        }
        repaired = result != value;
        return result;
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
    private static AppException ApprovalRequired(string text) => new(new(AppErrorKind.Player, ErrorCodes.ExternalApprovalRequired, text, false));
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Interlocked.Increment(ref validationGeneration);
        // 验证调用持有并释放其 CTS；Dispose 不销毁仍在使用的写锁。
        try { validationCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        Changed = null;
        CacheClearer = null;
    }
}
