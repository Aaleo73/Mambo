using System.Diagnostics;
using System.Runtime.InteropServices;
using Mambo.Core.Updates;

namespace Mambo.App.Composition;

internal static partial class UpdateBootstrap
{
    // Runs before WinUI and single-instance registration. A partially applied update must recover first.
    internal static bool CanStart()
    {
        try { return CheckAndRecover(); }
        catch (Exception)
        {
            _ = MessageBoxW(0, "无法恢复上次更新。原文件已保留，请检查应用目录权限后重试。", "Mambo 更新", 0x10);
            return false;
        }
    }

    private static bool CheckAndRecover()
    {
        var directory = AppContext.BaseDirectory;
        var lockPath = Path.Combine(directory, UpdateFiles.LockFile);
        if (File.Exists(lockPath))
        {
            try { using var check = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException) { return false; }
        }
        if (!Directory.Exists(Path.Combine(directory, UpdateFiles.TransactionDirectory))) return true;
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mambo", "updates", Guid.NewGuid().ToString("N"));
        UpdateFiles.EnsurePlainPath(staging);
        Directory.CreateDirectory(staging);
        var helper = UpdateFiles.Resolve(staging, "Mambo.Updater.exe");
        File.Copy(UpdateFiles.Resolve(directory, "Mambo.Updater.exe"), helper);
        using var parent = Process.GetCurrentProcess();
        UpdateFiles.WriteRequest(staging, new(directory, parent.Id, parent.StartTime.ToUniversalTime().Ticks, ""));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = staging };
        start.ArgumentList.Add("--recover");
        start.ArgumentList.Add(staging);
        Process.Start(start)?.Dispose();
        return false;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint window, string text, string caption, uint type);
}
