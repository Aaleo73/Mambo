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

public sealed record AppSettings
{
    public Guid DeviceId { get; init; }
    public PlaybackMode PlaybackMode { get; init; } = PlaybackMode.Embedded;
    public string? ExternalMpvPath { get; init; }
    public ExternalMpvApproval? ExternalMpvApproval { get; init; }
    public SettingsThemeMode ThemeMode { get; init; } = SettingsThemeMode.System;
    public HdrMode HdrMode { get; init; } = HdrMode.Auto;
    public HardwareDecodingMode HardwareDecoding { get; init; } = HardwareDecodingMode.Auto;
    public double Volume { get; init; } = 100;
    public WindowPlacement? Window { get; init; }
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
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Get 是已交接的 C# 资料库偏好接口名称，不用于跨语言调用。")]
    LibraryQuery Get(string libraryId);
    event EventHandler<LibraryPreferenceChangedEventArgs>? Changed;
    Task SetAsync(string libraryId, LibraryQuery query, CancellationToken cancellationToken = default);
    Task ResetAsync(string libraryId, CancellationToken cancellationToken = default);
}
