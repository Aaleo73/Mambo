using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public async Task OpenReaderCanFinishOldDocumentAfterAtomicReplacement()
    {
        using var sandbox = new Sandbox();
        await AtomicFile.WriteAsync(sandbox.FilePath, "{\"value\":1}"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        await using var reader = new FileStream(sandbox.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        await WriteWithDiagnosticAsync(sandbox.FilePath, "{\"value\":2}"u8.ToArray(), TestContext.Current.CancellationToken);
        using var text = new StreamReader(reader, leaveOpen: true);
        Assert.Equal("{\"value\":1}", await text.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"value\":2}", await File.ReadAllTextAsync(sandbox.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TransientWindowsDestinationLockIsRetriedAfterUnlock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "此测试验证 Windows 原子替换的文件共享语义。");
        using var sandbox = new Sandbox();
        await AtomicFile.WriteAsync(sandbox.FilePath, "original"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        var destinationLock = new FileStream(sandbox.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task replacement;
        try
        {
            replacement = AtomicFile.WriteAsync(sandbox.FilePath, "replacement"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
            // Wait until the temporary document is fully flushed and its writer has closed.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!replacement.IsCompleted && !TemporaryWriterHasClosed(sandbox.Root))
                await Task.Delay(1, timeout.Token);
            await Task.Delay(30, timeout.Token);
            if (replacement.IsFaulted)
            {
                var failure = await Assert.ThrowsAsync<AppException>(() => replacement);
                Assert.Fail($"临时锁的替换提前失败，安全 HRESULT：{failure.Error.DiagnosticId}");
            }
            Assert.False(replacement.IsCompleted, "短暂共享冲突应进入有界重试，而不是立即丢弃替换操作。");
        }
        finally { destinationLock.Dispose(); }
        await replacement.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("replacement", await File.ReadAllTextAsync(sandbox.FilePath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.tmp"));
    }

    [Fact]
    public async Task CancellationDuringWindowsDestinationLockKeepsOriginalDocumentAndCleansTemporaryFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "此测试验证 Windows 原子替换的文件共享语义。");
        using var sandbox = new Sandbox();
        await AtomicFile.WriteAsync(sandbox.FilePath, "original"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        using var destinationLock = new FileStream(sandbox.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var replacement = AtomicFile.WriteAsync(sandbox.FilePath, "replacement"u8.ToArray(), cancellationToken: cancellation.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!replacement.IsCompleted && !TemporaryWriterHasClosed(sandbox.Root))
            await Task.Delay(1, timeout.Token);
        if (replacement.IsFaulted)
        {
            var failure = await Assert.ThrowsAsync<AppException>(() => replacement);
            Assert.Fail($"取消前的替换提前失败，安全 HRESULT：{failure.Error.DiagnosticId}");
        }
        Assert.False(replacement.IsCompleted, "目标文件仍被占用，替换应等待有限重试。");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);
        Assert.Equal("original", await File.ReadAllTextAsync(sandbox.FilePath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.tmp"));
    }

    [Fact]
    public async Task PersistentWindowsLockReturnsSafeErrorAndKeepsOriginalDocument()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "此测试验证 Windows 原子替换的文件共享语义。");
        using var sandbox = new Sandbox();
        await AtomicFile.WriteAsync(sandbox.FilePath, "original"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        using var destinationLock = new FileStream(sandbox.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = await Assert.ThrowsAsync<AppException>(() => AtomicFile.WriteAsync(sandbox.FilePath,
            "replacement"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(AppErrorKind.Persistence, failure.Error.Kind);
        Assert.Equal(ErrorCodes.PersistenceFailed, failure.Error.Code);
        Assert.False(string.IsNullOrEmpty(failure.Error.DiagnosticId));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(sandbox.Root, failure.Message, StringComparison.Ordinal);
        Assert.Equal("original", await File.ReadAllTextAsync(sandbox.FilePath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.tmp"));
    }

    private static bool TemporaryWriterHasClosed(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException) { }
        }
        return false;
    }

    private static async Task WriteWithDiagnosticAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        try { await AtomicFile.WriteAsync(path, bytes, cancellationToken: cancellationToken); }
        catch (AppException failure) { Assert.Fail($"原子替换失败，安全 HRESULT：{failure.Error.DiagnosticId}"); }
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(FindRoot(), "artifacts", "tests", "atomic-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(Root, "document.json");
        public Sandbox() => Directory.CreateDirectory(Root);
        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mambo.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? AppContext.BaseDirectory;
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.Combine(FindRoot(), "artifacts", "tests")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录超出 artifacts/tests。");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
