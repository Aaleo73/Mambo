using Mambo.App.Composition;
using Mambo.App.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mambo.App;

public sealed partial class App : Application
{
    private Window? window;

    public App() { Debug.StartupTimeline.Mark("AppConstructor"); InitializeComponent(); Debug.StartupTimeline.Mark("AppResourcesLoaded"); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Debug.StartupTimeline.Mark("OnLaunched");
        if (LabWindow.IsRequested(Program.Arguments))
        {
            window = new LabWindow();
            window.Activate();
            return;
        }
        var uiSmoke = Program.Arguments.Contains("--ui-smoke", StringComparer.Ordinal);
        var fake = uiSmoke || BackendServices.IsFakeMode(Program.Arguments, Environment.GetEnvironmentVariable("MAMBO_FAKE"));
        if (fake && Debug.FakeLifetimeProbe.IsActive)
            UnhandledException += (_, failure) => Debug.FakeLifetimeProbe.Record(failure.Exception);
        var services = new ServiceCollection()
            .AddBackendServices(fake, new UiScheduler(DispatcherQueue.GetForCurrentThread()))
            .AddUiServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Debug.StartupTimeline.Mark("ServicesBuilt");
        // 先解析退出协调器：它同时挂接 WinUI 异常日志（R-003）。
        _ = services.GetRequiredService<AppShutdownCoordinator>();
        Debug.StartupTimeline.Mark("BackendResolved");
        var main = new MainWindow(services);
        Debug.StartupTimeline.Mark("MainWindowCreated");
        window = main;
        _ = Debug.StartupTimeline.ObserveAsync(main);
        main.Activate();
        Debug.StartupTimeline.Mark("Activated");
        main.StartSession();
        if (uiSmoke)
            _ = Debug.UiLabSmoke.RunAsync(main, Environment.GetEnvironmentVariable("MAMBO_UI_LAB_REPORT") ?? "");
        else if (Program.Arguments.Contains("--startup-smoke", StringComparer.Ordinal))
            _ = Debug.StartupTimeline.RunProbeAsync(main, Environment.GetEnvironmentVariable("MAMBO_STARTUP_REPORT") ?? "");
    }
}
