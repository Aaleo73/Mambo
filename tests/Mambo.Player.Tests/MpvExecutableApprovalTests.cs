using System.Security.Cryptography;
using Mambo.Core.Contracts;
using Mambo.Player.External;
using Xunit;

namespace Mambo.Player.Tests;

public sealed class MpvExecutableApprovalTests
{
    [Fact]
    public async Task DirectorySelectionStoresCanonicalFileAndFingerprint()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner();
        var validator = new MpvExecutableApproval(runner);
        var approval = await validator.ValidateAsync("  " + fixture.Directory + "  ", TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Executable, approval.Path);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Executable))), approval.Sha256);
        Assert.Equal(new FileInfo(fixture.Executable).Length, approval.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(fixture.Executable).Ticks, approval.LastWriteTimeUtcTicks);
        Assert.Equal("v0.40.0-25-gabc1234", approval.Version);
        Assert.Equal(fixture.Executable, runner.LastPath);
        Assert.True(await validator.VerifyAsync(approval, TestContext.Current.CancellationToken));
        Assert.Equal(1, runner.Calls);
        Assert.DoesNotContain(fixture.Directory, approval.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelativeSelectionIsImmediatelyConvertedToAbsolutePath()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, fixture.Executable);
        var validator = new MpvExecutableApproval(new VersionRunner());
        var approval = await validator.ValidateAsync(relative, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Executable, approval.Path);
        Assert.True(Path.IsPathFullyQualified(approval.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\\\\server\\share\\mpv.exe")]
    [InlineData("\\\\?\\C:\\mpv.exe")]
    [InlineData("\\\\.\\C:\\mpv.exe")]
    [InlineData("https://example.invalid/mpv.exe")]
    [InlineData("mpv.exe\0")]
    public async Task InvalidInputNeverRunsExecutableOrExposesPath(string path)
    {
        RequireWindows();
        var runner = new VersionRunner();
        var error = await Assert.ThrowsAsync<AppException>(() =>
            new MpvExecutableApproval(runner).ValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(AppErrorKind.Player, error.Error.Kind);
        Assert.Equal(0, runner.Calls);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("example.invalid", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server\\share", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongFileNameMissingFileAndEmptyFileAreRejectedBeforeVersionProbe()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner();
        var validator = new MpvExecutableApproval(runner);
        var renamed = Path.Combine(fixture.Directory, "other.exe");
        File.WriteAllText(renamed, "placeholder");
        await Assert.ThrowsAsync<AppException>(() => validator.ValidateAsync(renamed, TestContext.Current.CancellationToken));
        File.Delete(fixture.Executable);
        await Assert.ThrowsAsync<AppException>(() => validator.ValidateAsync(fixture.Directory, TestContext.Current.CancellationToken));
        File.WriteAllBytes(fixture.Executable, []);
        await Assert.ThrowsAsync<AppException>(() => validator.ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken));
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ffmpeg version 7.1")]
    [InlineData("MPV v0.40.0")]
    [InlineData("not mpv v0.40.0")]
    [InlineData("mpv UNKNOWN")]
    [InlineData("mpv --version")]
    [InlineData("mpv abcdef123456")]
    [InlineData("mpv git-abcdef1")]
    [InlineData("mpv 0.40.0/path-sensitive")]
    [InlineData("noise\nmpv v0.40.0")]
    public async Task NonMpvVersionIsRejectedWithoutEchoingUntrustedOutput(string output)
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner { Result = new(0, output) };
        var error = await Assert.ThrowsAsync<AppException>(() =>
            new MpvExecutableApproval(runner).ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ExternalVersionInvalid, error.Error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("path-sensitive", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mpv 0.40.0 Copyright © 2000-2026 mpv projects\nlibplacebo version: v7", "0.40.0")]
    [InlineData("mpv v0.40.0-25-gabc1234 Copyright © 2000-2026 mpv projects", "v0.40.0-25-gabc1234")]
    [InlineData("mpv 0.38.0", "0.38.0")]
    [InlineData("mpv v0.38.0-1-gabcdef1", "v0.38.0-1-gabcdef1")]
    [InlineData("mpv 1.0.0", "1.0.0")]
    public async Task CompatibleReleaseAndNightlyVersionBannersAreRecognized(string output, string expected)
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var validator = new MpvExecutableApproval(new VersionRunner { Result = new(0, output) });
        var approval = await validator.ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken);
        Assert.Equal(expected, approval.Version);
    }

    [Theory]
    [InlineData("mpv 0.37.0")]
    [InlineData("mpv v0.37.99-100-gabcdef1")]
    [InlineData("mpv 0.1.0")]
    [InlineData("mpv 0.38")]
    [InlineData("mpv 0.38.0invalid")]
    public async Task IncompatibleOrIndeterminateVersionCannotBeApproved(string output)
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var error = await Assert.ThrowsAsync<AppException>(() =>
            new MpvExecutableApproval(new VersionRunner { Result = new(0, output) })
                .ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ExternalVersionInvalid, error.Error.Code);
        Assert.Contains("0.38.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonzeroExitAndExcessiveOutputFailClosed()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var results = new MpvVersionResult[]
        {
            new(1, "mpv v0.40.0"),
            new(0, "mpv v0.40.0", true),
            new(0, "mpv v0.40.0\n" + new string('x', MpvExecutableApproval.MaximumVersionOutputCharacters)),
        };
        foreach (var result in results)
        {
            var error = await Assert.ThrowsAsync<AppException>(() =>
                new MpvExecutableApproval(new VersionRunner { Result = result })
                    .ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.ExternalVersionInvalid, error.Error.Code);
            Assert.DoesNotContain("mpv v0.40.0", result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ValidationKeepsFileLockedAgainstWritesAndReplacement()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner
        {
            Handler = (path, _) =>
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "replacement"));
                Assert.Throws<IOException>(() => File.Delete(path));
                return Task.FromResult(new MpvVersionResult(0, "mpv 0.40.0"));
            },
        };
        var approval = await new MpvExecutableApproval(runner).ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken);
        Assert.Equal(new FileInfo(fixture.Executable).Length, approval.Size);
    }

    [Fact]
    public async Task FingerprintChangeWithRestoredSizeAndTimestampNeedsFreshApprovalWithoutExecution()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner();
        var validator = new MpvExecutableApproval(runner);
        var approval = await validator.ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken);
        var bytes = File.ReadAllBytes(fixture.Executable);
        bytes[0] ^= 1;
        File.WriteAllBytes(fixture.Executable, bytes);
        File.SetLastWriteTimeUtc(fixture.Executable, new DateTime(approval.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        Assert.Equal(approval.Size, new FileInfo(fixture.Executable).Length);
        Assert.Equal(approval.LastWriteTimeUtcTicks, File.GetLastWriteTimeUtc(fixture.Executable).Ticks);
        Assert.False(await validator.VerifyAsync(approval, TestContext.Current.CancellationToken));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task MetadataChangeMissingFileAndMalformedApprovalNeverExecute()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner();
        var validator = new MpvExecutableApproval(runner);
        var approval = await validator.ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken);
        Assert.False(await validator.VerifyAsync(approval with { Size = approval.Size + 1 }, TestContext.Current.CancellationToken));
        Assert.False(await validator.VerifyAsync(approval with { LastWriteTimeUtcTicks = approval.LastWriteTimeUtcTicks - 1 }, TestContext.Current.CancellationToken));
        Assert.False(await validator.VerifyAsync(approval with { Sha256 = "not-sha256" }, TestContext.Current.CancellationToken));
        Assert.False(await validator.VerifyAsync(approval with { Path = fixture.Directory }, TestContext.Current.CancellationToken));
        Assert.False(await validator.VerifyAsync(approval with { Version = "" }, TestContext.Current.CancellationToken));
        Assert.False(await validator.VerifyAsync(approval with { Version = "0.37.0" }, TestContext.Current.CancellationToken));
        Assert.False(await validator.VerifyAsync(approval with { Version = "abcdef123456" }, TestContext.Current.CancellationToken));
        File.SetLastWriteTimeUtc(fixture.Executable, new DateTime(approval.LastWriteTimeUtcTicks, DateTimeKind.Utc).AddSeconds(5));
        Assert.False(await validator.VerifyAsync(approval, TestContext.Current.CancellationToken));
        File.Delete(fixture.Executable);
        Assert.False(await validator.VerifyAsync(approval, TestContext.Current.CancellationToken));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task DefaultVerifierCanReadFingerprintOfNonExecutableFixtureWithoutRunningIt()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var approval = new ExternalMpvApproval(fixture.Executable,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Executable))),
            new FileInfo(fixture.Executable).Length, File.GetLastWriteTimeUtc(fixture.Executable).Ticks, "0.40.0");
        Assert.True(await new MpvExecutableApproval().VerifyAsync(approval, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCancellationCancelsVersionRunnerAndReleasesLock()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new VersionRunner
        {
            Handler = async (_, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled.TrySetResult(); }
                return new(0, "mpv v0.40.0");
            },
        };
        var validation = new MpvExecutableApproval(runner).ValidateAsync(fixture.Executable, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(fixture.Executable, "lock released");
    }

    [Fact]
    public async Task ThreeSecondDeadlineCancelsVersionRunnerWithoutLeakingItsFailure()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new VersionRunner
        {
            Handler = async (_, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled.TrySetResult(); }
                return new(0, "mpv v0.40.0");
            },
        };
        var error = await Assert.ThrowsAsync<AppException>(() =>
            new MpvExecutableApproval(runner).ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ExternalVersionTimeout, error.Error.Code);
        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(fixture.Executable, "lock released");
    }

    [Fact]
    public async Task UntrustedRunnerExceptionsAreTranslatedToSafeErrors()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner
        {
            Handler = (_, _) => throw new InvalidOperationException("sensitive-runner-output " + fixture.Directory),
        };
        var error = await Assert.ThrowsAsync<AppException>(() =>
            new MpvExecutableApproval(runner).ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive-runner-output", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Directory, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlreadyCancelledVerificationPropagatesCancellationWithoutVersionProbe()
    {
        RequireWindows();
        using var fixture = new ExecutableFixture();
        var runner = new VersionRunner();
        var validator = new MpvExecutableApproval(runner);
        var approval = await validator.ValidateAsync(fixture.Executable, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validator.VerifyAsync(approval, cancellation.Token));
        Assert.Equal(1, runner.Calls);
    }

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "外部 mpv 路径规则针对 Windows。");

    private sealed class VersionRunner : IMpvVersionRunner
    {
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public MpvVersionResult Result { get; init; } = new(0, "mpv v0.40.0-25-gabc1234 Copyright © 2000-2026 mpv projects\nlibplacebo: v7");
        public Func<string, CancellationToken, Task<MpvVersionResult>>? Handler { get; init; }
        public Task<MpvVersionResult> RunAsync(string executablePath, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = executablePath;
            return Handler?.Invoke(executablePath, cancellationToken) ?? Task.FromResult(Result);
        }
    }

    private sealed class ExecutableFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mambo-mpv-approval-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Directory, "mpv.exe");
        public ExecutableFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            // The injected runner never starts this intentionally non-executable fixture.
            File.WriteAllText(Executable, "this file is not a Windows executable");
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
