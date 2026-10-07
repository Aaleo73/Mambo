using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;

namespace Mambo.Core.Updates;

public sealed record UpdateFile(string Path, long Bytes, string Sha256);
public sealed record UpdateComponent(string Name, long Bytes, string Sha256, UpdateFile[] Files);
public sealed record ComponentUpdateManifest(int SchemaVersion, string Version, string Architecture, UpdateComponent[] Components);
internal sealed record InstalledReleaseManifest(string Version, UpdateFile[] Files);
public sealed record UpdateRequest(string ApplicationDirectory, int ParentProcessId, long ParentStartTimeUtcTicks, string ManifestSha256);
internal sealed record UpdateJournal(string[] Existing, string[] Added);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ComponentUpdateManifest))]
[JsonSerializable(typeof(InstalledReleaseManifest))]
[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(UpdateJournal))]
internal sealed partial class ComponentJsonContext : JsonSerializerContext;

/// <summary>发布清单和磁盘路径共用一套限制，下载器与独立更新进程均需验证。</summary>
public static class UpdateFiles
{
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const int MaximumManifestBytes = 2 * 1024 * 1024;
    public const string TransactionDirectory = ".mambo-update";
    public const string LockFile = ".mambo-update.lock";
    internal static readonly string[] ComponentNames = ["app", "runtime", "mpv", "assets", "licenses"];

    public static UpdateRequest ReadRequest(string directory)
    {
        var path = Resolve(directory, "request.json");
        if (new FileInfo(path).Length > 16384) throw Invalid();
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), ComponentJsonContext.Default.UpdateRequest) ?? throw Invalid();
    }

    public static void WriteRequest(string directory, UpdateRequest request) =>
        File.WriteAllBytes(Resolve(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request, ComponentJsonContext.Default.UpdateRequest));

    public static void RemoveInstalledFiles(string directory)
    {
        var root = Path.GetFullPath(directory);
        var lockPath = Path.Combine(root, LockFile);
        EnsurePlainPath(lockPath);
        using var updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Resolve(root, "release-manifest.json");
        if (new FileInfo(path).Length > MaximumManifestBytes) throw Invalid();
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(path), ComponentJsonContext.Default.InstalledReleaseManifest);
        if (manifest?.Files is not { Length: <= 10000 }) throw Invalid();
        foreach (var file in manifest.Files) ValidateFile(file);
        foreach (var file in manifest.Files)
        {
            // Inno owns the helper and removes it after this process returns.
            if (file.Path == "Mambo.Updater.exe") continue;
            var target = Resolve(root, file.Path);
            if (MatchesAsync(target, file).GetAwaiter().GetResult()) File.Delete(target);
        }
        File.Delete(path);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relative = file.Path;
            while (relative.LastIndexOf('/') is var index && index >= 0)
            {
                relative = relative[..index];
                directories.Add(relative);
            }
        }
        foreach (var relative in directories.OrderByDescending(static p => p.Length))
        {
            var folder = Resolve(root, relative);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        updateLock.Dispose();
        File.Delete(lockPath);
    }

    public static ComponentUpdateManifest ReadManifest(string path)
    {
        if (new FileInfo(path).Length > MaximumManifestBytes) throw Invalid();
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(path), ComponentJsonContext.Default.ComponentUpdateManifest);
        Validate(manifest);
        return manifest!;
    }

    public static void Validate(ComponentUpdateManifest? manifest)
    {
        if (manifest is null || manifest.SchemaVersion != 1 || manifest.Architecture != "win-x64" ||
            !IsStableVersion(manifest.Version) || manifest.Components is not { Length: > 0 and <= 5 }) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var component in manifest.Components)
        {
            if (component is null || !ComponentNames.Contains(component.Name, StringComparer.Ordinal) || !names.Add(component.Name) ||
                component.Bytes is <= 0 or > MaximumFileBytes || !IsHash(component.Sha256) || component.Files is not { Length: > 0 }) throw Invalid();
            foreach (var file in component.Files)
            {
                ValidateFile(file);
                if (!paths.Add(file.Path) || paths.Count > 10000) throw Invalid();
                total += file.Bytes;
                if (total > 2L * 1024 * 1024 * 1024) throw Invalid();
            }
        }
        // A file cannot also be another file's directory (including case aliases on Windows).
        foreach (var path in paths)
        {
            var parent = path;
            while (parent.LastIndexOf('/') is var index && index >= 0)
            {
                parent = parent[..index];
                if (paths.Contains(parent)) throw Invalid();
            }
        }
        if (!paths.Contains("Mambo.exe") || !paths.Contains("Mambo.Updater.exe") || !paths.Contains("release-manifest.json")) throw Invalid();
    }

    internal static void ValidateFile(UpdateFile? file)
    {
        if (file is null || file.Bytes is < 0 or > MaximumFileBytes || !IsHash(file.Sha256)) throw Invalid();
        ValidateRelativePath(file.Path);
    }

    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.Any(c => c < 32 || ":*?\"<>|".Contains(c))) throw Invalid();
        // Persistent user subtitles live beside Mambo.exe, including in portable installations.
        if (path.Equals("Subtitles", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Subtitles/", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.StartsWith(".mambo-update", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 "123456789¹²³".Contains(stem[3]))) throw Invalid();
        }
    }

    public static string Resolve(string directory, string relative)
    {
        ValidateRelativePath(relative);
        var root = Path.GetFullPath(directory);
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        EnsurePlainPath(result);
        return result;
    }

    public static void EnsurePlainPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AppUpdateException("更新目录包含链接，无法安全更新。请从发行页面获取新版。");
    }

    public static async Task<bool> MatchesAsync(string path, UpdateFile file, CancellationToken token = default)
    {
        EnsurePlainPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHash(string? hash) => hash?.Length == 64 && hash.All(char.IsAsciiHexDigit);
    internal static bool IsStableVersion(string? version) => version is not null && ReleaseVersion.TryParse(version, out var parsed) &&
        !parsed.Prerelease && !version.StartsWith('v') && !version.Contains('+');
    internal static AppUpdateException Invalid() => new("更新清单无效，已取消更新。");
}
