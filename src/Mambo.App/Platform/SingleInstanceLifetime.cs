using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;

namespace Mambo.App.Platform;

/// <summary>创建窗口前注册单实例；STA 用 COM 等待泵重定向，不阻塞 COM 回调。</summary>
internal sealed partial class SingleInstanceLifetime : IDisposable
{
    private readonly AppInstance instance;
    private Action? activate;
    private bool disposed;

    private SingleInstanceLifetime(AppInstance instance)
    {
        this.instance = instance;
        instance.Activated += OnActivated;
    }

    internal static SingleInstanceLifetime? Register(string[] args)
    {
        // 诊断是独立进程，不能重定向到正在使用的正常窗口。
        var diagnostic = Windowing.LabWindow.IsRequested(args) || args.Any(argument => argument is "--ui-smoke" or "--startup-smoke" or "--native-overlay-smoke");
        var fake = args.Contains("--fake", StringComparer.Ordinal) || Environment.GetEnvironmentVariable("MAMBO_FAKE") == "1";
        var key = diagnostic ? "Mambo:diagnostic:" + Environment.ProcessId : fake ? "Mambo:fake" : "Mambo";
        var target = AppInstance.FindOrRegisterForKey(key);
        if (target.IsCurrent) return new(target);

        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // 让原窗口响应重定向时有资格回到前台，不查找或操作其他进程的窗口。
        _ = AllowSetForegroundWindow(target.ProcessId);
        var redirect = Task.Run(async () =>
        {
            try { await target.RedirectActivationToAsync(activation); }
            finally
            {
                try { completed.Set(); }
                catch (ObjectDisposedException) { }
            }
        });
        nint[] handles = [completed.SafeWaitHandle.DangerousGetHandle()];
        var result = CoWaitForMultipleHandles(0, 10000, 1, handles, out _);
        if (result < 0)
        {
            // 延迟完成仍观察异常，退出不再创建第二个可写用户数据的实例。
            _ = redirect.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Environment.ExitCode = 1;
            return null;
        }
        try { redirect.GetAwaiter().GetResult(); }
        catch (Exception) { Environment.ExitCode = 1; }
        return null;
    }

    internal void Attach(Action activation) => activate = activation;

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        if (!disposed) activate?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        Debug.FakeLifetimeProbe.Mark("InstanceDisposeStarted");
        disposed = true;
        activate = null;
        instance.Activated -= OnActivated;
        Debug.FakeLifetimeProbe.Mark("InstanceEventRemoved");
        instance.UnregisterKey();
        Debug.FakeLifetimeProbe.Mark("InstanceKeyRemoved");
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleHandles(uint flags, uint milliseconds, uint count,
        [In] nint[] handles, out uint index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
