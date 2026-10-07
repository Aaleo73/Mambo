using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Mambo.Core.Session;

namespace Mambo.Core.Subtitles;

/// <summary>软件目录下的账号隔离字幕资料；所有失败静默，仅发出稳定错误码。</summary>
public sealed class LocalSubtitleLibrary : IDisposable
{
    private const int ItemCapacity = 32;
    private readonly string root;
    private readonly AccountContext accounts;
    private readonly ILocalSubtitleTargetResolver targets;
    private readonly Action<AppError>? log;
    private readonly SemaphoreSlim writer = new(1, 1);

    public LocalSubtitleLibrary(string subtitleRoot, AccountContext accounts, ILocalSubtitleTargetResolver targets, Action<AppError>? log = null)
    {
        root = Path.GetFullPath(subtitleRoot);
        this.accounts = accounts;
        this.targets = targets;
        this.log = log;
    }

    public async Task<LocalSubtitleImportResult> ImportAsync(AccountSession account, PlaybackEntry context,
        ImmutableArray<string> sourcePaths, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, account.Token);
        var cancellation = linked.Token;
        RequireCurrent(account, cancellation);
        if (sourcePaths.IsDefaultOrEmpty) return LocalSubtitleImportResult.Empty;
        var candidates = ImmutableArray.CreateBuilder<SubtitleImportCandidate>();
        for (var index = 0; index < sourcePaths.Length; index++)
        {
            var source = sourcePaths[index];
            if (!ValidSource(source)) { Report("subtitles.invalid_file"); continue; }
            candidates.Add(new(index, Path.GetFileName(source), sourcePaths.Length));
        }
        if (candidates.Count == 0) return LocalSubtitleImportResult.Empty;
        ImmutableDictionary<int, string> mapped;
        try { mapped = await targets.ResolveAsync(account, context, candidates.ToImmutable(), cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsExpected(error)) { Report("subtitles.target_unavailable"); return LocalSubtitleImportResult.Empty; }
        var results = ImmutableArray.CreateBuilder<LocalSubtitleImportEntry>();
        foreach (var candidate in candidates)
        {
            RequireCurrent(account, cancellation);
            if (!mapped.TryGetValue(candidate.InputIndex, out var itemId) || !LocalSubtitleTargetResolver.ValidId(itemId)) continue;
            string? temporary = null;
            try
            {
                var directory = AccountDirectory(account);
                EnsureDirectories(directory);
                temporary = Path.Combine(directory, "files", Guid.NewGuid().ToString("N") + ".tmp");
                var hash = await CopyAsync(sourcePaths[candidate.InputIndex], temporary, cancellation).ConfigureAwait(false);
                RequireCurrent(account, cancellation);
                await writer.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    RequireCurrent(account, cancellation);
                    var loaded = Load(directory);
                    if (loaded is null) { Report("subtitles.index_invalid"); continue; }
                    var document = loaded.Document;
                    var binding = document.Items.GetValueOrDefault(itemId) ?? new();
                    var existing = binding.SubtitleIds.Select(id => document.Files[id]).FirstOrDefault(file => file.Sha256 == hash);
                    if (existing is not null)
                    {
                        await EnsureBlobAsync(temporary, directory, existing, cancellation).ConfigureAwait(false);
                        results.Add(new(itemId, Describe(directory, existing)));
                        continue;
                    }
                    if (binding.SubtitleIds.Length >= ItemCapacity) { Report("subtitles.capacity_reached"); continue; }
                    var format = Format(sourcePaths[candidate.InputIndex]);
                    var file = document.Files.Values.FirstOrDefault(value => value.Sha256 == hash && value.Format == format) ?? new()
                    {
                        Id = Guid.NewGuid().ToString("N"), DisplayName = DisplayName(candidate.FileName),
                        Format = format, Sha256 = hash, RelativePath = "files/" + hash + "." + format,
                    };
                    var changed = document with
                    {
                        Files = new(document.Files, StringComparer.Ordinal) { [file.Id] = file },
                        Items = new(document.Items, StringComparer.Ordinal)
                        { [itemId] = binding with { SubtitleIds = [.. binding.SubtitleIds, file.Id] } },
                    };
                    var newBlob = !File.Exists(ManagedPath(directory, file));
                    try
                    {
                        await EnsureBlobAsync(temporary, directory, file, cancellation).ConfigureAwait(false);
                        RequireCurrent(account, cancellation);
                        await SaveAsync(directory, changed, loaded.Recovered, cancellation).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 仍持有写锁：只回收本次新增且旧索引从未引用的副本，不能删共享或既有文件。
                        if (newBlob && !document.Files.ContainsKey(file.Id)) DeleteUncommitted(ManagedPath(directory, file));
                        throw;
                    }
                    results.Add(new(itemId, Describe(directory, file)));
                }
                finally { writer.Release(); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsExpected(error)) { Report("subtitles.import_failed"); }
            finally { DeleteUncommitted(temporary); }
        }
        return new(results.ToImmutable());
    }

