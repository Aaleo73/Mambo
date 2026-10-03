using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mambo.App;

public static class Program
{
    internal static Platform.SingleInstanceLifetime? Instance { get; private set; }
    internal static readonly Stopwatch Startup = Stopwatch.StartNew();
    private static readonly long ProcessAgeAtEntryMilliseconds = ReadProcessAge();
    internal static long UptimeMilliseconds => ProcessAgeAtEntryMilliseconds + Startup.ElapsedMilliseconds;
    internal static string[] Arguments { get; private set; } = [];
    private static long ReadProcessAge()
    {
        using var process = Process.GetCurrentProcess();
        return Math.Max(0, (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds - Startup.ElapsedMilliseconds);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Arguments = args;
        // This diagnostic client has no App, window, services or single-instance registration.
        // Reject malformed invocations without falling through to a normal application launch.
        if (args.Contains(Debug.NativeUiaProbe.Argument, StringComparer.Ordinal))
        {
            Environment.ExitCode = args.Length == 1
                ? Debug.NativeUiaProbe.RunHelperAsync().GetAwaiter().GetResult() : 2;
            return;
        }
        Debug.FakeLifetimeProbe.Initialize(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        using var instance = Platform.SingleInstanceLifetime.Register(args);
        if (instance is null) return;
        Instance = instance;
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        Debug.FakeLifetimeProbe.Mark("ApplicationStartReturned");
        Instance = null;
    }
}
