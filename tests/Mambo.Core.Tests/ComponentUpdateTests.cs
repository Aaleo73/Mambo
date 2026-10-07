using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;
using Mambo.Core.Updates;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class ComponentUpdateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReusesLocalComponentsAcrossSkippedVersionsAndKeepsApplicationRunningUntilApply()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var update = Assert.IsType<AppUpdate>(await service.CheckAsync(Token));
        Assert.NotNull(update.ComponentManifest);
        var prepared = await service.PrepareAsync(update, cancellationToken: Token);
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.DoesNotContain(fixture.Requests, static path => path.EndsWith("-mpv.zip", StringComparison.Ordinal));
        Assert.True(prepared.ReusedBytes > 0);
        Assert.Equal(fixture.Assets["Mambo-0.3.0-win-x64-update-app.zip"].Length, prepared.DownloadedBytes);
        await UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.Equal("keep my settings", File.ReadAllText(Path.Combine(fixture.App, "custom.ini")));
        Assert.Equal("uninstaller", File.ReadAllText(Path.Combine(fixture.App, "unins000.exe")));
        Assert.False(File.Exists(Path.Combine(fixture.App, "obsolete.dll")));
        Assert.Equal("user changed old file", File.ReadAllText(Path.Combine(fixture.App, "modified-old.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.App, UpdateFiles.TransactionDirectory)));
        Assert.False(fixture.SawAuthorization);
        service.Dispose();
        Assert.False(Directory.Exists(prepared.StagingDirectory));
        Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
    }

    [Fact]
    public async Task RepairsChangedLocalRuntimeInsteadOfTrustingInstalledManifest()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.App, "mpv", "libmpv-2.dll"), "corrupted runtime");
        using var service = fixture.Service();
        var prepared = await service.PrepareAsync((await service.CheckAsync(Token))!, cancellationToken: Token);
        Assert.Contains(fixture.Requests, static path => path.EndsWith("-mpv.zip", StringComparison.Ordinal));
        await UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token);
        Assert.Equal(fixture.NewFiles["mpv/libmpv-2.dll"], File.ReadAllBytes(Path.Combine(fixture.App, "mpv", "libmpv-2.dll")));
    }

    [Fact]
    public async Task LockedReplacementRollsBackAlreadyReplacedFilesAndCanRetry()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = await service.PrepareAsync((await service.CheckAsync(Token))!, cancellationToken: Token);
        using (var locked = new FileStream(Path.Combine(fixture.App, "blocked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
            Assert.Equal("old blocked", File.ReadAllText(Path.Combine(fixture.App, "blocked.dll")));
            Assert.False(Directory.Exists(Path.Combine(fixture.App, UpdateFiles.TransactionDirectory)));
            Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
        }
        await UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
    }

    [Fact]
    public async Task RechecksReusedFilesBeforeReplacingAnything()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = await service.PrepareAsync((await service.CheckAsync(Token))!, cancellationToken: Token);
        File.WriteAllText(Path.Combine(fixture.App, "mpv", "libmpv-2.dll"), "changed after preparation");
        await Assert.ThrowsAsync<AppUpdateException>(() => UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.False(Directory.Exists(Path.Combine(fixture.App, UpdateFiles.TransactionDirectory)));
    }

    [Fact]
    public async Task RefusesStalePreparedUpdateAfterAnotherUpgrade()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = await service.PrepareAsync((await service.CheckAsync(Token))!, cancellationToken: Token);
        var localManifest = Path.Combine(fixture.App, "release-manifest.json");
        File.WriteAllText(localManifest, File.ReadAllText(localManifest).Replace("0.1.0", "0.4.0", StringComparison.Ordinal));
        await Assert.ThrowsAsync<AppUpdateException>(() => UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
    }

    [Fact]
    public async Task InvalidInstalledMetadataProducesSafeErrorBeforeLaunchingHelper()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = await service.PrepareAsync((await service.CheckAsync(Token))!, cancellationToken: Token);
        File.WriteAllText(Path.Combine(fixture.App, "release-manifest.json"), "damaged local metadata");
        var error = await Assert.ThrowsAsync<AppUpdateException>(() => service.LaunchPreparedAsync(prepared, Token));
        Assert.Null(error.InnerException);
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
    }

    [Theory]
    [InlineData("manifest-hash")]
    [InlineData("package-hash")]
    [InlineData("truncated-package")]
    [InlineData("zip-traversal")]
    [InlineData("zip-duplicate")]
    [InlineData("zip-content")]
    [InlineData("zip-invalid")]
    [InlineData("version")]
    [InlineData("schema")]
    [InlineData("architecture")]
    public async Task InvalidRemoteContentNeverTouchesApplicationAndCleansPreparation(string kind)
    {
        using var fixture = new Fixture();
        var manifest = fixture.Manifest;
        if (kind == "version") manifest = manifest with { Version = "0.4.0" };
        if (kind == "schema") manifest = manifest with { SchemaVersion = 2 };
        if (kind == "architecture") manifest = manifest with { Architecture = "win-arm64" };
        if (kind.StartsWith("zip-", StringComparison.Ordinal))
        {
            var files = fixture.NewFiles.Where(static f => !f.Key.StartsWith("mpv/", StringComparison.Ordinal)).ToArray();
            var entries = kind switch
            {
                "zip-traversal" => files.Select((f, i) => new KeyValuePair<string, byte[]>(i == 0 ? "../outside" : f.Key, f.Value)),
                "zip-duplicate" => files.Select((f, i) => new KeyValuePair<string, byte[]>(i == 0 ? files[1].Key : f.Key, f.Value)),
                _ => files.Select((f, i) => new KeyValuePair<string, byte[]>(f.Key, i == 0 ? new byte[f.Value.Length] : f.Value)),
            };
            var zip = Fixture.Zip(entries);
            if (kind == "zip-invalid") zip = Encoding.UTF8.GetBytes("this is not a zip");
            fixture.Assets["Mambo-0.3.0-win-x64-update-app.zip"] = zip;
            manifest = manifest with { Components = [manifest.Components[0] with { Bytes = zip.Length, Sha256 = Fixture.Hash(zip) }, manifest.Components[1]] };
        }
        fixture.SetManifest(manifest);
        using var service = fixture.Service();
        var update = (await service.CheckAsync(Token))!;
        if (kind == "manifest-hash") fixture.Assets["Mambo-0.3.0-win-x64-update.json"][0] ^= 1;
        if (kind == "package-hash") fixture.Assets["Mambo-0.3.0-win-x64-update-app.zip"][0] ^= 1;
        if (kind == "truncated-package") fixture.Assets["Mambo-0.3.0-win-x64-update-app.zip"] = [1];
        await Assert.ThrowsAsync<AppUpdateException>(() => service.PrepareAsync(update, cancellationToken: Token));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.Empty(Directory.GetFiles(fixture.Cache, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/outside")]
    [InlineData("folder\\outside")]
    [InlineData("folder/./outside")]
    [InlineData("folder/../../outside")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/COM1")]
    [InlineData("folder/LPT².txt")]
    [InlineData("folder/name.")]
    [InlineData("folder/name ")]
    [InlineData("file:stream")]
    [InlineData("unins000.exe")]
    [InlineData(".mambo-update/backup")]
    [InlineData("Subtitles")]
    [InlineData("Subtitles/movie.srt")]
    [InlineData("sUbTiTlEs/item/nested.ass")]
    public void RejectsUnsafeWindowsPaths(string path) => Assert.Throws<AppUpdateException>(() => UpdateFiles.ValidateRelativePath(path));

    [Fact]
    public void RejectsCaseCollisionsAndFileDirectoryAliases()
    {
        using var fixture = new Fixture();
        var app = fixture.Manifest.Components[0];
        foreach (var path in new[] { "mambo.EXE", "Mambo.exe/child" })
        {
            var manifest = fixture.Manifest with
            { Components = [app with { Files = [.. app.Files, new(path, 1, new string('a', 64))] }, fixture.Manifest.Components[1]] };
            Assert.Throws<AppUpdateException>(() => UpdateFiles.Validate(manifest));
        }
    }

    [Fact]
    public async Task CancellationLeavesOriginalFilesAndNoStagedPayload()
    {
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var service = fixture.Service();
        var update = (await service.CheckAsync(Token))!;
        fixture.OnRequest = path => { if (path.EndsWith("-app.zip", StringComparison.Ordinal)) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(update, cancellationToken: cancellation.Token));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.Empty(Directory.GetFiles(fixture.Cache, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void InterruptedTransactionRestoresBackupsAndRemovesOnlyItsAddedFiles()
    {
        using var fixture = new Fixture();
        var transaction = Path.Combine(fixture.App, UpdateFiles.TransactionDirectory);
        Directory.CreateDirectory(Path.Combine(transaction, "backup"));
        File.Copy(Path.Combine(fixture.App, "Mambo.exe"), Path.Combine(transaction, "backup", "Mambo.exe"));
        File.WriteAllText(Path.Combine(transaction, "journal.json"), "{\"existing\":[\"Mambo.exe\"],\"added\":[\"new-file.dll\"]}");
        File.WriteAllText(Path.Combine(fixture.App, "Mambo.exe"), "partially updated");
        File.WriteAllText(Path.Combine(fixture.App, "new-file.dll"), "new file");
        UpdateTransaction.Recover(fixture.App);
        UpdateTransaction.Recover(fixture.App);
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.False(File.Exists(Path.Combine(fixture.App, "new-file.dll")));
        Assert.Equal("keep my settings", File.ReadAllText(Path.Combine(fixture.App, "custom.ini")));
        Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
    }

    [Fact]
    public void CommittedTransactionCleanupDoesNotUndoSuccessfulUpdate()
    {
        using var fixture = new Fixture();
        var transaction = Path.Combine(fixture.App, UpdateFiles.TransactionDirectory);
        Directory.CreateDirectory(transaction);
        File.WriteAllText(Path.Combine(transaction, "committed"), "1");
        File.WriteAllText(Path.Combine(transaction, "journal.json"), "malformed but already committed");
        UpdateTransaction.Recover(fixture.App);
        Assert.False(Directory.Exists(transaction));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
    }

    [Fact]
    public async Task UninstallCleanupIncludesFilesAddedByUpdatesAndKeepsCustomFiles()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = await service.PrepareAsync((await service.CheckAsync(Token))!, cancellationToken: Token);
        await UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token);
        UpdateFiles.RemoveInstalledFiles(fixture.App);
        Assert.False(File.Exists(Path.Combine(fixture.App, "added.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.App, "Mambo.Updater.exe")));
        Assert.Equal("keep my settings", File.ReadAllText(Path.Combine(fixture.App, "custom.ini")));
        Assert.Equal("uninstaller", File.ReadAllText(Path.Combine(fixture.App, "unins000.exe")));
        Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
    }

    [Theory]
    [InlineData("Subtitles/movie.srt")]
    [InlineData("sUbTiTlEs/item/nested.ass")]
    [InlineData("Subtitles")]
    public async Task ReservedSubtitleManifestIsRejectedBeforeDownloadOrApply(string relative)
    {
        using var fixture = new Fixture();
        var app = fixture.Manifest.Components[0];
        var invalid = fixture.Manifest with
        { Components = [app with { Files = [.. app.Files, Fixture.FileRecord(relative, Encoding.UTF8.GetBytes("replacement"))] }, fixture.Manifest.Components[1]] };
        fixture.SetManifest(invalid);
        using var service = fixture.Service();
        var update = (await service.CheckAsync(Token))!;
        await Assert.ThrowsAsync<AppUpdateException>(() => service.PrepareAsync(update, cancellationToken: Token));
        Assert.DoesNotContain(fixture.Requests, static path => path.EndsWith(".zip", StringComparison.Ordinal));
        var staging = Path.Combine(fixture.Cache, "invalid-plan");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "update.json"), fixture.Assets["Mambo-0.3.0-win-x64-update.json"]);
        await Assert.ThrowsAsync<AppUpdateException>(() => UpdateTransaction.ApplyAsync(fixture.App, staging, Token));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
        Assert.False(Directory.Exists(Path.Combine(fixture.App, UpdateFiles.TransactionDirectory)));
    }

    [Fact]
    public async Task InstalledManifestCannotClaimUserSubtitlesForObsoleteRemovalOrUninstall()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = await service.PrepareAsync((await service.CheckAsync(Token))!, cancellationToken: Token);
        var manifestPath = Path.Combine(fixture.App, "release-manifest.json");
        var installed = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), ComponentTestJsonContext.Default.TestInstalledRelease)!;
        var invalid = installed with
        { Files = [.. installed.Files, Fixture.FileRecord("Subtitles/movie.srt", File.ReadAllBytes(fixture.SavedSubtitle))] };
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(invalid, ComponentTestJsonContext.Default.TestInstalledRelease));
        await Assert.ThrowsAsync<AppUpdateException>(() => UpdateTransaction.ApplyAsync(fixture.App, prepared.StagingDirectory, Token));
        Assert.Throws<AppUpdateException>(() => UpdateFiles.RemoveInstalledFiles(fixture.App));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
        Assert.True(File.Exists(manifestPath));
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("added")]
    public void RecoveryRejectsReservedSubtitlePathsBeforeAnyMutation(string kind)
    {
        using var fixture = new Fixture();
        var transaction = Path.Combine(fixture.App, UpdateFiles.TransactionDirectory);
        Directory.CreateDirectory(Path.Combine(transaction, "backup", "Subtitles"));
        File.WriteAllText(Path.Combine(transaction, "backup", "Subtitles", "movie.srt"), "replacement");
        File.WriteAllText(Path.Combine(transaction, "backup", "Mambo.exe"), "replacement app");
        var json = kind == "existing"
            ? "{\"existing\":[\"Mambo.exe\",\"Subtitles/movie.srt\"],\"added\":[]}"
            : "{\"existing\":[\"Mambo.exe\"],\"added\":[\"Subtitles/movie.srt\"]}";
        File.WriteAllText(Path.Combine(transaction, "journal.json"), json);
        Assert.Throws<AppUpdateException>(() => UpdateTransaction.Recover(fixture.App));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(fixture.App, "Mambo.exe")));
        Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
    }

    [Fact]
    public void CompletedUpdateCacheCleanupKeepsApplicationSubtitleTree()
    {
        using var fixture = new Fixture();
        // Use the application root as the cache to exercise the cleanup boundary in the same tree.
        var completed = Path.Combine(fixture.App, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(completed);
        File.WriteAllText(Path.Combine(completed, "result"), "success");
        File.SetLastWriteTimeUtc(Path.Combine(completed, "result"), DateTime.UtcNow.AddMinutes(-2));
        var subtitleDirectory = Path.Combine(fixture.App, "Subtitles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subtitleDirectory);
        File.WriteAllText(Path.Combine(subtitleDirectory, "result"), "success");
        File.SetLastWriteTimeUtc(Path.Combine(subtitleDirectory, "result"), DateTime.UtcNow.AddMinutes(-2));
        File.WriteAllText(Path.Combine(subtitleDirectory, "episode.ass"), "persistent subtitle");
        using var service = new GitHubUpdateService("example/Mambo", "0.1.0", fixture.App, applicationDirectory: fixture.App);
        Assert.False(Directory.Exists(completed));
        Assert.Equal("saved subtitle", File.ReadAllText(fixture.SavedSubtitle));
        Assert.Equal("persistent subtitle", File.ReadAllText(Path.Combine(subtitleDirectory, "episode.ass")));
    }

    [Fact]
    public async Task NoManifestStillOffersVerifiedInstallerForLegacyReleases()
    {
        using var fixture = new Fixture();
        fixture.Assets.Remove("Mambo-0.3.0-win-x64-update.json");
        using var service = fixture.Service();
        var update = (await service.CheckAsync(Token))!;
        Assert.Null(update.ComponentManifest);
        Assert.NotNull(await service.DownloadAsync(update, cancellationToken: Token));
    }

    private sealed class Fixture : IDisposable
    {
        private const string Version = "0.3.0";
        private readonly string root = Path.Combine(Path.GetTempPath(), "Mambo-component-tests", Guid.NewGuid().ToString("N"));
        public string App => Path.Combine(root, "app");
        public string Cache => Path.Combine(root, "cache");
        public string SavedSubtitle => Path.Combine(App, "Subtitles", "movie.srt");
        public Dictionary<string, byte[]> NewFiles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public Action<string>? OnRequest { get; set; }
        public bool SawAuthorization { get; private set; }
        public ComponentUpdateManifest Manifest { get; private set; }

        public Fixture()
        {
            var runtime = new byte[32768];
            Random.Shared.NextBytes(runtime);
            var old = new Dictionary<string, byte[]>
            {
                ["Mambo.exe"] = Encoding.UTF8.GetBytes("old app"),
                ["Mambo.Updater.exe"] = Encoding.UTF8.GetBytes("fixture helper, never executable"),
                ["blocked.dll"] = Encoding.UTF8.GetBytes("old blocked"),
                ["mpv/libmpv-2.dll"] = runtime,
                ["obsolete.dll"] = Encoding.UTF8.GetBytes("old obsolete"),
                ["modified-old.dll"] = Encoding.UTF8.GetBytes("old modified"),
            };
            foreach (var pair in old) Write(pair.Key, pair.Value);
            Write("release-manifest.json", JsonSerializer.SerializeToUtf8Bytes(new TestInstalledRelease("0.1.0", old.Select(static p => FileRecord(p.Key, p.Value)).ToArray()), ComponentTestJsonContext.Default.TestInstalledRelease));
            Write("custom.ini", Encoding.UTF8.GetBytes("keep my settings"));
            Write("Subtitles/movie.srt", Encoding.UTF8.GetBytes("saved subtitle"));
            Write("unins000.exe", Encoding.UTF8.GetBytes("uninstaller"));
            Write("modified-old.dll", Encoding.UTF8.GetBytes("user changed old file"));
            foreach (var pair in old.Where(static p => p.Key is not ("obsolete.dll" or "modified-old.dll"))) NewFiles.Add(pair.Key, pair.Value);
            NewFiles["Mambo.exe"] = Encoding.UTF8.GetBytes("new app");
            NewFiles["blocked.dll"] = Encoding.UTF8.GetBytes("new blocked");
            NewFiles["added.dll"] = Encoding.UTF8.GetBytes("added by update");
            NewFiles["release-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new TestInstalledRelease(Version, NewFiles.Select(static p => FileRecord(p.Key, p.Value)).ToArray()), ComponentTestJsonContext.Default.TestInstalledRelease);
            var components = new List<UpdateComponent>();
            foreach (var name in new[] { "app", "mpv" })
            {
                var selected = NewFiles.Where(p => p.Key.StartsWith("mpv/", StringComparison.Ordinal) == (name == "mpv")).ToArray();
                var zip = Zip(selected);
                Assets[$"Mambo-{Version}-win-x64-update-{name}.zip"] = zip;
                components.Add(new(name, zip.Length, Hash(zip), selected.Select(static p => FileRecord(p.Key, p.Value)).ToArray()));
            }
            Manifest = new(1, Version, "win-x64", components.ToArray());
            SetManifest(Manifest);
            Assets[$"Mambo-{Version}-win-x64-setup.exe"] = Encoding.UTF8.GetBytes("fixture setup, never executable");
        }

        public void SetManifest(ComponentUpdateManifest manifest)
        {
            Manifest = manifest;
            Assets[$"Mambo-{Version}-win-x64-update.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, ComponentTestJsonContext.Default.ComponentUpdateManifest);
        }

        public GitHubUpdateService Service() => new("example/Mambo", "0.1.0", Cache, new Handler(this), App);

        private void Write(string relative, byte[] bytes)
        {
            var path = Path.Combine(App, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public static UpdateFile FileRecord(string path, byte[] bytes) => new(path, bytes.Length, Hash(bytes));
        public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public static byte[] Zip(IEnumerable<KeyValuePair<string, byte[]>> entries)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var pair in entries)
                {
                    using var entry = zip.CreateEntry(pair.Key).Open();
                    entry.Write(pair.Value);
                }
            return stream.ToArray();
        }

        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fixture.SawAuthorization |= request.Headers.Authorization is not null;
                var uri = request.RequestUri!;
                fixture.Requests.Add(uri.AbsolutePath);
                fixture.OnRequest?.Invoke(uri.AbsolutePath);
                if (uri.Host == "api.github.com")
                {
                    var assets = fixture.Assets.Select(p => $$"""{"name":"{{p.Key}}","state":"uploaded","size":{{p.Value.Length}},"digest":"sha256:{{Hash(p.Value)}}","browser_download_url":"https://github.com/example/Mambo/releases/download/v{{Version}}/{{p.Key}}"}""");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"tag_name":"v{{Version}}","draft":false,"prerelease":false,"assets":[{{string.Join(',', assets)}}]}""") });
                }
                var bytes = fixture.Assets[Path.GetFileName(uri.AbsolutePath)];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
        }
    }
}

internal sealed record TestInstalledRelease(string Version, UpdateFile[] Files);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ComponentUpdateManifest))]
[JsonSerializable(typeof(TestInstalledRelease))]
internal sealed partial class ComponentTestJsonContext : JsonSerializerContext;
