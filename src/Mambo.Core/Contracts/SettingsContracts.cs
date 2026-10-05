using System.Diagnostics.CodeAnalysis;

namespace Mambo.Core.Contracts;

public enum PlaybackMode
{
    Embedded,
    External,
}

public enum HdrMode
{
    Auto,
    Always,
    Off,
}

public enum HardwareDecodingMode
{
    Auto,
    Off,
}

public enum VideoQualityMode
{
    Standard = 0,
    Clear,
    Anime,
}

public enum SettingsThemeMode
{
    System,
    Light,
    Dark,
}

public enum ExternalPlayerStatus
{
    UsingEmbedded,
    Validating,
    Approved,
    Invalid,
}

public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized = false);

/// <summary>用于预填表单的输入在诊断文本中隐藏，绝不包含密码或令牌。</summary>
public sealed class ConnectionDefaults
{
    public ConnectionDefaults(string serverAddress = "", string userName = "")
    {
        ServerAddress = serverAddress;
        UserName = userName;
    }

    public string ServerAddress { get; }
    public string UserName { get; }
    public override string ToString() => "ConnectionDefaults { <redacted> }";
}

/// <summary>播放页弹幕菜单里的选择；不含服务器地址或任何凭据。</summary>
public sealed record BulletChatSettings
{
    public const int CurrentDefaults = 1;
    /// <summary>默认值的版本。后端据此把仍停留在旧默认值上的设置迁到新默认值；前端修改设置时原样保留。</summary>
    public int DefaultsVersion { get; init; } = CurrentDefaults;
    public bool Enabled { get; init; } = true;
    /// <summary>0.2–1。</summary>
    public double Opacity { get; init; } = 0.7;
    /// <summary>字号倍率，0.6–2。</summary>
    public double FontScale { get; init; } = 1;
    /// <summary>滚动弹幕横穿画面所需的秒数，5–30。</summary>
    public double ScrollSeconds { get; init; } = 15;
    /// <summary>显示区域占视频高度的比例，0.1–1。</summary>
    public double Area { get; init; } = 0.25;
}

public sealed record AppSettings
{
    public Guid DeviceId { get; init; }
    public PlaybackMode PlaybackMode { get; init; } = PlaybackMode.Embedded;
    public string? ExternalMpvPath { get; init; }
    public ExternalMpvApproval? ExternalMpvApproval { get; init; }
    public SettingsThemeMode ThemeMode { get; init; } = SettingsThemeMode.System;
    public bool AutomaticallyCheckForUpdates { get; init; } = true;
    /// <summary>选集面板的上次布局；false 为列表，true 为集号网格。旧设置中的显式选择保持不变。</summary>
    public bool UseEpisodeGrid { get; init; } = true;
    /// <summary>非全屏播放时是否收起选集面板；旧设置默认展开。</summary>
    public bool EpisodePanelCollapsed { get; init; }
    public HdrMode HdrMode { get; init; } = HdrMode.Auto;
    public HardwareDecodingMode HardwareDecoding { get; init; } = HardwareDecodingMode.Auto;
    public double Volume { get; init; } = 100;
    public WindowPlacement? Window { get; init; }
    public BulletChatSettings BulletChat { get; init; } = new();
}

/// <summary>设置变更通知经 IUiScheduler 触发；命令失败抛出 AppException。</summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    ConnectionDefaults ConnectionDefaults { get; }
    ExternalPlayerStatus ExternalPlayerStatus { get; }
    event EventHandler? Changed;
    Task UpdateAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default);
    Task SaveConnectionDefaultsAsync(ConnectionDefaults defaults, CancellationToken cancellationToken = default);
    Task ValidateExternalPlayerAsync(string path, CancellationToken cancellationToken = default);
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
    /// <summary>查询缓存与 mpv 着色器缓存的文件字节数；旧实现默认返回 0。</summary>
    Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0L);
    }
    /// <summary>日志目录的绝对路径；旧实现可返回空字符串。</summary>
    string LogDirectory => "";
}

public sealed class LibraryPreferenceChangedEventArgs(string libraryId, LibraryQuery query) : EventArgs
{
    public string LibraryId { get; } = libraryId;
    public LibraryQuery Query { get; } = query;
}

/// <summary>偏好自动归属当前账号作用域，UI 不构造账号键；Changed 经 IUiScheduler 触发。</summary>
public interface ILibraryPreferences
{
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Get 是 C# 资料库偏好接口名称，不用于跨语言调用。")]
    LibraryQuery Get(string libraryId);
    event EventHandler<LibraryPreferenceChangedEventArgs>? Changed;
    Task SetAsync(string libraryId, LibraryQuery query, CancellationToken cancellationToken = default);
    Task ResetAsync(string libraryId, CancellationToken cancellationToken = default);
}
