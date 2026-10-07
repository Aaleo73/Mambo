using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Mambo.Core.Contracts;
using Mambo.Core.Session;
using Mambo.Core.Subtitles;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class LocalSubtitleLibraryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ImportedCopySurvivesSourceRemovalAndLibraryRestartWithoutLeakingPaths()
    {
        using var test = new Fixture();
        var source = await test.SourceAsync("字幕.srt", "1\n00:00:00,000 --> 00:00:02,000\n合成字幕\n");
        using var library = test.Library();
        var imported = Assert.Single((await library.ImportAsync(test.Account, test.Entry, [source], Token)).Items);
        Assert.Null((await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).AdoptedSubtitleId);
        await library.SetAdoptedAsync(test.Account, test.Entry.ItemId, imported.File.Id, Token);
        var expected = await File.ReadAllTextAsync(source, Token);
        File.Delete(source);
        using var reopened = test.Library();
        var restored = await reopened.GetForItemAsync(test.Account, test.Entry.ItemId, Token);
        Assert.Equal(expected, await File.ReadAllTextAsync(Assert.Single(restored.Files).ManagedPath, Token));
        Assert.Equal(imported.File.Id, restored.AdoptedSubtitleId);
        var index = await File.ReadAllTextAsync(test.IndexPath, Token);
        Assert.DoesNotContain(test.SourceDirectory, index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(test.Account.Secret.ServerAddress, index, StringComparison.Ordinal);
        Assert.DoesNotContain(test.Account.Secret.AccessToken, index, StringComparison.Ordinal);
        Assert.DoesNotContain(test.Root, imported.File.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.All(imported.File.Id, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'));
        Assert.Empty(test.Errors);
    }

    [Fact]
    public async Task ContentDeduplicationNewVersionsAndAdoptionAreIndependent()
    {
        using var test = new Fixture();
        var source = await test.SourceAsync("字幕.srt", "first");
        var duplicate = await test.SourceAsync("同内容.ass", "first");
        using var library = test.Library();
        var first = Assert.Single((await library.ImportAsync(test.Account, test.Entry, [source], Token)).Items);
        await library.SetAdoptedAsync(test.Account, test.Entry.ItemId, first.File.Id, Token);
        var again = Assert.Single((await library.ImportAsync(test.Account, test.Entry, [duplicate], Token)).Items);
        Assert.Equal(first.File.Id, again.File.Id);
        await File.WriteAllTextAsync(source, "second", Token);
        var second = Assert.Single((await library.ImportAsync(test.Account, test.Entry, [source], Token)).Items);
        Assert.NotEqual(first.File.Id, second.File.Id);
        var saved = await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token);
        Assert.Equal(2, saved.Files.Length);
        Assert.Equal(first.File.Id, saved.AdoptedSubtitleId);
        await library.SetAdoptedAsync(test.Account, test.Entry.ItemId, "not-registered", Token);
        Assert.Equal(first.File.Id, (await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).AdoptedSubtitleId);
    }

    [Fact]
    public async Task ExplicitTargetsPreserveInputOrderAndNeverCopyUnmappedFiles()
    {
        using var test = new Fixture();
        var sources = ImmutableArray.Create(await test.SourceAsync("01.srt", "one"), await test.SourceAsync("unknown.srt", "unknown"),
            await test.SourceAsync("02.srt", "two"));
        var resolver = new Resolver((_, _, _, _) => Task.FromResult(ImmutableDictionary<int, string>.Empty.Add(0, "episode-1").Add(2, "episode-2")));
        using var library = test.Library(resolver);
        var result = await library.ImportAsync(test.Account, test.Entry, sources, Token);
        Assert.Equal(["episode-1", "episode-2"], result.Items.Select(item => item.ItemId));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(test.ScopeDirectory, "files")).Length);
        Assert.Empty((await library.GetForItemAsync(test.Account, "unknown", Token)).Files);
    }

    [Fact]
    public async Task UnmatchedBatchDoesNotCreateStorageAndOriginalBatchSizeSurvivesFiltering()
    {
        using var test = new Fixture();
        var accepted = await test.SourceAsync("字幕.srt", "text");
        var unsupported = await test.SourceAsync("bad.zip", "bytes");
        var resolver = new Resolver((_, _, candidates, _) =>
        {
            var candidate = Assert.Single(candidates);
            Assert.Equal(2, candidate.BatchSize);
            Assert.Equal(0, candidate.InputIndex);
            return Task.FromResult(ImmutableDictionary<int, string>.Empty);
        });
        using var library = test.Library(resolver);
        Assert.Empty((await library.ImportAsync(test.Account, test.Entry, [accepted, unsupported], Token)).Items);
        Assert.False(Directory.Exists(test.LibraryRoot));
    }

    [Fact]
    public async Task ConcurrentImportsDoNotLoseRecordsAndDuplicateContentHasOneIdentity()
    {
        using var test = new Fixture();
        using var library = test.Library();
        var sources = new List<string>();
        for (var index = 0; index < 12; index++) sources.Add(await test.SourceAsync(index + ".srt", "subtitle " + index));
        var results = await Task.WhenAll(sources.Select(source => library.ImportAsync(test.Account, test.Entry, [source], Token)));
        Assert.All(results, result => Assert.Single(result.Items));
        Assert.Equal(12, (await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files.Length);
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => library.ImportAsync(test.Account, test.Entry, [sources[0]], Token)));
        Assert.Single(duplicates.Select(result => Assert.Single(result.Items).File.Id).Distinct(StringComparer.Ordinal));
        Assert.Equal(12, (await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files.Length);
        Assert.Empty(Directory.GetFiles(test.LibraryRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CapacityIsPerItemAndBatchContinuesAfterAnUnreadableFile()
    {
        using var test = new Fixture();
        using var library = test.Library();
        var paths = ImmutableArray.CreateBuilder<string>();
        paths.Add(Path.Combine(test.SourceDirectory, "missing.srt"));
        for (var index = 0; index < 35; index++) paths.Add(await test.SourceAsync(index + ".srt", "subtitle " + index));
        Assert.Equal(32, (await library.ImportAsync(test.Account, test.Entry, paths.ToImmutable(), Token)).Items.Length);
        Assert.Equal(32, (await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files.Length);
        Assert.Single((await library.ImportAsync(test.Account, test.Entry with { ItemId = "another" }, [paths[35]], Token)).Items);
        Assert.Contains(test.Errors, error => error.Code == "subtitles.capacity_reached");
        Assert.All(test.Errors, error => { Assert.Equal("", error.Message); Assert.Null(error.DiagnosticId); });
    }

    [Fact]
    public async Task SwitchingAccountsCancelsPendingResolutionAndKeepsPreviouslyCommittedData()
    {
        using var test = new Fixture();
        var source = await test.SourceAsync("字幕.srt", "text");
        using var savedLibrary = test.Library();
        await savedLibrary.ImportAsync(test.Account, test.Entry, [source], Token);
        var old = test.Account;
        var oldSecret = old.Secret;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(async (_, _, _, token) =>
        {
            entered.SetResult(); await pending.Task.WaitAsync(token);
            return ImmutableDictionary<int, string>.Empty.Add(0, "other");
        });
        using var importing = test.Library(resolver);
        var operation = importing.ImportAsync(old, test.Entry, [source], Token);
        await entered.Task.WaitAsync(Token);
        test.Accounts.Set(new(oldSecret with { UserId = Guid.NewGuid().ToString("N") }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty((await savedLibrary.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files);
        test.Accounts.Set(new(oldSecret));
        Assert.Single((await savedLibrary.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files);
    }

    [Fact]
    public async Task CancellationAfterAnEarlierCommitDoesNotRollItBack()
    {
        using var test = new Fixture();
        var first = await test.SourceAsync("first.srt", "first");
        using var library = test.Library();
        await library.ImportAsync(test.Account, test.Entry, [first], Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.ImportAsync(test.Account, test.Entry, [first], cancellation.Token));
        Assert.Single((await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files);
    }

    [Fact]
    public async Task UnwritableStorageIsSilentAndDoesNotUseAnotherDirectory()
    {
        using var test = new Fixture();
        var source = await test.SourceAsync("字幕.srt", "text");
        await File.WriteAllTextAsync(test.LibraryRoot, "occupied", Token);
        using var library = test.Library();
        Assert.Empty((await library.ImportAsync(test.Account, test.Entry, [source], Token)).Items);
        Assert.Equal("occupied", await File.ReadAllTextAsync(test.LibraryRoot, Token));
        Assert.Equal("text", await File.ReadAllTextAsync(source, Token));
        Assert.Single(test.Errors);
    }

    [Fact]
    public async Task CorruptPrimaryUsesBackupButTwoCorruptIndexesAreNeverOverwritten()
    {
        using var test = new Fixture();
        var first = await test.SourceAsync("first.srt", "first");
        var second = await test.SourceAsync("second.srt", "second");
        using var library = test.Library();
        var saved = Assert.Single((await library.ImportAsync(test.Account, test.Entry, [first], Token)).Items);
        await library.SetAdoptedAsync(test.Account, test.Entry.ItemId, saved.File.Id, Token);
        await File.WriteAllTextAsync(test.IndexPath, "broken", Token);
        Assert.Single((await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files);
        Assert.Single((await library.ImportAsync(test.Account, test.Entry, [second], Token)).Items);
        Assert.Equal(2, (await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files.Length);
        await File.WriteAllTextAsync(test.IndexPath, "broken primary", Token);
        await File.WriteAllTextAsync(test.IndexPath + ".bak", "broken backup", Token);
        Assert.Empty((await library.ImportAsync(test.Account, test.Entry, [first], Token)).Items);
        Assert.Equal("broken primary", await File.ReadAllTextAsync(test.IndexPath, Token));
        Assert.Equal("broken backup", await File.ReadAllTextAsync(test.IndexPath + ".bak", Token));
    }

    [Fact]
    public async Task IncompleteIndexIsNotTreatedAsAnEmptyLibrary()
    {
        using var test = new Fixture();
        var source = await test.SourceAsync("字幕.srt", "text");
        using var library = test.Library();
        await library.ImportAsync(test.Account, test.Entry, [source], Token);
        await File.WriteAllTextAsync(test.IndexPath, "{}", Token);
        Assert.Empty((await library.ImportAsync(test.Account, test.Entry, [source], Token)).Items);
        Assert.Equal("{}", await File.ReadAllTextAsync(test.IndexPath, Token));
    }

    [Theory]
    [InlineData("RelativePath", "../outside.srt")]
    [InlineData("Sha256", "../outside")]
    [InlineData("Sha256", null)]
    public async Task MalformedManagedPathsCannotEscapeTheLibrary(string property, string? value)
    {
        using var test = new Fixture();
        var source = await test.SourceAsync("字幕.srt", "text");
        using var library = test.Library();
        await library.ImportAsync(test.Account, test.Entry, [source], Token);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(test.IndexPath, Token))!;
        document["Files"]!.AsObject().First().Value![property] = value;
        var malformed = document.ToJsonString();
        await File.WriteAllTextAsync(test.IndexPath, malformed, Token);
        Assert.Empty((await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files);
        Assert.Empty((await library.ImportAsync(test.Account, test.Entry, [source], Token)).Items);
        Assert.Equal(malformed, await File.ReadAllTextAsync(test.IndexPath, Token));
    }

    [Fact]
    public async Task MissingOrChangedManagedContentIsNotReturnedForPlayback()
    {
        using var test = new Fixture();
        using var library = test.Library();
        var first = await test.SourceAsync("first.srt", "first");
        var second = await test.SourceAsync("second.srt", "second");
        var result = await library.ImportAsync(test.Account, test.Entry, [first, second], Token);
        await library.SetAdoptedAsync(test.Account, test.Entry.ItemId, result.Items[0].File.Id, Token);
        File.Delete(result.Items[0].File.ManagedPath);
        await File.WriteAllTextAsync(result.Items[1].File.ManagedPath, "damaged", Token);
        var unavailable = await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token);
        Assert.Empty(unavailable.Files);
        Assert.Null(unavailable.AdoptedSubtitleId);
        await library.ImportAsync(test.Account, test.Entry, [first, second], Token);
        Assert.Equal(2, (await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files.Length);
    }

    [Fact]
    public async Task IndexWriteFailurePreservesEarlierBindingsAndOnlyCleansTemporaryFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "验证 Windows 文件锁使原子提交失败的行为。");
        using var test = new Fixture();
        using var library = test.Library();
        var first = await test.SourceAsync("first.srt", "first");
        var second = await test.SourceAsync("second.srt", "second");
        await library.ImportAsync(test.Account, test.Entry, [first], Token);
        var original = await File.ReadAllTextAsync(test.IndexPath, Token);
        using (var indexLock = new FileStream(test.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Empty((await library.ImportAsync(test.Account, test.Entry, [second], Token)).Items);
        Assert.Equal(original, await File.ReadAllTextAsync(test.IndexPath, Token));
        Assert.Single((await library.GetForItemAsync(test.Account, test.Entry.ItemId, Token)).Files);
        Assert.Equal("second", await File.ReadAllTextAsync(second, Token));
        Assert.Empty(Directory.GetFiles(test.LibraryRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Path.Combine(test.ScopeDirectory, "files")));
        Assert.Single((await library.ImportAsync(test.Account, test.Entry, [second], Token)).Items);
    }

    private sealed class Resolver(Func<AccountSession, PlaybackEntry, ImmutableArray<SubtitleImportCandidate>, CancellationToken,
        Task<ImmutableDictionary<int, string>>> resolve) : ILocalSubtitleTargetResolver
    {
        public Task<ImmutableDictionary<int, string>> ResolveAsync(AccountSession account, PlaybackEntry context,
            ImmutableArray<SubtitleImportCandidate> candidates, CancellationToken token) => resolve(account, context, candidates, token);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Parent = Path.Combine(Path.GetTempPath(), "mambo-subtitle-tests");
        public string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        public string LibraryRoot => Path.Combine(Root, "Subtitles");
        public string SourceDirectory => Path.Combine(Root, "source");
        public string ScopeDirectory => Path.Combine(LibraryRoot, Account.Scope);
        public string IndexPath => Path.Combine(ScopeDirectory, "index.json");
        public AccountContext Accounts { get; } = new();
        public AccountSession Account => Accounts.Current!;
        public PlaybackEntry Entry { get; } = new("episode-1", "合成第一集") { SeriesId = "series", SeriesName = "合成剧", SeasonId = "season", SeasonNumber = 1, EpisodeNumber = 1 };
        public List<AppError> Errors { get; } = [];
        public Fixture()
        {
            Directory.CreateDirectory(SourceDirectory);
            Accounts.Set(new(new("https://" + Guid.NewGuid().ToString("N") + ".invalid", Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), "合成用户", Guid.NewGuid().ToString("N"))));
        }
        public LocalSubtitleLibrary Library(ILocalSubtitleTargetResolver? resolver = null) => new(LibraryRoot, Accounts,
            resolver ?? new Resolver((_, context, candidates, _) => Task.FromResult(candidates.ToImmutableDictionary(candidate => candidate.InputIndex, _ => context.ItemId))),
            error => { lock (Errors) Errors.Add(error); });
        public async Task<string> SourceAsync(string name, string text)
        {
            var path = Path.Combine(SourceDirectory, name);
            await File.WriteAllTextAsync(path, text, Token);
            return path;
        }
        public void Dispose()
        {
            Accounts.Dispose();
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(resolved, true);
        }
    }
}
