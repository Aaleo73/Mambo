using Mambo.App.Composition;
using Mambo.App.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mambo.App;

public sealed partial class App : Application
{
    private Window? window;

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (LabWindow.IsRequested(Program.Arguments))
        {
            window = new LabWindow();
            window.Activate();
            return;
        }
        var fake = BackendServices.IsFakeMode(Program.Arguments, Environment.GetEnvironmentVariable("MAMBO_FAKE"));
        var services = new ServiceCollection()
            .AddBackendServices(fake, new UiScheduler(DispatcherQueue.GetForCurrentThread()))
            .AddUiServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        // 先解析退出协调器：它同时挂接 WinUI 异常日志（R-003）。
        _ = services.GetRequiredService<AppShutdownCoordinator>();
        var main = new MainWindow(services);
        window = main;
        main.Activate();
        main.StartSession();
    }
}
