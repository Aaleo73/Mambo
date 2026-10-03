using System.Collections.Concurrent;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class SettingsP7Tests
{
    [Fact]
    public async Task ThemePersistsAndOldDocumentsKeepTheSystemDefault()
    {
        using var directory = new SettingsDirectory();
        var device = Guid.NewGuid();
        await File.WriteAllTextAsync(directory.Paths.Settings, $$$"""{"Version":1,"Settings":{"DeviceId":"{{{device}}}","Volume":37}}""", TestContext.Current.CancellationToken);
        using (var settings = new SettingsStore(directory.Paths, new Scheduler()))
        {
            Assert.Equal(SettingsThemeMode.System, settings.Current.ThemeMode);
            await settings.UpdateAsync(value => value with { ThemeMode = SettingsThemeMode.Dark }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { ThemeMode = (SettingsThemeMode)99 }, TestContext.Current.CancellationToken));
        }
        using var restored = new SettingsStore(directory.Paths, new Scheduler());
        Assert.Equal(SettingsThemeMode.Dark, restored.Current.ThemeMode);
        Assert.Equal(device, restored.Current.DeviceId);
        Assert.Equal(37, restored.Current.Volume);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(",\"UseEpisodeGrid\":false", false)]
    [InlineData(",\"UseEpisodeGrid\":true", true)]
    public async Task EpisodePreferencesMigrateWithoutOverwritingStoredLayout(string storedLayout, bool expectedGrid)
    {
        using var directory = new SettingsDirectory();
        var device = Guid.NewGuid();
        await File.WriteAllTextAsync(directory.Paths.Settings,
            $$$"""{"Version":1,"Settings":{"DeviceId":"{{{device}}}","Volume":37{{{storedLayout}}}}}""",
            TestContext.Current.CancellationToken);
        using (var settings = new SettingsStore(directory.Paths, new Scheduler()))
        {
            Assert.Equal(expectedGrid, settings.Current.UseEpisodeGrid);
            Assert.False(settings.Current.EpisodePanelCollapsed);
            await settings.UpdateAsync(value => value with { EpisodePanelCollapsed = true }, TestContext.Current.CancellationToken);
        }
        using var restored = new SettingsStore(directory.Paths, new Scheduler());
        Assert.True(restored.Current.EpisodePanelCollapsed);
        Assert.Equal(expectedGrid, restored.Current.UseEpisodeGrid);
        Assert.Equal(device, restored.Current.DeviceId);
        Assert.Equal(37, restored.Current.Volume);
    }

    [Fact]
    public async Task CacheMeasurementIncludesOnlyCacheAndShaderFiles()
    {
        using var directory = new SettingsDirectory();
        var paths = directory.Paths;
        await WriteFileAsync(Path.Combine(paths.Root, "cache", "nested", "image.bin"), 7);
        await WriteFileAsync(Path.Combine(paths.Root, "mpv", "shader-cache", "shader.bin"), 11);
        await WriteFileAsync(Path.Combine(paths.Root, "mpv", "not-a-cache.bin"), 23);
        await WriteFileAsync(Path.Combine(paths.Root, "outbox", "pending.bin"), 29);
        await WriteFileAsync(Path.Combine(paths.Root, "logs", "log.bin"), 31);
        using var settings = new SettingsStore(paths, new Scheduler());
        Assert.Equal(18, await settings.GetCacheSizeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(paths.Root, "logs"), settings.LogDirectory);
    }

    [Fact]
    public async Task EmptyCacheQueryDoesNotCreateCacheOrLogDirectoriesAndSupportsCancellation()
    {
        using var directory = new SettingsDirectory();
        using var settings = new SettingsStore(directory.Paths, new Scheduler());
        Assert.Equal(0, await settings.GetCacheSizeAsync(TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(directory.Paths.Root, "cache")));
        Assert.False(Directory.Exists(Path.Combine(directory.Paths.Root, "logs")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settings.GetCacheSizeAsync(cancellation.Token));
    }

    [Fact]
    public async Task CacheMeasurementSkipsDirectoryReparsePoints()
    {
        using var directory = new SettingsDirectory();
        var external = Path.Combine(directory.Paths.Root, "excluded");
        await WriteFileAsync(Path.Combine(external, "large.bin"), 47);
        var cache = Path.Combine(directory.Paths.Root, "cache");
        Directory.CreateDirectory(cache);
        var link = Path.Combine(cache, "junction");
        if (OperatingSystem.IsWindows())
        {
            // Junction 不需要管理员或开发者模式；所有目标均在本测试临时目录内。
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(external);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, external);
        try
        {
            using var settings = new SettingsStore(directory.Paths, new Scheduler());
            Assert.Equal(0, await settings.GetCacheSizeAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task DemoSettingsPersistThemeInMemoryAndExposeDeterministicCacheWithoutFileAccess()
    {
        using var fake = new FakeSettingsService(new FakeOperation(new FakeOptions { Delay = TimeSpan.Zero }, TimeProvider.System), new Scheduler());
        await fake.UpdateAsync(value => value with { ThemeMode = SettingsThemeMode.Light }, TestContext.Current.CancellationToken);
        Assert.Equal(SettingsThemeMode.Light, fake.Current.ThemeMode);
        Assert.Equal(312L * 1024 * 1024, await fake.GetCacheSizeAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fake.LogDirectory);
        await Assert.ThrowsAsync<AppException>(() => fake.UpdateAsync(value => value with { ThemeMode = (SettingsThemeMode)99 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManualValidationPublishesValidatingAndPersistsApprovalWithoutLosingConcurrentEdits()
    {
        using var directory = new SettingsDirectory();
        var scheduler = new Scheduler();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, scheduler, validator);
        var statuses = new List<ExternalPlayerStatus>();
        settings.Changed += (_, _) => statuses.Add(settings.ExternalPlayerStatus);
        var approval = Approval(directory.Paths.Root, "first.exe");
        var validation = settings.ValidateExternalPlayerAsync(approval.Path, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalPlayerStatus.Validating, settings.ExternalPlayerStatus);
        scheduler.Drain();
        Assert.Equal([ExternalPlayerStatus.Validating], statuses);
        await settings.UpdateAsync(value => value with { ThemeMode = SettingsThemeMode.Dark, Volume = 17 }, TestContext.Current.CancellationToken);
        validator.Requests[0].Completion.SetResult(approval);
        await validation;
        Assert.Equal(ExternalPlayerStatus.Approved, settings.ExternalPlayerStatus);
        Assert.Equal(approval, settings.Current.ExternalMpvApproval);
        Assert.Equal(SettingsThemeMode.Dark, settings.Current.ThemeMode);
        Assert.Equal(17, settings.Current.Volume);
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken);
        Assert.Equal(approval, await settings.GetApprovedExternalPlayerAsync(TestContext.Current.CancellationToken));
        using var restored = new SettingsStore(directory.Paths, new Scheduler(), validator);
        Assert.Equal(ExternalPlayerStatus.Approved, restored.ExternalPlayerStatus);
        Assert.Equal(approval, restored.Current.ExternalMpvApproval);
        Assert.Equal(PlaybackMode.External, restored.Current.PlaybackMode);
    }

    [Fact]
    public async Task OlderValidationCannotOverrideANewerPath()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var first = Approval(directory.Paths.Root, "first.exe");
        var second = Approval(directory.Paths.Root, "second.exe");
        var validation = settings.ValidateExternalPlayerAsync(first.Path, TestContext.Current.CancellationToken);
        await settings.UpdateAsync(value => value with { ExternalMpvPath = second.Path }, TestContext.Current.CancellationToken);
        validator.Requests[0].Completion.SetResult(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        Assert.Equal(second.Path, settings.Current.ExternalMpvPath);
        Assert.Null(settings.Current.ExternalMpvApproval);
        Assert.Equal(ExternalPlayerStatus.Invalid, settings.ExternalPlayerStatus);
    }

    [Fact]
    public async Task NewerValidationWinsEvenWhenTheOldProviderIgnoresCancellation()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var first = Approval(directory.Paths.Root, "first.exe");
        var second = Approval(directory.Paths.Root, "second.exe");
        var oldValidation = settings.ValidateExternalPlayerAsync(first.Path, TestContext.Current.CancellationToken);
        var newValidation = settings.ValidateExternalPlayerAsync(second.Path, TestContext.Current.CancellationToken);
        validator.Requests[1].Completion.SetResult(second);
        await newValidation;
        validator.Requests[0].Completion.SetResult(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldValidation);
        Assert.Equal(second, settings.Current.ExternalMpvApproval);
        Assert.Equal(ExternalPlayerStatus.Approved, settings.ExternalPlayerStatus);
    }

    [Fact]
    public async Task CancellationRestoresThePreviousStatusAndDoesNotPersistApproval()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var approval = Approval(directory.Paths.Root, "first.exe");
        using var cancellation = new CancellationTokenSource();
        var validation = settings.ValidateExternalPlayerAsync(approval.Path, cancellation.Token);
        cancellation.Cancel();
        validator.Requests[0].Completion.SetResult(approval);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        Assert.Equal(ExternalPlayerStatus.UsingEmbedded, settings.ExternalPlayerStatus);
        Assert.Null(settings.Current.ExternalMpvApproval);
        Assert.Null(settings.Current.ExternalMpvPath);
    }

    [Fact]
    public async Task CancellingANewerValidationDoesNotRestoreAnOrphanedValidatingStatus()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var first = Approval(directory.Paths.Root, "first.exe");
        var second = Approval(directory.Paths.Root, "second.exe");
        var oldValidation = settings.ValidateExternalPlayerAsync(first.Path, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var newValidation = settings.ValidateExternalPlayerAsync(second.Path, cancellation.Token);
        cancellation.Cancel();
        validator.Requests[1].Completion.SetResult(second);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newValidation);
        validator.Requests[0].Completion.SetResult(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldValidation);
        Assert.Equal(ExternalPlayerStatus.UsingEmbedded, settings.ExternalPlayerStatus);
        Assert.Null(settings.Current.ExternalMpvApproval);
    }

    [Fact]
    public async Task APathChangeDuringFingerprintVerificationCannotLaunchTheOldApproval()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var approval = Approval(directory.Paths.Root, "first.exe");
        var validation = settings.ValidateExternalPlayerAsync(approval.Path, TestContext.Current.CancellationToken);
        validator.Requests[0].Completion.SetResult(approval);
        await validation;
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken);
        validator.Verification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verification = settings.GetApprovedExternalPlayerAsync(TestContext.Current.CancellationToken);
        var changed = Path.Combine(directory.Paths.Root, "second.exe");
        await settings.UpdateAsync(value => value with { ExternalMpvPath = changed }, TestContext.Current.CancellationToken);
        validator.Verification.SetResult(true);
        Assert.Null(await verification);
        Assert.Equal(changed, settings.Current.ExternalMpvPath);
        Assert.Null(settings.Current.ExternalMpvApproval);
        Assert.Equal(PlaybackMode.Embedded, settings.Current.PlaybackMode);
    }

    [Fact]
    public async Task FailedValidationDisablesExternalModeWithASafeError()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var approval = Approval(directory.Paths.Root, "first.exe");
        var validation = settings.ValidateExternalPlayerAsync(approval.Path, TestContext.Current.CancellationToken);
        validator.Requests[0].Completion.SetResult(approval);
        await validation;
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken);
        validation = settings.ValidateExternalPlayerAsync(approval.Path, TestContext.Current.CancellationToken);
        validator.Requests[1].Completion.SetException(new IOException(approval.Path));
        var error = await Assert.ThrowsAsync<AppException>(() => validation);
        Assert.DoesNotContain(approval.Path, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(PlaybackMode.Embedded, settings.Current.PlaybackMode);
        Assert.Null(settings.Current.ExternalMpvApproval);
        Assert.Equal(ExternalPlayerStatus.Invalid, settings.ExternalPlayerStatus);
    }

    [Fact]
    public async Task ChangedFingerprintDisablesExternalModeAndRequiresAnotherManualValidation()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var approval = Approval(directory.Paths.Root, "first.exe");
        var validation = settings.ValidateExternalPlayerAsync(approval.Path, TestContext.Current.CancellationToken);
        validator.Requests[0].Completion.SetResult(approval);
        await validation;
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken);
        validator.Verified = false;
        Assert.Null(await settings.GetApprovedExternalPlayerAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PlaybackMode.Embedded, settings.Current.PlaybackMode);
        Assert.Null(settings.Current.ExternalMpvApproval);
        Assert.Equal(ExternalPlayerStatus.Invalid, settings.ExternalPlayerStatus);
        var unapproved = await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ExternalApprovalRequired, unapproved.Error.Code);
        Assert.Null(await settings.GetApprovedExternalPlayerAsync(TestContext.Current.CancellationToken));
        Assert.Single(validator.Requests);
        using var restored = new SettingsStore(directory.Paths, new Scheduler(), validator);
        Assert.Equal(PlaybackMode.Embedded, restored.Current.PlaybackMode);
        Assert.Null(restored.Current.ExternalMpvApproval);
    }

    [Fact]
    public async Task SettingsUpdatesCannotForgeApprovalOrEnableAnUnapprovedPath()
    {
        using var directory = new SettingsDirectory();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), new ControlledValidator());
        var approval = Approval(directory.Paths.Root, "first.exe");
        await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { ExternalMpvPath = approval.Path, ExternalMpvApproval = approval, PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken));
        Assert.Equal(PlaybackMode.Embedded, settings.Current.PlaybackMode);
        Assert.Null(settings.Current.ExternalMpvApproval);
    }

    [Fact]
    public async Task ChangingAnApprovedPathRevokesApprovalAndKeepsTheNewInput()
    {
        using var directory = new SettingsDirectory();
        var validator = new ControlledValidator();
        using var settings = new SettingsStore(directory.Paths, new Scheduler(), validator);
        var approval = Approval(directory.Paths.Root, "first.exe");
        var validation = settings.ValidateExternalPlayerAsync(approval.Path, TestContext.Current.CancellationToken);
        validator.Requests[0].Completion.SetResult(approval);
        await validation;
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, TestContext.Current.CancellationToken);
        var changed = Path.Combine(directory.Paths.Root, "second.exe");
        await settings.UpdateAsync(value => value with { ExternalMpvPath = changed }, TestContext.Current.CancellationToken);
        Assert.Equal(changed, settings.Current.ExternalMpvPath);
        Assert.Null(settings.Current.ExternalMpvApproval);
        Assert.Equal(PlaybackMode.Embedded, settings.Current.PlaybackMode);
        Assert.Equal(ExternalPlayerStatus.Invalid, settings.ExternalPlayerStatus);
    }

    [Fact]
    public async Task LegacyExternalModeWithoutFingerprintIsRepairedWithoutResettingDevice()
    {
        using var directory = new SettingsDirectory();
        var device = Guid.NewGuid();
        await File.WriteAllTextAsync(directory.Paths.Settings, $$$"""{"Version":1,"Settings":{"DeviceId":"{{{device}}}","PlaybackMode":1,"Volume":41}}""", TestContext.Current.CancellationToken);
        using var settings = new SettingsStore(directory.Paths, new Scheduler());
        Assert.Equal(device, settings.Current.DeviceId);
        Assert.Equal(41, settings.Current.Volume);
        Assert.Equal(PlaybackMode.Embedded, settings.Current.PlaybackMode);
        Assert.Null(settings.Current.ExternalMpvApproval);
    }

    private static ExternalMpvApproval Approval(string root, string name) =>
        new(Path.Combine(root, name), new string('a', 64), 1024, DateTime.UtcNow.Ticks, "mpv test version");
    private static Task WriteFileAsync(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllBytesAsync(path, new byte[length], TestContext.Current.CancellationToken);
    }
    private sealed class ControlledValidator : IExternalPlayerValidator
    {
        public List<Request> Requests { get; } = [];
        public bool Verified { get; set; } = true;
        public TaskCompletionSource<bool>? Verification { get; set; }
        public Task<ExternalMpvApproval> ValidateAsync(string path, CancellationToken cancellationToken = default)
        {
            var request = new Request(path, new TaskCompletionSource<ExternalMpvApproval>(TaskCreationOptions.RunContinuationsAsynchronously));
            Requests.Add(request);
            return request.Completion.Task;
        }
        public Task<bool> VerifyAsync(ExternalMpvApproval approval, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Verification?.Task ?? Task.FromResult(Verified);
        }
    }
    private sealed record Request(string Path, TaskCompletionSource<ExternalMpvApproval> Completion);
    private sealed class Scheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> callbacks = new();
        public bool TryEnqueue(Action callback) { callbacks.Enqueue(callback); return true; }
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }
    private sealed class SettingsDirectory : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "MamboSettingsTests", Guid.NewGuid().ToString("N")));
        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MamboSettingsTests")) + Path.DirectorySeparatorChar;
            if (!Paths.Root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录不在预期路径内。");
            if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true);
        }
    }
}
