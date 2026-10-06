using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mambo.Core.Contracts;

namespace Mambo.Core.Updates;

public sealed partial class GitHubUpdateService
{
    private readonly string? applicationDirectory;
    private readonly ConcurrentDictionary<string, string> preparations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> handedOff = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PreparedAppUpdate> PrepareAsync(AppUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);
        if (applicationDirectory is null || update.ComponentManifest is not { } source)
            throw new AppUpdateException("此版本尚不支持应用内更新。");
        ValidateManifestSource(update.Version, source);
        string? staging = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        try
        {
            UpdateFiles.EnsurePlainPath(applicationDirectory);
            UpdateFiles.EnsurePlainPath(cacheDirectory);
            if (Directory.Exists(Path.Combine(applicationDirectory, UpdateFiles.TransactionDirectory)))
                throw new AppUpdateException("上次更新尚未恢复，请重新启动 Mambo 后再试。");
            // Test write access before downloading. Portable applications keep their current directory.
            var probe = Path.Combine(applicationDirectory, ".mambo-update-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            staging = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var manifestPath = Path.Combine(staging, "update.json");
            await DownloadAssetAsync(source, manifestPath, null, token).ConfigureAwait(false);
            var manifest = UpdateFiles.ReadManifest(manifestPath);
            if (manifest.Version != update.Version) throw UpdateFiles.Invalid();
            var required = new List<UpdateComponent>();
            long reusedBytes = 0;
            foreach (var component in manifest.Components)
            {
                var matches = true;
                foreach (var file in component.Files)
                    if (!await UpdateFiles.MatchesAsync(UpdateFiles.Resolve(applicationDirectory, file.Path), file, token).ConfigureAwait(false))
                    { matches = false; break; }
                if (matches) reusedBytes += component.Files.Sum(static f => f.Bytes);
                else required.Add(component);
            }
            var total = required.Sum(static c => c.Bytes);
            long downloaded = 0;
            foreach (var component in required)
            {
                var name = $"Mambo-{manifest.Version}-win-x64-update-{component.Name}.zip";
                var uri = new Uri(source.DownloadUri, name);
                var zip = Path.Combine(staging, component.Name + ".zip");
                var previous = downloaded;
                await DownloadAssetAsync(new(uri, component.Bytes, component.Sha256), zip,
                    new InlineProgress(value => progress?.Report((int)Math.Min(99, (previous + component.Bytes * value / 100) * 100 / Math.Max(1, total)))), token).ConfigureAwait(false);
                await ExtractComponentAsync(zip, Path.Combine(staging, "payload"), component, token).ConfigureAwait(false);
                File.Delete(zip);
                downloaded += component.Bytes;
            }
            await UpdateTransaction.VerifyAsync(applicationDirectory, staging, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            preparations[staging] = source.Sha256;
            progress?.Report(100);
            return new(staging, manifest.Version, downloaded, reusedBytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AppUpdateException("下载更新超时，请重试。"); }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { throw new AppUpdateException("无法准备更新，请检查网络、目录写入权限和可用磁盘空间。"); }
        finally
        {
            if (staging is not null && !preparations.ContainsKey(staging)) UpdateTransaction.TryClean(staging);
        }
    }

    public async Task LaunchPreparedAsync(PreparedAppUpdate update, CancellationToken cancellationToken = default)
    {
        try { await LaunchPreparedCoreAsync(update, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AppUpdateException("更新程序准备超时，请稍后重试。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new AppUpdateException("无法启动更新，请重新下载或从发行页面修复应用。"); }
    }

    private async Task LaunchPreparedCoreAsync(PreparedAppUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (applicationDirectory is null || !preparations.TryGetValue(update.StagingDirectory, out var digest) || handedOff.ContainsKey(update.StagingDirectory))
            throw new AppUpdateException("更新尚未准备好，请重新下载。");
        var staging = update.StagingDirectory;
        var manifestPath = UpdateFiles.Resolve(staging, "update.json");
        if (!await UpdateFiles.MatchesAsync(manifestPath, new("update.json", new FileInfo(manifestPath).Length, digest), cancellationToken).ConfigureAwait(false))
            throw UpdateFiles.Invalid();
        await UpdateTransaction.VerifyAsync(applicationDirectory, staging, cancellationToken).ConfigureAwait(false);
        var installedPath = UpdateFiles.Resolve(applicationDirectory, "release-manifest.json");
        if (new FileInfo(installedPath).Length > UpdateFiles.MaximumManifestBytes) throw UpdateFiles.Invalid();
        var installed = JsonSerializer.Deserialize(File.ReadAllBytes(installedPath), ComponentJsonContext.Default.InstalledReleaseManifest);
        var helperFile = installed?.Files?.SingleOrDefault(static file => file.Path == "Mambo.Updater.exe");
        if (helperFile is null || !await UpdateFiles.MatchesAsync(UpdateFiles.Resolve(applicationDirectory, helperFile.Path), helperFile, cancellationToken).ConfigureAwait(false))
            throw new AppUpdateException("更新程序缺失或已损坏，请从发行页面重新获取应用。");
        var helper = UpdateFiles.Resolve(staging, "Mambo.Updater.exe");
        File.Copy(UpdateFiles.Resolve(applicationDirectory, helperFile.Path), helper, overwrite: true);
        using var parent = Process.GetCurrentProcess();
        var request = new UpdateRequest(applicationDirectory, parent.Id, parent.StartTime.ToUniversalTime().Ticks, digest);
        var requestPath = UpdateFiles.Resolve(staging, "request.json");
        File.WriteAllBytes(requestPath, JsonSerializer.SerializeToUtf8Bytes(request, ComponentJsonContext.Default.UpdateRequest));
        var ready = UpdateFiles.Resolve(staging, "ready");
        var cancel = UpdateFiles.Resolve(staging, "cancel");
        File.Delete(ready);
        File.Delete(cancel);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = staging };
        start.ArgumentList.Add("--apply");
        start.ArgumentList.Add(staging);
        using var process = Process.Start(start) ?? throw new AppUpdateException("无法启动更新程序。");
        // Keep the running application until the helper has validated the plan and acquired the lock.
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while (!File.Exists(ready))
            {
                if (process.HasExited) throw new AppUpdateException("更新程序未能就绪，请稍后重试。");
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            handedOff[staging] = true;
        }
        catch
        {
            File.WriteAllText(cancel, "cancel");
            // The application is still alive, so this helper cannot have started replacing files.
            // Reap it before permitting a retry to avoid a stale readiness file or two waiting helpers.
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(stopDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); }
            throw;
        }
    }

    private void CleanCompletedPreparations()
    {
        if (!IsConfigured || applicationDirectory is null || !Directory.Exists(cacheDirectory)) return;
        try
        {
            UpdateFiles.EnsurePlainPath(cacheDirectory);
            foreach (var directory in Directory.EnumerateDirectories(cacheDirectory))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                var result = UpdateFiles.Resolve(directory, "result");
                if (File.Exists(result) && new FileInfo(result).Length <= 16 &&
                    File.GetLastWriteTimeUtc(result) < DateTime.UtcNow.AddMinutes(-1) &&
                    File.ReadAllText(result) is "success" or "failed" or "cancelled") UpdateTransaction.TryClean(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppUpdateException) { }
    }

    private void ValidateManifestSource(string version, AppUpdateAsset source)
    {
        var name = $"Mambo-{version}-win-x64-update.json";
        var uri = source.DownloadUri;
        if (!IsDownloadHost(uri) || uri.Host != "github.com" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            source.Bytes is <= 0 or > UpdateFiles.MaximumManifestBytes || !UpdateFiles.IsHash(source.Sha256) ||
            (uri.AbsolutePath != $"/{repository}/releases/download/v{version}/{name}" && uri.AbsolutePath != $"/{repository}/releases/download/{version}/{name}"))
            throw UpdateFiles.Invalid();
    }

    private async Task DownloadAssetAsync(AppUpdateAsset source, string path, IProgress<int>? progress, CancellationToken token)
    {
        using var response = await GetDownloadAsync(source.DownloadUri, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength is { } length && length != source.Bytes))
            throw new AppUpdateException("更新文件下载失败或大小不匹配。");
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await CopyLimitedAsync(input, output, source.Bytes, progress, token).ConfigureAwait(false);
            if (output.Length != source.Bytes) throw new AppUpdateException("更新下载不完整，请重试。");
        }
        if (!await UpdateFiles.MatchesAsync(path, new(Path.GetFileName(path), source.Bytes, source.Sha256), token).ConfigureAwait(false))
            throw new AppUpdateException("更新校验失败，请重新下载。");
    }

    internal static async Task ExtractComponentAsync(string zip, string payload, UpdateComponent component, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zip);
        var expected = component.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        if (archive.Entries.Count != expected.Count) throw UpdateFiles.Invalid();
        foreach (var entry in archive.Entries)
        {
            if (!expected.Remove(entry.FullName, out var file) || entry.Length != file.Bytes ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw UpdateFiles.Invalid();
            var path = UpdateFiles.Resolve(payload, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = entry.Open())
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await CopyLimitedAsync(source, destination, file.Bytes, null, token).ConfigureAwait(false);
            if (!await UpdateFiles.MatchesAsync(path, file, token).ConfigureAwait(false)) throw new AppUpdateException("解压后的更新文件校验失败。");
        }
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
