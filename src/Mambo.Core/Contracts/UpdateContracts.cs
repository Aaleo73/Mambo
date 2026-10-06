namespace Mambo.Core.Contracts;

/// <summary>公开发行资产；不包含 Emby 地址、凭据或 GitHub 令牌。</summary>
public sealed record AppUpdate(string Version, Uri ReleasePage, Uri DownloadUri, long Bytes, string Sha256,
    AppUpdateAsset? ComponentManifest = null);

public sealed record AppUpdateAsset(Uri DownloadUri, long Bytes, string Sha256);

public sealed record PreparedAppUpdate(string StagingDirectory, string Version, long DownloadedBytes, long ReusedBytes);

public sealed record DownloadedAppUpdate(string InstallerPath, string Sha256);

public interface IAppUpdateService
{
    bool IsConfigured { get; }
    Uri? ReleasesPage { get; }
    Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default);
    Task<DownloadedAppUpdate> DownloadAsync(AppUpdate update, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
    Task<PreparedAppUpdate> PrepareAsync(AppUpdate update, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
    Task LaunchPreparedAsync(PreparedAppUpdate update, CancellationToken cancellationToken = default);
}

/// <summary>更新错误只公开固定中文文案，避免 HTTP 异常中的签名下载 URL 进入界面或日志。</summary>
public sealed class AppUpdateException(string message) : Exception(message);
