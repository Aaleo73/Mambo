using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Mambo.Core.Contracts;

namespace Mambo.Core.Updates;

/// <summary>专用无凭据客户端；只接受公开稳定版及 GitHub 计算的 SHA-256。</summary>
public sealed partial class GitHubUpdateService : IAppUpdateService, IDisposable
{
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly string repository;
    private readonly string cacheDirectory;
    private readonly ReleaseVersion currentVersion;

    public GitHubUpdateService(string repository, string version, string cacheDirectory, HttpMessageHandler? handler = null,
        string? applicationDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        this.repository = repository;
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.applicationDirectory = applicationDirectory is null ? null : Path.GetFullPath(applicationDirectory);
        IsConfigured = IsRepository(repository) && ReleaseVersion.TryParse(version, out currentVersion);
        CleanCompletedPreparations();
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mambo-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public bool IsConfigured { get; }
    public Uri? ReleasesPage => IsConfigured ? new($"https://github.com/{repository}/releases") : null;

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new AppUpdateException("此构建尚未配置更新来源。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await client.GetAsync(new Uri($"https://api.github.com/repos/{repository}/releases/latest"),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new AppUpdateException("暂时没有可用的公开发行版本。");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new AppUpdateException("GitHub 请求暂时受限，请稍后重试。");
            if (!response.IsSuccessStatusCode) throw new AppUpdateException("无法检查更新，请稍后重试。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            await CopyLimitedAsync(stream, bytes, MaximumMetadataBytes, null, timeout.Token).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), UpdateJsonContext.Default.GitHubRelease);
            if (release is null || release.Draft || release.Prerelease ||
                !ReleaseVersion.TryParse(release.TagName, out var version) || version.Prerelease)
                throw new AppUpdateException("发行版本信息无效。");
            if (version.CompareTo(currentVersion) <= 0) return null;
            var versionText = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;
            var name = $"Mambo-{versionText}-win-x64-setup.exe";
            var matching = release.Assets?.Where(a => a is not null && a.Name == name).ToArray() ?? [];
            if (matching.Length != 1) throw new AppUpdateException("新版尚未提供 Windows x64 安装包。");
            var asset = matching[0];
            if (asset.State != "uploaded" || asset.Digest?.StartsWith("sha256:", StringComparison.Ordinal) != true ||
                !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri))
                throw new AppUpdateException("新版安装包缺少有效的校验信息。");
            var update = new AppUpdate(versionText,
                new($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(release.TagName)}"), uri, asset.Size, asset.Digest[7..]);
            Validate(update);
            var manifestName = $"Mambo-{versionText}-win-x64-update.json";
            var manifests = release.Assets?.Where(a => a is not null && a.Name == manifestName).ToArray() ?? [];
            if (applicationDirectory is not null && manifests.Length > 0)
            {
                if (manifests.Length != 1) throw UpdateFiles.Invalid();
                var manifest = manifests[0];
                if (manifest.State != "uploaded" || manifest.Digest?.StartsWith("sha256:", StringComparison.Ordinal) != true ||
                    !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var manifestUri)) throw UpdateFiles.Invalid();
                var source = new AppUpdateAsset(manifestUri, manifest.Size, manifest.Digest[7..]);
                ValidateManifestSource(versionText, source);
                update = update with { ComponentManifest = source };
            }
            return update;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AppUpdateException("检查更新超时，请稍后重试。"); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { throw new AppUpdateException("无法读取更新信息，请检查网络后重试。"); }
    }

    public async Task<DownloadedAppUpdate> DownloadAsync(AppUpdate update, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);
        var directory = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Mambo-{update.Version}-win-x64-setup.exe");
        var partial = path + ".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await GetDownloadAsync(update.DownloadUri, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                (response.Content.Headers.ContentLength is { } length && length != update.Bytes))
                throw new AppUpdateException("安装包下载失败或大小不匹配。");
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await CopyLimitedAsync(source, destination, update.Bytes, progress, timeout.Token).ConfigureAwait(false);
                if (destination.Length != update.Bytes) throw new AppUpdateException("安装包下载不完整，请重试。");
            }
            await using (var file = File.OpenRead(partial))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, timeout.Token).ConfigureAwait(false));
                if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new AppUpdateException("安装包校验失败，已取消更新，请重新下载。");
            }
            File.Move(partial, path);
            progress?.Report(100);
            return new(path, update.Sha256);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AppUpdateException("下载更新超时，请重试。"); }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        { throw new AppUpdateException("无法下载更新，请检查网络和可用磁盘空间。"); }
        finally
        {
            // 仅清理本次下载的临时文件，不扫描程序或用户数据目录。
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<HttpResponseMessage> GetDownloadAsync(Uri uri, CancellationToken token)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!IsDownloadHost(uri)) throw new AppUpdateException("更新下载地址不受信任。");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new AppUpdateException("更新下载跳转无效。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new AppUpdateException("更新下载跳转次数过多。");
    }

    private void Validate(AppUpdate update)
    {
        if (!IsConfigured || !ReleaseVersion.TryParse(update.Version, out var version) || version.Prerelease ||
            version.CompareTo(currentVersion) <= 0 || update.Version.Contains('+', StringComparison.Ordinal) ||
            update.Version.StartsWith('v') || update.Bytes is <= 0 or > MaximumInstallerBytes || update.Sha256.Length != 64 ||
            update.Sha256.Any(static c => !char.IsAsciiHexDigit(c)) || !IsDownloadHost(update.DownloadUri) ||
            !update.DownloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            update.DownloadUri.Query.Length != 0 || update.DownloadUri.Fragment.Length != 0 ||
            (update.DownloadUri.AbsolutePath != $"/{repository}/releases/download/v{update.Version}/Mambo-{update.Version}-win-x64-setup.exe" &&
             update.DownloadUri.AbsolutePath != $"/{repository}/releases/download/{update.Version}/Mambo-{update.Version}-win-x64-setup.exe"))
            throw new AppUpdateException("新版安装包信息无效，已取消更新。");
    }

    private static bool IsDownloadHost(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static bool IsRepository(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 2 && parts.All(static p => p.Length is > 0 and <= 100 && p is not "." and not ".." &&
            p.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long maximum, IProgress<int>? progress, CancellationToken token)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            copied += count;
            if (copied > maximum) throw new AppUpdateException("更新文件超过预期大小，已取消下载。");
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            progress?.Report((int)Math.Min(99, copied * 100 / maximum));
        }
    }

    public void Dispose()
    {
        client.Dispose();
        foreach (var entry in preparations)
            if (!handedOff.ContainsKey(entry.Key)) UpdateTransaction.TryClean(entry.Key);
    }
}