    public async Task<LocalSubtitleItem> GetForItemAsync(AccountSession account, string itemId, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, account.Token);
        var cancellation = linked.Token;
        RequireCurrent(account, cancellation);
        if (!LocalSubtitleTargetResolver.ValidId(itemId)) return LocalSubtitleItem.Empty;
        await writer.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireCurrent(account, cancellation);
            var directory = AccountDirectory(account);
            var loaded = Load(directory);
            if (loaded is null) { Report("subtitles.index_invalid"); return LocalSubtitleItem.Empty; }
            if (!loaded.Document.Items.TryGetValue(itemId, out var binding)) return LocalSubtitleItem.Empty;
            var files = ImmutableArray.CreateBuilder<LocalSubtitleFileInfo>();
            foreach (var id in binding.SubtitleIds)
            {
                var file = loaded.Document.Files[id];
                if (await MatchesAsync(ManagedPath(directory, file), file.Sha256, cancellation).ConfigureAwait(false))
                    files.Add(Describe(directory, file));
                else Report("subtitles.file_unavailable");
            }
            RequireCurrent(account, cancellation);
            return new(files.ToImmutable(), files.Any(file => file.Id == binding.AdoptedSubtitleId) ? binding.AdoptedSubtitleId : null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsExpected(error)) { Report("subtitles.load_failed"); return LocalSubtitleItem.Empty; }
        finally { writer.Release(); }
    }

    /// <summary>调用方已经按用户操作代际裁决采用顺序；ImportAsync 不自行修改此字段。</summary>
    public async Task SetAdoptedAsync(AccountSession account, string itemId, string subtitleId, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, account.Token);
        var cancellation = linked.Token;
        RequireCurrent(account, cancellation);
        if (!LocalSubtitleTargetResolver.ValidId(itemId)) return;
        await writer.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireCurrent(account, cancellation);
            var directory = AccountDirectory(account);
            var loaded = Load(directory);
            if (loaded is null) { Report("subtitles.index_invalid"); return; }
            if (!loaded.Document.Items.TryGetValue(itemId, out var binding) || !binding.SubtitleIds.Contains(subtitleId, StringComparer.Ordinal) ||
                binding.AdoptedSubtitleId == subtitleId) return;
            var changed = loaded.Document with { Items = new(loaded.Document.Items, StringComparer.Ordinal)
            { [itemId] = binding with { AdoptedSubtitleId = subtitleId } } };
            RequireCurrent(account, cancellation);
            await SaveAsync(directory, changed, loaded.Recovered, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsExpected(error)) { Report("subtitles.adopt_failed"); }
        finally { writer.Release(); }
    }

    private void RequireCurrent(AccountSession account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(accounts.Current, account)) throw new OperationCanceledException("字幕操作的账号已改变。", token);
    }

    private string AccountDirectory(AccountSession account)
    {
        if (!Hex(account.Scope, 64)) throw new InvalidDataException();
        var directory = Path.Combine(root, account.Scope);
        CheckDirectory(root); CheckDirectory(directory); CheckDirectory(Path.Combine(directory, "files"));
        return directory;
    }

    private static void EnsureDirectories(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "files"));
        CheckDirectory(directory); CheckDirectory(Path.Combine(directory, "files"));
    }

    private static void CheckDirectory(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException();
    }

    private static bool ValidSource(string? path)
    {
        try { return path is not null && Path.IsPathFullyQualified(path) && Supported(Format(path)); }
        catch (ArgumentException) { return false; }
    }
    private static string Format(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    private static bool Supported(string format) => format is "srt" or "ass" or "ssa" or "vtt";
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(char.IsAsciiHexDigit);
    private static string DisplayName(string name) => new(name.Where(character => !char.IsControl(character)).Take(96).ToArray());
    private static string ManagedPath(string directory, LocalSubtitleStoredFile file) => Path.Combine(directory, "files", file.Sha256 + "." + file.Format);
    private static LocalSubtitleFileInfo Describe(string directory, LocalSubtitleStoredFile file) => new(file.Id, file.DisplayName, file.Format, ManagedPath(directory, file));

    private static async Task<string> CopyAsync(string source, string temporary, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            digest.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static async Task EnsureBlobAsync(string temporary, string directory, LocalSubtitleStoredFile file, CancellationToken token)
    {
        var destination = ManagedPath(directory, file);
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        if (await MatchesAsync(destination, file.Sha256, token).ConfigureAwait(false)) return;
        token.ThrowIfCancellationRequested();
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }

    private static async Task<bool> MatchesAsync(string path, string expectedHash, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
            return Convert.ToHexStringLower(digest).Equals(expectedHash, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static LoadedIndex? Load(string directory)
    {
        var path = Path.Combine(directory, "index.json");
        if (ReadDocument(path) is { } primary) return new(primary, false);
        if (ReadDocument(path + ".bak") is { } backup) return new(backup, true);
        return !File.Exists(path) && !Directory.Exists(path) && !File.Exists(path + ".bak") && !Directory.Exists(path + ".bak")
            ? new(new(), false) : null;
    }

    private static LocalSubtitleIndexDocument? ReadDocument(string path)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var value = JsonSerializer.Deserialize(input, LocalSubtitleJsonContext.Default.LocalSubtitleIndexDocument);
            if (value is not { Version: 1, Files: not null, Items: not null }) return null;
            foreach (var (id, file) in value.Files)
                if (file is null || !Hex(id, 32) || file.Id != id || !Supported(file.Format) || !Hex(file.Sha256, 64) ||
                    file.Sha256.Any(character => character is >= 'A' and <= 'F') || file.RelativePath != "files/" + file.Sha256 + "." + file.Format ||
                    string.IsNullOrWhiteSpace(file.DisplayName) || file.DisplayName.Length > 96 || file.DisplayName.Any(char.IsControl) ||
                    file.DisplayName.IndexOfAny(['/', '\\']) >= 0) return null;
            foreach (var (itemId, binding) in value.Items)
                if (!LocalSubtitleTargetResolver.ValidId(itemId) || binding?.SubtitleIds is null || binding.SubtitleIds.Length is 0 or > ItemCapacity ||
                    binding.SubtitleIds.Distinct(StringComparer.Ordinal).Count() != binding.SubtitleIds.Length ||
                    binding.SubtitleIds.Any(id => id is null || !value.Files.ContainsKey(id)) ||
                    binding.AdoptedSubtitleId is { } adopted && !binding.SubtitleIds.Contains(adopted, StringComparer.Ordinal)) return null;
            return value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private static Task SaveAsync(string directory, LocalSubtitleIndexDocument document, bool recovered, CancellationToken token) =>
        AtomicFile.WriteAsync(Path.Combine(directory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(document, LocalSubtitleJsonContext.Default.LocalSubtitleIndexDocument), backup: !recovered, cancellationToken: token);
    private static bool IsExpected(Exception error) => error is AppException or IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or System.Security.SecurityException;
    private void Report(string code) => log?.Invoke(new(AppErrorKind.Persistence, code, "", false));
    private static void DeleteUncommitted(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() => writer.Dispose();
    private sealed record LoadedIndex(LocalSubtitleIndexDocument Document, bool Recovered);
}
