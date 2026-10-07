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
        var videoQualityUiSmoke = Program.Arguments.Contains(Debug.VideoQualityUiSmoke.Argument, StringComparer.Ordinal);
        if (!videoQualityUiSmoke && LabWindow.IsRequested(Program.Arguments))
        {
            window = new LabWindow();
            window.Activate();
            return;
        }
        var nativeOverlaySmoke = !videoQualityUiSmoke && Program.Arguments.Contains(Debug.NativeOverlaySmoke.Argument, StringComparer.Ordinal);
        var externalHandoffSmoke = !videoQualityUiSmoke && Program.Arguments.Contains(Debug.ExternalHandoffSmoke.Argument, StringComparer.Ordinal);
        var uiSmoke = Program.Arguments.Contains("--ui-smoke", StringComparer.Ordinal);
        var bulletChatSmoke = Program.Arguments.Contains(Debug.BulletChatSmoke.Argument, StringComparer.Ordinal);
        var fake = videoQualityUiSmoke || uiSmoke || bulletChatSmoke || BackendServices.IsFakeMode(Program.Arguments, Environment.GetEnvironmentVariable("MAMBO_FAKE"));
        if (videoQualityUiSmoke)
        {
            Environment.SetEnvironmentVariable("MAMBO_FAKE_DELAY_MS", "10");
            Environment.SetEnvironmentVariable("MAMBO_FAKE_FAILURE_RATE", "0");
        }
        if (fake && Debug.FakeLifetimeProbe.IsActive)
            UnhandledException += (_, failure) => Debug.FakeLifetimeProbe.Record(failure.Exception);
        var services = externalHandoffSmoke
            ? Debug.ExternalHandoffSmoke.CreateServices(DispatcherQueue.GetForCurrentThread())
            : nativeOverlaySmoke
            ? Debug.NativeOverlaySmoke.CreateServices(DispatcherQueue.GetForCurrentThread())
            : new ServiceCollection()
                .AddBackendServices(fake, new UiScheduler(DispatcherQueue.GetForCurrentThread()))
                .AddUiServices()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Debug.StartupTimeline.Mark("ServicesBuilt");
        // 先解析退出协调器：它同时挂接 WinUI 异常日志。
        _ = services.GetRequiredService<AppShutdownCoordinator>();
        Debug.StartupTimeline.Mark("BackendResolved");
        var main = new MainWindow(services);
        Debug.StartupTimeline.Mark("MainWindowCreated");
        window = main;
        _ = Debug.StartupTimeline.ObserveAsync(main);
        main.Activate();
        Debug.StartupTimeline.Mark("Activated");
        main.StartSession();
        if (videoQualityUiSmoke)
        {
            // 设置、账号和缓存均为本进程内存假服务；报告也使用本轮独立目录。
            var reportPath = Environment.GetEnvironmentVariable("MAMBO_VIDEO_QUALITY_UI_REPORT");
            if (string.IsNullOrWhiteSpace(reportPath))
                reportPath = Path.Combine(Path.GetTempPath(), "Mambo", "video-quality-ui", Guid.NewGuid().ToString("N"), "app-report.json");
            _ = Debug.VideoQualityUiSmoke.RunAsync(main, reportPath);
        }
        else if (externalHandoffSmoke)
            _ = Debug.ExternalHandoffSmoke.RunAsync(main);
        else if (nativeOverlaySmoke)
            _ = Debug.NativeOverlaySmoke.RunAsync(main);
        else if (uiSmoke)
            _ = Debug.UiLabSmoke.RunAsync(main, Environment.GetEnvironmentVariable("MAMBO_UI_LAB_REPORT") ?? "");
        else if (bulletChatSmoke)
            _ = Debug.BulletChatSmoke.RunAsync(main, Environment.GetEnvironmentVariable("MAMBO_BULLET_CHAT_REPORT") ?? "");
        else if (Program.Arguments.Contains("--startup-smoke", StringComparer.Ordinal))
            _ = Debug.StartupTimeline.RunProbeAsync(main, Environment.GetEnvironmentVariable("MAMBO_STARTUP_REPORT") ?? "");
    }
}
