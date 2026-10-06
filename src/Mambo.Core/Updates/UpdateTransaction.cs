using System.Text.Json;
using Mambo.Core.Contracts;

namespace Mambo.Core.Updates;

/// <summary>只改动发布清单中的应用文件。先备份并落盘日志，再替换；失败时恢复。</summary>
public static class UpdateTransaction
{
    public static async Task VerifyAsync(string applicationDirectory, string stagingDirectory, CancellationToken token = default)
    {
        UpdateFiles.EnsurePlainPath(applicationDirectory);
        UpdateFiles.EnsurePlainPath(stagingDirectory);
        var manifest = UpdateFiles.ReadManifest(Path.Combine(stagingDirectory, "update.json"));
        var payload = Path.Combine(stagingDirectory, "payload");
        foreach (var file in manifest.Components.SelectMany(static c => c.Files))
        {
            var staged = UpdateFiles.Resolve(payload, file.Path);
            var source = File.Exists(staged) ? staged : UpdateFiles.Resolve(applicationDirectory, file.Path);
            if (!await UpdateFiles.MatchesAsync(source, file, token).ConfigureAwait(false))
                throw new AppUpdateException("更新文件发生变化或不完整，请重新下载。");
        }
    }

    public static async Task ApplyAsync(string applicationDirectory, string stagingDirectory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(applicationDirectory);
        var transaction = Path.Combine(root, UpdateFiles.TransactionDirectory);
        UpdateFiles.EnsurePlainPath(transaction);
        if (Directory.Exists(transaction)) throw new AppUpdateException("上次更新尚未恢复，请先恢复后重试。");
        await VerifyAsync(root, stagingDirectory, token).ConfigureAwait(false);
        var manifest = UpdateFiles.ReadManifest(Path.Combine(stagingDirectory, "update.json"));
        var files = manifest.Components.SelectMany(static c => c.Files).ToArray();
        var targetPaths = files.Select(static f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<string>();
        foreach (var file in files)
            if (!await UpdateFiles.MatchesAsync(UpdateFiles.Resolve(root, file.Path), file, token).ConfigureAwait(false)) changes.Add(file.Path);
        // Obsolete shipped files are removed only when they still match the old release.
        // Custom files, the uninstaller and user-modified old files are preserved.
        var obsolete = new List<string>();
        var installedPath = UpdateFiles.Resolve(root, "release-manifest.json");
        if (File.Exists(installedPath))
        {
            if (new FileInfo(installedPath).Length > UpdateFiles.MaximumManifestBytes) throw UpdateFiles.Invalid();
            var installed = JsonSerializer.Deserialize(File.ReadAllBytes(installedPath), ComponentJsonContext.Default.InstalledReleaseManifest);
            if (installed?.Files is not { Length: <= 10000 }) throw UpdateFiles.Invalid();
            if (!ReleaseVersion.TryParse(installed.Version, out var installedVersion) ||
                !ReleaseVersion.TryParse(manifest.Version, out var targetVersion) || targetVersion.CompareTo(installedVersion) <= 0)
                throw new AppUpdateException("这个更新已过期，请重新检查更新。");
            foreach (var file in installed.Files)
            {
                UpdateFiles.ValidateFile(file);
                if (!targetPaths.Contains(file.Path) && await UpdateFiles.MatchesAsync(UpdateFiles.Resolve(root, file.Path), file, token).ConfigureAwait(false))
                    obsolete.Add(file.Path);
            }
        }
        var touched = changes.Concat(obsolete).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = touched.Where(path => File.Exists(UpdateFiles.Resolve(root, path))).ToArray();
        var added = touched.Except(existing, StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(transaction);
        try
        {
            foreach (var relative in existing)
            {
                token.ThrowIfCancellationRequested();
                var backup = UpdateFiles.Resolve(Path.Combine(transaction, "backup"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(UpdateFiles.Resolve(root, relative), backup);
            }
            foreach (var relative in changes)
            {
                token.ThrowIfCancellationRequested();
                var next = UpdateFiles.Resolve(Path.Combine(transaction, "next"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(next)!);
                File.Copy(UpdateFiles.Resolve(Path.Combine(stagingDirectory, "payload"), relative), next);
            }
            // No application file is touched until every backup and replacement is ready.
            WriteDurable(Path.Combine(transaction, "journal.tmp"), JsonSerializer.SerializeToUtf8Bytes(new UpdateJournal(existing, added), ComponentJsonContext.Default.UpdateJournal));
            File.Move(Path.Combine(transaction, "journal.tmp"), Path.Combine(transaction, "journal.json"));
            foreach (var relative in changes)
            {
                token.ThrowIfCancellationRequested();
                var destination = UpdateFiles.Resolve(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(UpdateFiles.Resolve(Path.Combine(transaction, "next"), relative), destination, overwrite: true);
            }
            foreach (var relative in obsolete) File.Delete(UpdateFiles.Resolve(root, relative));
            foreach (var file in files)
                if (!await UpdateFiles.MatchesAsync(UpdateFiles.Resolve(root, file.Path), file, token).ConfigureAwait(false))
                    throw new AppUpdateException("更新后校验失败，正在恢复原版本。");
            WriteDurable(Path.Combine(transaction, "committed"), [1]);
        }
        catch
        {
            Recover(root);
            throw;
        }
        // Cleanup failure must never roll back a committed update.
        TryClean(transaction);
    }

    public static void Recover(string applicationDirectory)
    {
        var root = Path.GetFullPath(applicationDirectory);
        var transaction = Path.Combine(root, UpdateFiles.TransactionDirectory);
        UpdateFiles.EnsurePlainPath(transaction);
        if (!Directory.Exists(transaction)) return;
        var journalPath = Path.Combine(transaction, "journal.json");
        if (!File.Exists(Path.Combine(transaction, "committed")) && File.Exists(journalPath))
        {
            UpdateFiles.EnsurePlainPath(journalPath);
            if (new FileInfo(journalPath).Length > UpdateFiles.MaximumManifestBytes) throw UpdateFiles.Invalid();
            var journal = JsonSerializer.Deserialize(File.ReadAllBytes(journalPath), ComponentJsonContext.Default.UpdateJournal);
            if (journal?.Existing is not { Length: <= 10000 } || journal.Added is not { Length: <= 10000 }) throw UpdateFiles.Invalid();
            foreach (var relative in journal.Existing)
            {
                var backup = UpdateFiles.Resolve(Path.Combine(transaction, "backup"), relative);
                var destination = UpdateFiles.Resolve(root, relative);
                // Leave unchanged/locked files alone; a failure may have happened before their replacement.
                if (File.Exists(destination) && FilesEqual(backup, destination)) continue;
                File.Copy(backup, destination, overwrite: true);
            }
            foreach (var relative in journal.Added) File.Delete(UpdateFiles.Resolve(root, relative));
        }
        Clean(transaction);
    }

    private static bool FilesEqual(string first, string second)
    {
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        return a.Length == b.Length && System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    internal static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static void TryClean(string directory)
    {
        try { Clean(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppUpdateException) { }
    }

    private static void Clean(string directory)
    {
        if (!Directory.Exists(directory)) return;
        UpdateFiles.EnsurePlainPath(directory);
        // Never follow a reparse point, including one added during an interrupted update.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            UpdateFiles.EnsurePlainPath(entry);
            if (Directory.Exists(entry)) Clean(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }
}
