using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mambo.App;

public static class Program
{
    internal static readonly Stopwatch Startup = Stopwatch.StartNew();
    private static readonly DateTime ProcessStartedUtc = ReadProcessStart();
    internal static long UptimeMilliseconds => Math.Max(0, (long)(DateTime.UtcNow - ProcessStartedUtc).TotalMilliseconds);
    internal static string[] Arguments { get; private set; } = [];
    private static DateTime ReadProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Arguments = args;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
