using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Microsoft.Win32.SafeHandles;

namespace Mambo.Player.External;

/// <summary>可替换版本探测器，供测试验证不可信输出和取消；生产默认直接启动进程。</summary>
public interface IMpvVersionRunner
{
    Task<MpvVersionResult> RunAsync(string executablePath, CancellationToken cancellationToken);
}

/// <summary>只在校验内部使用的有限版本输出；不允许隐式写入日志。</summary>
public sealed record MpvVersionResult(int ExitCode, string StandardOutput, bool OutputLimitExceeded = false)
{
    public override string ToString() => "MpvVersionResult { Output = <redacted> }";
}

/// <summary>本地 mpv.exe 的显式批准与只读指纹复验，不负责文件选择界面。</summary>
public sealed partial class MpvExecutableApproval : IExternalPlayerValidator
{
    public const int MaximumVersionOutputCharacters = 8192;
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(3);
    private readonly IMpvVersionRunner versionRunner;

    public MpvExecutableApproval() : this(new ProcessVersionRunner()) { }

    public MpvExecutableApproval(IMpvVersionRunner versionRunner)
        => this.versionRunner = versionRunner ?? throw new ArgumentNullException(nameof(versionRunner));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Never expose native process, path or untrusted version output at the frontend boundary.")]
    public async Task<ExternalMpvApproval> ValidateAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var normalized = NormalizePath(path);
            EnsureLocalFile(normalized);
            await using var file = OpenLocked(normalized);
            var before = await FingerprintAsync(normalized, file, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(VersionTimeout);
            MpvVersionResult result;
            try
            {
                // WaitAsync also bounds an injected runner that fails to observe cancellation.
                result = await versionRunner.RunAsync(normalized, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw SafeError(ErrorCodes.ExternalVersionTimeout, "MPV 版本校验超时，请重新选择可执行文件。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.OutputLimitExceeded ||
                result.StandardOutput is null || result.StandardOutput.Length > MaximumVersionOutputCharacters)
                throw SafeError(ErrorCodes.ExternalVersionInvalid, "未识别到有效的 MPV 版本，请重新选择可执行文件。");
            var version = ParseVersion(result.StandardOutput);
            EnsureLocalFile(normalized);
            var after = await FingerprintAsync(normalized, file, cancellationToken).ConfigureAwait(false);
            if (before != after)
                throw SafeError(ErrorCodes.ExternalFileChanged, "MPV 文件在校验期间发生变化，请重新选择并批准。");
            return new(normalized, after.Sha256, after.Size, after.LastWriteTimeUtcTicks, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("MPV 校验已取消。", cancellationToken);
        }
        catch (AppException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw SafeError(ErrorCodes.ExternalPathInvalid, "未找到该路径下的 MPV 可执行文件，将使用内置播放器。");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed read-only approval check must safely disable external playback without exposing filesystem details.")]
    public async Task<bool> VerifyAsync(ExternalMpvApproval approval, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (approval is null || approval.Size <= 0 || approval.LastWriteTimeUtcTicks <= 0 ||
                approval.LastWriteTimeUtcTicks > DateTime.MaxValue.Ticks || !ValidSha256(approval.Sha256) ||
                !HasSupportedReleaseVersion(approval.Version))
                return false;
            var normalized = NormalizePath(approval.Path);
            // Stored approvals are canonical absolute file paths; edited aliases need explicit approval.
            if (!Path.IsPathFullyQualified(approval.Path) ||
                !string.Equals(normalized, approval.Path, StringComparison.OrdinalIgnoreCase))
                return false;
            EnsureLocalFile(normalized);
            await using var file = OpenLocked(normalized);
            var fingerprint = await FingerprintAsync(normalized, file, cancellationToken).ConfigureAwait(false);
            EnsureLocalFile(normalized);
            return fingerprint.Size == approval.Size &&
                fingerprint.LastWriteTimeUtcTicks == approval.LastWriteTimeUtcTicks &&
                string.Equals(fingerprint.Sha256, approval.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("MPV 复验已取消。", cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private static string NormalizePath(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (string.IsNullOrWhiteSpace(path)) throw SafeError(ErrorCodes.ExternalPathInvalid, "请选择 MPV 可执行文件。");
        var trimmed = path.Trim();
        if (trimmed.Length > 32767 || trimmed.Any(char.IsControl) || trimmed.StartsWith("\\\\", StringComparison.Ordinal))
            throw SafeError(ErrorCodes.ExternalPathInvalid, "请选择本地 MPV 可执行文件。");
        var normalized = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(normalized);
        // No UNC, Win32 device names, alternate data streams or mapped network disks.
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            normalized.AsSpan(2).Contains(':') || new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw SafeError(ErrorCodes.ExternalPathInvalid, "请选择本地 MPV 可执行文件。");
        if (Directory.Exists(normalized)) normalized = Path.Combine(normalized, "mpv.exe");
        if (normalized.Length > 32767 || !string.Equals(Path.GetFileName(normalized), "mpv.exe", StringComparison.OrdinalIgnoreCase))
            throw SafeError(ErrorCodes.ExternalPathInvalid, "未找到该路径下的 MPV 可执行文件，将使用内置播放器。");
        return normalized;
    }

    private static void EnsureLocalFile(string path)
    {
        // Preserve one stable target: do not approve junctions/symlinks or traverse them on later launches.
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw SafeError(ErrorCodes.ExternalPathInvalid, "请选择本地普通文件中的 MPV 可执行文件。");
        for (var directory = info.Directory; directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw SafeError(ErrorCodes.ExternalPathInvalid, "请选择本地普通目录中的 MPV 可执行文件。");
    }

    private static FileStream OpenLocked(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<FileFingerprint> FingerprintAsync(string path, FileStream file, CancellationToken cancellationToken)
    {
        EnsureHandlePath(path, file);
        var size = file.Length;
        var modified = File.GetLastWriteTimeUtc(path).Ticks;
        if (size <= 0) throw SafeError(ErrorCodes.ExternalPathInvalid, "MPV 可执行文件为空，请重新选择。");
        file.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        EnsureHandlePath(path, file);
        if (file.Length != size || File.GetLastWriteTimeUtc(path).Ticks != modified)
            throw SafeError(ErrorCodes.ExternalFileChanged, "MPV 文件在校验期间发生变化，请重新选择并批准。");
        return new(hash, size, modified);
    }

    internal static unsafe void EnsureHandlePath(string path, FileStream file)
    {
        // Re-check the opened target, not only the path text, if an ancestor changes during a read.
        var buffer = new char[32772]; // Windows maximum path plus the \\?\ prefix and terminating NUL.
        uint length;
        fixed (char* pointer = buffer)
            length = GetFinalPathNameByHandle(file.SafeFileHandle, pointer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw SafeError(ErrorCodes.ExternalPathInvalid, "无法确认 MPV 文件位置，请重新选择。");
        var actual = new string(buffer, 0, (int)length);
        if (!actual.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            !string.Equals(actual[4..], path, StringComparison.OrdinalIgnoreCase))
            throw SafeError(ErrorCodes.ExternalFileChanged, "MPV 文件位置发生变化，请重新选择并批准。");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint characters, uint flags);

    private static bool ValidSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static string ParseVersion(string output)
    {
        using var reader = new StringReader(output);
        // mpv --version begins with its program name, followed by a release or git version.
        var firstLine = reader.ReadLine();
        var match = firstLine is null ? Match.Empty : VersionPattern().Match(firstLine);
        if (!match.Success || match.Groups["version"].Length > 96)
            throw SafeError(ErrorCodes.ExternalVersionInvalid, "未识别到有效的 MPV 版本，请重新选择可执行文件。");
        var version = match.Groups["version"].Value;
        if (!HasSupportedReleaseVersion(version))
            throw SafeError(ErrorCodes.ExternalVersionInvalid, "需要 MPV 0.38.0 或更新版本，请重新选择可执行文件。");
        return version;
    }

    private static bool HasSupportedReleaseVersion(string? value)
    {
        if (value is not { Length: > 0 and <= 96 }) return false;
        var match = ReleaseVersionPattern().Match(value);
        // loadfile gained the insertion-index argument in 0.38.0. Bare git hashes cannot prove compatibility.
        return match.Success && Version.TryParse(match.Groups["release"].Value, out var version) &&
            version >= new Version(0, 38, 0);
    }

    [GeneratedRegex("\\Ampv (?<version>v?[0-9][0-9A-Za-z.+_-]*|(?:git-)?[0-9a-f]{7,40})(?:[ \\t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("\\Av?(?<release>[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4})(?:[-+][0-9A-Za-z._+-]+)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();

    private static AppException SafeError(string code, string message)
        => new(new(AppErrorKind.Player, code, message, false, "external-player-validation"));

    private sealed record FileFingerprint(string Sha256, long Size, long LastWriteTimeUtcTicks);

    private sealed class ProcessVersionRunner : IMpvVersionRunner
    {
        public async Task<MpvVersionResult> RunAsync(string executablePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            };
            start.ArgumentList.Add("--no-config");
            start.ArgumentList.Add("--load-scripts=no");
            start.ArgumentList.Add("--version");
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException();
            using var cancellationRegistration = cancellationToken.Register(static value => TryKill((Process)value!), process);
            var stdout = DrainAsync(process.StandardOutput, true, cancellationToken);
            var stderr = DrainAsync(process.StandardError, false, cancellationToken);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr).ConfigureAwait(false);
                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                return new(process.ExitCode, output.Text, output.Overflow || error.Overflow);
            }
            finally
            {
                // Cancellation of WaitForExitAsync alone does not terminate the subprocess.
                TryKill(process);
            }
        }

        private static async Task<DrainResult> DrainAsync(StreamReader reader, bool capture, CancellationToken cancellationToken)
        {
            var buffer = new char[512];
            var result = capture ? new StringBuilder(MaximumVersionOutputCharacters) : null;
            var remaining = MaximumVersionOutputCharacters;
            var overflow = false;
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var retained = Math.Min(read, remaining);
                if (capture && retained > 0) result!.Append(buffer, 0, retained);
                remaining -= retained;
                overflow |= read > retained;
                // Keep draining after the cap to prevent blocked pipes, while retaining no more data.
            }
            return new(result?.ToString() ?? string.Empty, overflow);
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return;
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (AggregateException) { }
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        private sealed record DrainResult(string Text, bool Overflow);
    }
}
