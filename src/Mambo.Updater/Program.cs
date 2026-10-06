using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.Core.Contracts;
using Mambo.Core.Updates;
using Microsoft.Win32;

namespace Mambo.Updater;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] is not ("--apply" or "--recover" or "--uninstall-cleanup")) return 2;
        if (args[0] == "--uninstall-cleanup")
        {
            try
            {
                if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])).Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) return 2;
                UpdateFiles.RemoveInstalledFiles(args[1]);
                return 0;
            }
            catch (Exception) { return 1; }
        }
        string? applicationDirectory = null;
        var parentExited = false;
        var succeeded = false;
        var stage = "Request";
        var staging = Path.GetFullPath(args[1]);
        FileStream? updateLock = null;
        try
        {
            var request = UpdateFiles.ReadRequest(staging);
            applicationDirectory = Path.GetFullPath(request.ApplicationDirectory);
            UpdateFiles.EnsurePlainPath(applicationDirectory);
            if (staging.StartsWith(Path.TrimEndingDirectorySeparator(applicationDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
            var executable = UpdateFiles.Resolve(applicationDirectory, "Mambo.exe");
            using var parent = GetParent(request, executable, allowExited: args[0] == "--recover");
            var lockPath = Path.Combine(applicationDirectory, UpdateFiles.LockFile);
            UpdateFiles.EnsurePlainPath(lockPath);
            updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (args[0] == "--apply")
            {
                stage = "Verify";
                var manifestPath = UpdateFiles.Resolve(staging, "update.json");
                if (!UpdateFiles.IsHash(request.ManifestSha256) || !await UpdateFiles.MatchesAsync(manifestPath,
                    new("update.json", new FileInfo(manifestPath).Length, request.ManifestSha256))) throw new AppUpdateException("更新清单校验失败。");
                await UpdateTransaction.VerifyAsync(applicationDirectory, staging);
            }
            File.WriteAllText(UpdateFiles.Resolve(staging, "ready"), "ready");
            stage = "WaitForExit";
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                while (parent is not null && !parent.HasExited)
                {
                    if (File.Exists(UpdateFiles.Resolve(staging, "cancel"))) return Cancel(staging);
                    await Task.Delay(100, deadline.Token);
                }
            }
            if (File.Exists(UpdateFiles.Resolve(staging, "cancel"))) return Cancel(staging);
            if (parent is not null && parent.ExitCode != 0) return Cancel(staging);
            parentExited = true;
            parent?.Dispose();
            stage = "Apply";
            if (args[0] == "--recover") UpdateTransaction.Recover(applicationDirectory);
            else
            {
                await UpdateTransaction.ApplyAsync(applicationDirectory, staging);
                UpdateRegistration(applicationDirectory, UpdateFiles.ReadManifest(Path.Combine(staging, "update.json")).Version);
            }
            File.WriteAllText(UpdateFiles.Resolve(staging, "result"), "success");
            succeeded = true;
        }
        catch (Exception error)
        {
            // Fixed text only: never expose file/HTTP exceptions or signed download URLs.
            try
            {
                File.WriteAllText(UpdateFiles.Resolve(staging, "result"), "failed");
                File.WriteAllBytes(UpdateFiles.Resolve(staging, "failure.json"), JsonSerializer.SerializeToUtf8Bytes(
                    new UpdateFailure(stage, error.GetType().Name, error.HResult, error is AppUpdateException ? error.Message : ""), HelperJsonContext.Default.UpdateFailure));
            }
            catch (Exception) { }
            if (parentExited)
                _ = MessageBoxW(0, applicationDirectory is not null && Directory.Exists(Path.Combine(applicationDirectory, UpdateFiles.TransactionDirectory))
                    ? "更新恢复未完成，原文件已保留。请重新启动 Mambo 以重试恢复。"
                    : "更新未能完成，已保留原版本。请稍后重新下载更新。", "Mambo 更新", 0x10);
            else return 1;
        }
        finally { updateLock?.Dispose(); }

        if (parentExited && applicationDirectory is not null && !Directory.Exists(Path.Combine(applicationDirectory, UpdateFiles.TransactionDirectory)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(UpdateFiles.Resolve(applicationDirectory, "Mambo.exe"))
                { UseShellExecute = false, WorkingDirectory = applicationDirectory })?.Dispose();
            }
            catch (Exception) { _ = MessageBoxW(0, "更新处理已结束，请手动打开 Mambo。", "Mambo 更新", 0x10); return 1; }
        }
        return succeeded ? 0 : 1;
    }

    private static int Cancel(string staging)
    {
        File.WriteAllText(UpdateFiles.Resolve(staging, "result"), "cancelled");
        return 3;
    }

    private static Process? GetParent(UpdateRequest request, string executable, bool allowExited)
    {
        Process process;
        try { process = Process.GetProcessById(request.ParentProcessId); }
        catch (ArgumentException) when (allowExited) { return null; }
        try
        {
            // GetProcessById does not retain a handle by itself. Open it while the parent is alive;
            // otherwise HasExited may succeed after exit but ExitCode has no process to query.
            _ = process.Handle;
            if (allowExited && process.HasExited) { process.Dispose(); return null; }
            var matches = process.StartTime.ToUniversalTime().Ticks == request.ParentStartTimeUtcTicks &&
                string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
            if (allowExited && process.HasExited) { process.Dispose(); return null; }
            if (!matches)
                throw new AppUpdateException("更新进程身份不匹配。");
            return process;
        }
        catch (Exception error) when (allowExited && error is InvalidOperationException or System.ComponentModel.Win32Exception && process.HasExited)
        { process.Dispose(); return null; }
        catch { process.Dispose(); throw; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void UpdateRegistration(string directory, string version)
    {
        // Portable copies never create registration or shortcuts.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A36B6A2B-B280-4C39-9517-C22230A61E8C}_is1", writable: true);
            if (key?.GetValue("InstallLocation") is string location &&
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)).Equals(Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase))
                key.SetValue("DisplayVersion", version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint window, string text, string caption, uint type);
}

internal sealed record UpdateFailure(string Stage, string ErrorKind, int HResult, string Message);
[JsonSerializable(typeof(UpdateFailure))]
internal sealed partial class HelperJsonContext : JsonSerializerContext;
