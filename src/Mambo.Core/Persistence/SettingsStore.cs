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
    public Dictionary<string, VideoQualityMode> VideoQualityPreferences { get; init; } = new(StringComparer.Ordinal);
    public long AudioLanguageRevision { get; init; }
    public long SubtitleLanguageRevision { get; init; }
    public Dictionary<string, TrackPreferenceRecord> TrackPreferences { get; init; } = new(StringComparer.Ordinal);
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
        var audioChanged = value.PreferredAudioLanguage != current.Settings.PreferredAudioLanguage;
        var subtitleChanged = value.PreferredSubtitleLanguage != current.Settings.PreferredSubtitleLanguage;
        return current with
        {
            Settings = value,
            AudioLanguageRevision = audioChanged ? checked(current.AudioLanguageRevision + 1) : current.AudioLanguageRevision,
            SubtitleLanguageRevision = subtitleChanged ? checked(current.SubtitleLanguageRevision + 1) : current.SubtitleLanguageRevision,
            TrackPreferences = audioChanged || subtitleChanged ? ClearTrackKinds(current.TrackPreferences, audioChanged, subtitleChanged) : current.TrackPreferences,
        };
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
    internal VideoQualityMode? VideoQualityPreference(string key) => Volatile.Read(ref document).VideoQualityPreferences.TryGetValue(key, out var mode) ? mode : null;
    internal Task SaveVideoQualityPreferenceAsync(string key, VideoQualityMode mode, CancellationToken token) => MutateAsync(current =>
    {
        if (!Enum.IsDefined(mode)) throw Invalid("画质模式无效。");
        // 整份文档每次保存都会重写，只留最近选择过的内容：这一项重新排到末尾，超出上限从最早的一端丢弃。
        var kept = current.VideoQualityPreferences.Where(pair => pair.Key != key).ToArray();
        var preferences = new Dictionary<string, VideoQualityMode>(StringComparer.Ordinal);
        foreach (var pair in kept.Skip(Math.Max(0, kept.Length - (VideoQualityPreferenceLimit - 1)))) preferences[pair.Key] = pair.Value;
        preferences[key] = mode;
        return current with { VideoQualityPreferences = preferences };
    }, token);
    private const int VideoQualityPreferenceLimit = 500;
    internal TrackPreferenceEpoch CaptureTrackEpoch() => Epoch(Volatile.Read(ref document));
    private static TrackPreferenceEpoch Epoch(SettingsDocument current) => new(current.Settings.PreferredAudioLanguage,
        current.Settings.PreferredSubtitleLanguage, current.AudioLanguageRevision, current.SubtitleLanguageRevision);
    private static bool SameEpoch(SettingsDocument current, TrackKind kind, TrackPreferenceEpoch epoch) => kind == TrackKind.Audio ?
        current.AudioLanguageRevision == epoch.AudioRevision && current.Settings.PreferredAudioLanguage == epoch.AudioLanguage :
        current.SubtitleLanguageRevision == epoch.SubtitleRevision && current.Settings.PreferredSubtitleLanguage == epoch.SubtitleLanguage;
    internal TrackChoice? TrackPreference(string key, TrackKind kind, TrackPreferenceEpoch epoch)
    {
        var current = Volatile.Read(ref document);
        if (!SameEpoch(current, kind, epoch) || !current.TrackPreferences.TryGetValue(key, out var preference)) return null;
        return kind == TrackKind.Audio ? preference.Audio : preference.Subtitle;
    }
    internal Task SaveTrackPreferenceAsync(string key, TrackKind kind, TrackChoice? choice, TrackPreferenceEpoch epoch, CancellationToken token) => MutateAsync(current =>
    {
        // 校验在写锁内：语言改回原值也不允许旧会话复活已清空的选择。
        if (!SameEpoch(current, kind, epoch)) return current;
        var previous = current.TrackPreferences.GetValueOrDefault(key) ?? new();
        var record = kind == TrackKind.Audio ? previous with { Audio = choice } : previous with { Subtitle = choice };
        var preferences = new Dictionary<string, TrackPreferenceRecord>(StringComparer.Ordinal);
        var kept = current.TrackPreferences.Where(pair => pair.Key != key).ToArray();
        var present = record.Audio is not null || record.Subtitle is not null;
        foreach (var pair in kept.Skip(Math.Max(0, kept.Length - (TrackPreferenceLimit - (present ? 1 : 0))))) preferences[pair.Key] = pair.Value;
        if (present) preferences[key] = record;
        return current with { TrackPreferences = preferences };
    }, token);
    private const int TrackPreferenceLimit = 500;
    private static Dictionary<string, TrackPreferenceRecord> ClearTrackKinds(Dictionary<string, TrackPreferenceRecord> values, bool audio, bool subtitle)
    {
        var result = new Dictionary<string, TrackPreferenceRecord>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var record = new TrackPreferenceRecord(audio ? null : value.Audio, subtitle ? null : value.Subtitle);
            if (record.Audio is not null || record.Subtitle is not null) result[key] = record;
        }
        return result;
    }
    private async Task MutateAsync(Func<SettingsDocument, SettingsDocument> update, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var value = update(document);
            if (ReferenceEquals(value, document)) return;
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
            var qualityJsonRepaired = RepairVideoQualityJson(root);
            var tracksJsonRepaired = RepairTrackJson(root);
            var value = arraysRepaired || qualityJsonRepaired || tracksJsonRepaired ? JsonSerializer.Deserialize(root!.ToJsonString(), StorageJsonContext.Default.SettingsDocument) :
                JsonSerializer.Deserialize(bytes, StorageJsonContext.Default.SettingsDocument);
            if (value is null || value.Version != 1 || value.Settings is null) return null;
            var settings = value.Settings;
            // 旧文档没有弹幕设置：缺失的对象反序列化为 null、缺失的数值为 0，逐项落回默认值。
            var bulletChat = NormalizeBulletChat(settings.BulletChat, out var bulletChatRepaired);
            if (bulletChatRepaired) settings = settings with { BulletChat = bulletChat };
            var style = SubtitleStyle.Normalize(settings.SubtitleStyle);
            var styleRepaired = style != settings.SubtitleStyle;
            var languagesRepaired = !TrackSelection.IsPreference(settings.PreferredAudioLanguage, false) || !TrackSelection.IsPreference(settings.PreferredSubtitleLanguage, true);
            settings = settings with { SubtitleStyle = style,
                PreferredAudioLanguage = TrackSelection.IsPreference(settings.PreferredAudioLanguage, false) ? settings.PreferredAudioLanguage : "auto",
                PreferredSubtitleLanguage = TrackSelection.IsPreference(settings.PreferredSubtitleLanguage, true) ? settings.PreferredSubtitleLanguage : "zh" };
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
            var videoQualityPreferences = NormalizeVideoQualityPreferences(value.VideoQualityPreferences, out var qualityPreferencesRepaired);
            var trackPreferences = NormalizeTrackPreferences(value.TrackPreferences, out var trackPreferencesRepaired);
            repaired = arraysRepaired || connectionRepaired || preferencesRepaired || externalRepaired || episodeLayoutRepaired || bulletChatRepaired || qualityPreferencesRepaired || qualityJsonRepaired ||
                tracksJsonRepaired || styleRepaired || languagesRepaired || trackPreferencesRepaired;
            return value with
            {
                Settings = settings,
                Connection = connection,
                Preferences = preferences,
                VideoQualityPreferences = videoQualityPreferences,
                TrackPreferences = trackPreferences,
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
        if (!TrackSelection.IsPreference(value.PreferredAudioLanguage, false) || !TrackSelection.IsPreference(value.PreferredSubtitleLanguage, true)) throw Invalid("首选语言无效。");
        SubtitleStyle.Validate(value.SubtitleStyle);
    }

    private static bool RepairTrackJson(JsonNode? root)
    {
        if (root is not JsonObject document) return false;
        var repaired = false;
        static KeyValuePair<string, JsonNode?> Field(JsonObject value, string name) => value.FirstOrDefault(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        static bool StringValue(JsonNode? node, out string? value)
        {
            value = null;
            return node is JsonValue json && json.TryGetValue(out value);
        }
        var settingsField = Field(document, nameof(SettingsDocument.Settings));
        var clearAudio = false;
        var clearSubtitle = false;
        if (settingsField.Value is JsonObject settings)
        {
            foreach (var (name, subtitle, fallback) in new[] { (nameof(AppSettings.PreferredAudioLanguage), false, "auto"), (nameof(AppSettings.PreferredSubtitleLanguage), true, "zh") })
            {
                var field = Field(settings, name);
                if (field.Key is null) { settings[name] = fallback; repaired = true; continue; }
                if (StringValue(field.Value, out var language) && TrackSelection.IsPreference(language, subtitle)) continue;
                settings[field.Key] = fallback;
                if (subtitle) clearSubtitle = true; else clearAudio = true;
                repaired = true;
            }
            var styleField = Field(settings, nameof(AppSettings.SubtitleStyle));
            var defaults = new SubtitleStyleSettings();
            if (styleField.Value is not JsonObject style)
            {
                settings[styleField.Key ?? nameof(AppSettings.SubtitleStyle)] = JsonSerializer.SerializeToNode(defaults, StorageJsonContext.Default.SubtitleStyleSettings);
                repaired = true;
            }
            else
            {
                foreach (var (name, minimum, maximum, fallback) in new[] { (nameof(SubtitleStyleSettings.FontSize), 18d, 72d, defaults.FontSize),
                    (nameof(SubtitleStyleSettings.OutlineSize), 0d, 6d, defaults.OutlineSize), (nameof(SubtitleStyleSettings.BottomMargin), 0d, 180d, defaults.BottomMargin) })
                {
                    var field = Field(style, name);
                    if (field.Key is not null && field.Value is JsonValue json && json.TryGetValue<double>(out var number) && SubtitleStyle.ValidNumber(number, minimum, maximum)) continue;
                    style[field.Key ?? name] = fallback;
                    repaired = true;
                }
                foreach (var (name, fallback, font) in new[] { (nameof(SubtitleStyleSettings.FontFamily), defaults.FontFamily, true), (nameof(SubtitleStyleSettings.TextColor), defaults.TextColor, false) })
                {
                    var field = Field(style, name);
                    if (field.Key is not null && StringValue(field.Value, out var text) && (font ? SubtitleStyle.ValidFont(text) : SubtitleStyle.ValidColor(text))) continue;
                    style[field.Key ?? name] = fallback;
                    repaired = true;
                }
                var ass = Field(style, nameof(SubtitleStyleSettings.OverrideAssStyle));
                if (ass.Key is not null && !(ass.Value is JsonValue assJson && assJson.TryGetValue<bool>(out _)))
                { style[ass.Key] = false; repaired = true; }
            }
        }
        foreach (var name in new[] { nameof(SettingsDocument.AudioLanguageRevision), nameof(SettingsDocument.SubtitleLanguageRevision) })
        {
            var field = Field(document, name);
            if (field.Key is null) continue;
            if (field.Value is JsonValue json && json.TryGetValue<long>(out var revision) && revision is >= 0 and < long.MaxValue) continue;
            document[field.Key] = 0;
            if (name == nameof(SettingsDocument.AudioLanguageRevision)) clearAudio = true; else clearSubtitle = true;
            repaired = true;
        }
        var tracksField = Field(document, nameof(SettingsDocument.TrackPreferences));
        if (tracksField.Key is not null)
        {
            if (tracksField.Value is not JsonObject tracks)
            { document[tracksField.Key] = new JsonObject(); repaired = true; }
            else foreach (var (key, node) in tracks.ToArray())
            {
                TrackPreferenceRecord? record = null;
                try { if (TrackPreferences.IsValidKey(key) && node is not null) record = JsonSerializer.Deserialize(node, StorageJsonContext.Default.TrackPreferenceRecord); }
                catch (JsonException) { }
                if (record is null) { tracks.Remove(key); repaired = true; continue; }
                var exact = key.AsSpan(64).StartsWith("|item|", StringComparison.Ordinal);
                var audio = !clearAudio && TrackPreferences.ValidChoice(record.Audio, TrackKind.Audio, exact) ? record.Audio : null;
                var subtitle = !clearSubtitle && TrackPreferences.ValidChoice(record.Subtitle, TrackKind.Subtitle, exact) ? record.Subtitle : null;
                if (audio is null && subtitle is null) { tracks.Remove(key); repaired = true; }
                else if (audio != record.Audio || subtitle != record.Subtitle)
                { tracks[key] = JsonSerializer.SerializeToNode(new TrackPreferenceRecord(audio, subtitle), StorageJsonContext.Default.TrackPreferenceRecord); repaired = true; }
            }
        }
        return repaired;
    }

    private static Dictionary<string, TrackPreferenceRecord> NormalizeTrackPreferences(Dictionary<string, TrackPreferenceRecord>? values, out bool repaired)
    {
        repaired = values is null || values.Count > TrackPreferenceLimit;
        var result = new Dictionary<string, TrackPreferenceRecord>(StringComparer.Ordinal);
        if (values is null) return result;
        foreach (var pair in values.Skip(Math.Max(0, values.Count - TrackPreferenceLimit))) result[pair.Key] = pair.Value;
        return result;
    }

    private static bool RepairVideoQualityJson(JsonNode? root)
    {
        if (root is not JsonObject document) return false;
        static bool IsMode(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) && Enum.IsDefined((VideoQualityMode)number);
        var stored = document.FirstOrDefault(static property => property.Key.Equals(nameof(SettingsDocument.VideoQualityPreferences), StringComparison.OrdinalIgnoreCase));
        if (stored.Key is null) return false;
        if (stored.Value is not JsonObject preferences) { document[stored.Key] = new JsonObject(); return true; }
        var repaired = false;
        foreach (var pair in preferences.ToArray())
            if (!IsMode(pair.Value)) { preferences.Remove(pair.Key); repaired = true; }
        return repaired;
    }

    private static Dictionary<string, VideoQualityMode> NormalizeVideoQualityPreferences(Dictionary<string, VideoQualityMode>? values, out bool repaired)
    {
        repaired = values is null;
        var result = new Dictionary<string, VideoQualityMode>(StringComparer.Ordinal);
        if (values is null) return result;
        var valid = values.Where(pair => VideoQualityPreferences.IsValidKey(pair.Key) && Enum.IsDefined(pair.Value)).ToArray();
        repaired = valid.Length != values.Count || valid.Length > VideoQualityPreferenceLimit;
        foreach (var (key, mode) in valid.Skip(Math.Max(0, valid.Length - VideoQualityPreferenceLimit))) result[key] = mode;
        return result;
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
