using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Images;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class SettingsPreferenceTests
{
    [Fact]
    public async Task PreferencesPersistAcrossRestartAndRemainIsolatedByAccount()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        var first = Secret();
        var second = first with { UserId = Guid.NewGuid().ToString("N"), AccessToken = Guid.NewGuid().ToString("N") };
        using var accounts = new AccountContext();
        accounts.Set(new AccountSession(first));
        var query = new LibraryQuery { Sort = LibrarySort.Name, Direction = SortDirection.Ascending, Genres = ["科幻"], Years = [2020, 2024], OfficialRatings = ["PG"] };
        using (var settings = new SettingsStore(directory.Paths, scheduler))
        using (var preferences = new LibraryPreferences(settings, accounts, scheduler))
        {
            await preferences.SetAsync("demo-library", query, TestContext.Current.CancellationToken);
            Assert.Equal(LibrarySort.Name, preferences.Get("demo-library").Sort);
            accounts.Set(new AccountSession(second));
            Assert.Equal(LibrarySort.DateCreated, preferences.Get("demo-library").Sort);
            await preferences.SetAsync("demo-library", new LibraryQuery { Sort = LibrarySort.ProductionYear }, TestContext.Current.CancellationToken);
            accounts.Set(new AccountSession(first));
            Assert.Equal(LibrarySort.Name, preferences.Get("demo-library").Sort);
        }

        using var reopened = new SettingsStore(directory.Paths, scheduler);
        using var reloaded = new LibraryPreferences(reopened, accounts, scheduler);
        var restored = reloaded.Get("demo-library");
        Assert.Equal(query.Sort, restored.Sort);
        Assert.Equal(query.Direction, restored.Direction);
        Assert.Equal(["科幻"], restored.Genres);
        Assert.Equal([2020, 2024], restored.Years);
        Assert.Equal(["PG"], restored.OfficialRatings);
        var json = await File.ReadAllTextAsync(directory.Paths.Settings, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(first.ServerAddress, json);
        Assert.DoesNotContain(first.AccessToken, json);
        Assert.DoesNotContain(second.AccessToken, json);
    }

    [Fact]
    public async Task ResetRemovesOnlyCurrentAccountsLibraryPreference()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        using var accounts = new AccountContext();
        var first = Secret();
        var second = first with { UserId = Guid.NewGuid().ToString("N") };
        accounts.Set(new AccountSession(first));
        using var settings = new SettingsStore(directory.Paths, scheduler);
        using var preferences = new LibraryPreferences(settings, accounts, scheduler);
        await preferences.SetAsync("first-library", new LibraryQuery { Sort = LibrarySort.Name }, TestContext.Current.CancellationToken);
        await preferences.SetAsync("second-library", new LibraryQuery { Sort = LibrarySort.Runtime }, TestContext.Current.CancellationToken);
        accounts.Set(new AccountSession(second));
        await preferences.SetAsync("first-library", new LibraryQuery { Sort = LibrarySort.CommunityRating }, TestContext.Current.CancellationToken);
        await preferences.ResetAsync("first-library", TestContext.Current.CancellationToken);
        Assert.Equal(LibrarySort.DateCreated, preferences.Get("first-library").Sort);
        accounts.Set(new AccountSession(first));
        Assert.Equal(LibrarySort.Name, preferences.Get("first-library").Sort);
        Assert.Equal(LibrarySort.Runtime, preferences.Get("second-library").Sort);
    }

    [Fact]
    public async Task PreferenceNotificationsUseUiSchedulerAndDiscardThePreviousAccount()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        using var accounts = new AccountContext();
        var first = Secret();
        accounts.Set(new AccountSession(first));
        using var settings = new SettingsStore(directory.Paths, scheduler);
        using var preferences = new LibraryPreferences(settings, accounts, scheduler);
        var received = new List<LibraryPreferenceChangedEventArgs>();
        var callbackThread = -1;
        preferences.Changed += (_, value) => { received.Add(value); callbackThread = Environment.CurrentManagedThreadId; };
        await preferences.SetAsync("demo-library", new LibraryQuery { Sort = LibrarySort.Name }, TestContext.Current.CancellationToken);
        Assert.Empty(received);
        accounts.Set(new AccountSession(first with { UserId = Guid.NewGuid().ToString("N") }));
        await DrainUntilAsync(scheduler, () => received.Count == 1);
        Assert.Equal(LibrarySort.DateCreated, Assert.Single(received).Query.Sort);
        Assert.Equal(scheduler.LastDrainingThread, callbackThread);
    }

    [Fact]
    public async Task CancellationAndInvalidInputDoNotPersistAChangedPreference()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        using var accounts = new AccountContext();
        accounts.Set(new AccountSession(Secret()));
        using var settings = new SettingsStore(directory.Paths, scheduler);
        using var preferences = new LibraryPreferences(settings, accounts, scheduler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferences.SetAsync("demo-library", new LibraryQuery { Sort = LibrarySort.Name }, cancellation.Token));
        Assert.Equal(LibrarySort.DateCreated, preferences.Get("demo-library").Sort);
        var failure = await Assert.ThrowsAsync<AppException>(() => preferences.SetAsync(new string('库', 86), new LibraryQuery(), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidArgument, failure.Error.Code);
        await Assert.ThrowsAsync<AppException>(() => preferences.SetAsync("demo-library", new LibraryQuery { Years = [0] }, TestContext.Current.CancellationToken));
        Assert.Equal(LibrarySort.DateCreated, preferences.Get("demo-library").Sort);
    }

    [Fact]
    public async Task DisposeSuppressesPendingNotificationsAndRejectsFurtherWrites()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        using var accounts = new AccountContext();
        accounts.Set(new AccountSession(Secret()));
        using var settings = new SettingsStore(directory.Paths, scheduler);
        var preferences = new LibraryPreferences(settings, accounts, scheduler);
        var changes = 0;
        preferences.Changed += (_, _) => changes++;
        await preferences.SetAsync("demo-library", new LibraryQuery { Sort = LibrarySort.Name }, TestContext.Current.CancellationToken);
        preferences.Dispose();
        scheduler.Drain();
        Assert.Equal(0, changes);
        Assert.Throws<ObjectDisposedException>(() => preferences.Get("demo-library"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => preferences.ResetAsync("demo-library", TestContext.Current.CancellationToken));
        preferences.Dispose();
    }

    [Fact]
    public async Task SettingsMutationsPreserveUnrelatedFieldsAndStableDeviceIdentity()
    {
        using var directory = new SettingsDirectory();
        using var settings = new SettingsStore(directory.Paths, new PreferenceScheduler());
        var deviceId = settings.Current.DeviceId;
        await Task.WhenAll(
            settings.UpdateAsync(value => value with { Volume = 25 }, TestContext.Current.CancellationToken),
            settings.UpdateAsync(value => value with { HdrMode = HdrMode.Off }, TestContext.Current.CancellationToken));
        Assert.Equal(25, settings.Current.Volume);
        Assert.Equal(HdrMode.Off, settings.Current.HdrMode);
        Assert.Equal(deviceId, settings.Current.DeviceId);
        Assert.True(File.Exists(directory.Paths.Settings + ".bak"));
        await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { DeviceId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SettingsRecoverTheBackupWhenTheMainDocumentIsCorrupt()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        Guid deviceId;
        using (var settings = new SettingsStore(directory.Paths, scheduler))
        {
            deviceId = settings.Current.DeviceId;
            await settings.UpdateAsync(value => value with { Volume = 25 }, TestContext.Current.CancellationToken);
            await settings.UpdateAsync(value => value with { Volume = 50 }, TestContext.Current.CancellationToken);
        }
        await File.WriteAllTextAsync(directory.Paths.Settings, "{", TestContext.Current.CancellationToken);
        using var restored = new SettingsStore(directory.Paths, scheduler);
        Assert.Equal(deviceId, restored.Current.DeviceId);
        Assert.Equal(25, restored.Current.Volume);
    }

    [Fact]
    public async Task NullSettingsRecoverAndRepairPrimaryWithoutOverwritingTheGoodBackup()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        Guid deviceId;
        using (var settings = new SettingsStore(directory.Paths, scheduler))
        {
            deviceId = settings.Current.DeviceId;
            await settings.UpdateAsync(value => value with { Volume = 25 }, TestContext.Current.CancellationToken);
            await settings.UpdateAsync(value => value with { Volume = 50 }, TestContext.Current.CancellationToken);
        }
        var backup = await File.ReadAllTextAsync(directory.Paths.Settings + ".bak", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(directory.Paths.Settings, "{\"Version\":1,\"Settings\":null}", TestContext.Current.CancellationToken);
        using (var restored = new SettingsStore(directory.Paths, scheduler))
        {
            Assert.Equal(deviceId, restored.Current.DeviceId);
            Assert.Equal(25, restored.Current.Volume);
        }
        Assert.Equal(backup, await File.ReadAllTextAsync(directory.Paths.Settings + ".bak", TestContext.Current.CancellationToken));
        using var reopened = new SettingsStore(directory.Paths, scheduler);
        Assert.Equal(deviceId, reopened.Current.DeviceId);
        Assert.Equal(25, reopened.Current.Volume);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Settings\":null}")]
    public async Task InvalidPrimaryAndBackupPersistOneNewDeviceIdentity(string damaged)
    {
        using var directory = new SettingsDirectory();
        await File.WriteAllTextAsync(directory.Paths.Settings, damaged, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(directory.Paths.Settings + ".bak", damaged, TestContext.Current.CancellationToken);
        Guid deviceId;
        using (var recovered = new SettingsStore(directory.Paths, new PreferenceScheduler()))
        {
            deviceId = recovered.Current.DeviceId;
            Assert.NotEqual(Guid.Empty, deviceId);
        }
        using var reopened = new SettingsStore(directory.Paths, new PreferenceScheduler());
        Assert.Equal(deviceId, reopened.Current.DeviceId);
    }

    [Fact]
    public async Task NullOptionalFieldsAndDefaultPreferenceArraysAreRepairedWithoutResettingSettings()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new PreferenceScheduler();
        using var accounts = new AccountContext();
        accounts.Set(new AccountSession(Secret()));
        var scope = accounts.Current!.Scope;
        var deviceId = Guid.NewGuid();
        var damaged = $$"""
            {
              "Version":1,
              "Settings":{"DeviceId":"{{deviceId}}","Volume":45},
              "Connection":{"ServerAddress":null,"UserName":null},
              "Preferences":{
                "ignored-null":null,
                "ignored-enum":{"Sort":99},
                "{{scope}}|empty":{"Sort":1,"Genres":null,"Years":null,"OfficialRatings":null},
                "{{scope}}|filtered":{"Sort":2,"Genres":["科幻",null,"","科幻"],"Years":[0,2024,2024,10000],"OfficialRatings":["PG",null,""]}
              }
            }
            """;
        await File.WriteAllTextAsync(directory.Paths.Settings, damaged, TestContext.Current.CancellationToken);
        using var settings = new SettingsStore(directory.Paths, scheduler);
        using var preferences = new LibraryPreferences(settings, accounts, scheduler);
        Assert.Equal(deviceId, settings.Current.DeviceId);
        Assert.Equal(45, settings.Current.Volume);
        Assert.Empty(settings.ConnectionDefaults.ServerAddress);
        Assert.Empty(settings.ConnectionDefaults.UserName);
        var empty = preferences.Get("empty");
        Assert.Equal(LibrarySort.Name, empty.Sort);
        Assert.False(empty.Genres.IsDefault);
        Assert.False(empty.Years.IsDefault);
        Assert.False(empty.OfficialRatings.IsDefault);
        Assert.Empty(empty.Genres);
        var filtered = preferences.Get("filtered");
        Assert.Equal(["科幻"], filtered.Genres);
        Assert.Equal([2024], filtered.Years);
        Assert.Equal(["PG"], filtered.OfficialRatings);
        var repaired = await File.ReadAllTextAsync(directory.Paths.Settings, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("ignored-null", repaired);
        Assert.DoesNotContain("ignored-enum", repaired);
        using var reopened = new SettingsStore(directory.Paths, scheduler);
        Assert.Equal(deviceId, reopened.Current.DeviceId);
    }

    [Fact]
    public async Task NullOptionalSectionsBecomeUsableEmptyDefaults()
    {
        using var directory = new SettingsDirectory();
        var deviceId = Guid.NewGuid();
        var damaged = $$"""{"Version":1,"Settings":{"DeviceId":"{{deviceId}}"},"Connection":null,"Preferences":null}""";
        await File.WriteAllTextAsync(directory.Paths.Settings, damaged, TestContext.Current.CancellationToken);
        using var accounts = new AccountContext();
        accounts.Set(new AccountSession(Secret()));
        var scheduler = new PreferenceScheduler();
        using var settings = new SettingsStore(directory.Paths, scheduler);
        using var preferences = new LibraryPreferences(settings, accounts, scheduler);
        Assert.Equal(deviceId, settings.Current.DeviceId);
        Assert.Empty(settings.ConnectionDefaults.ServerAddress);
        Assert.Empty(settings.ConnectionDefaults.UserName);
        Assert.Empty(preferences.Get("demo-library").Genres);
        await preferences.SetAsync("demo-library", new LibraryQuery { Sort = LibrarySort.Name }, TestContext.Current.CancellationToken);
        Assert.Equal(LibrarySort.Name, preferences.Get("demo-library").Sort);
    }

    [Fact]
    public async Task ClearCacheWaitsForTheCompositionRootAndPropagatesSafeStorageFailures()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "此文件删除共享权限验证需要 Windows。");
        using var directory = new SettingsDirectory();
        using var runtime = new BackendRuntime(directory.Paths, new EmptySecretStore(), new PreferenceScheduler(),
            new WeakReferenceMessenger(), new FakeTimeProvider());
        var key = new ImageRequestKey(Guid.NewGuid().ToString("N"), "demo-item", ImageKind.Primary, "version-1", 160);
        await runtime.ImageCache.StoreAsync(key, new byte[] { 1, 2, 3 }, cancellationToken: TestContext.Current.CancellationToken);
        var image = Assert.Single(Directory.GetFiles(directory.Paths.Images, "*.img", SearchOption.AllDirectories));
        using (var locked = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<AppException>(() => runtime.Settings.ClearCacheAsync(TestContext.Current.CancellationToken));
            Assert.Equal(AppErrorKind.Persistence, failure.Error.Kind);
            Assert.Equal(ErrorCodes.PersistenceFailed, failure.Error.Code);
            Assert.DoesNotContain(directory.Paths.Root, failure.Message);
            Assert.Null(failure.InnerException);
        }
        await runtime.Settings.ClearCacheAsync(TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(directory.Paths.Images, "*.img", SearchOption.AllDirectories));
        Assert.Equal(0, runtime.ImageCache.MemoryBytes);
    }

    [Fact]
    public async Task SettingsCacheCleanupPreservesSubtitlesBesideExecutable()
    {
        using var directory = new SettingsDirectory();
        using var runtime = new BackendRuntime(directory.Paths, new EmptySecretStore(), new PreferenceScheduler(),
            new WeakReferenceMessenger(), new FakeTimeProvider());
        var subtitleDirectory = Path.Combine(AppContext.BaseDirectory, "Subtitles", "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subtitleDirectory);
        var subtitleFile = Path.Combine(subtitleDirectory, "preserved.srt");
        try
        {
            await File.WriteAllTextAsync(subtitleFile, "synthetic subtitle", TestContext.Current.CancellationToken);
            await runtime.Settings.ClearCacheAsync(TestContext.Current.CancellationToken);
            Assert.Equal("synthetic subtitle", await File.ReadAllTextAsync(subtitleFile, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(subtitleFile);
            Directory.Delete(subtitleDirectory);
        }
    }

    private static SessionSecret Secret()
    {
        var address = new UriBuilder(Uri.UriSchemeHttps, Guid.NewGuid().ToString("N") + ".invalid").Uri.AbsoluteUri;
        return new(address, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "读者", Guid.NewGuid().ToString("N"));
    }

    private static async Task DrainUntilAsync(PreferenceScheduler scheduler, Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) { scheduler.Drain(); if (!condition()) await Task.Delay(1, timeout.Token); }
    }

    private sealed class PreferenceScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> callbacks = new();
        public int LastDrainingThread { get; private set; }
        public bool TryEnqueue(Action callback) { callbacks.Enqueue(callback); return true; }
        public void Drain()
        {
            LastDrainingThread = Environment.CurrentManagedThreadId;
            while (callbacks.TryDequeue(out var callback)) callback();
        }
    }

    private sealed class SettingsDirectory : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "MamboTests", Guid.NewGuid().ToString("N")));
        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MamboTests")) + Path.DirectorySeparatorChar;
            if (!Paths.Root.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录不在预期路径内。");
            if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true);
        }
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<SessionSecret?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<SessionSecret?>(null);
        public Task WriteAsync(SessionSecret secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
