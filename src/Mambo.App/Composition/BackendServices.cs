using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.BulletChat;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Mambo.App.Platform;
using Microsoft.Extensions.DependencyInjection;
using Mambo.Player.LibMpv;
using Mambo.Player.External;
using Mambo.Core.Playback;
using Mambo.Core.Updates;
using System.Reflection;

namespace Mambo.App.Composition;

public static class BackendServices
{
    public static bool IsFakeMode(IReadOnlyList<string> arguments, string? environmentValue) =>
        arguments.Contains("--fake", StringComparer.Ordinal) || environmentValue == "1";

    // 显式工厂避免 AOT 反射激活；前端只解析 Contracts 接口。
    public static IServiceCollection AddBackendServices(this IServiceCollection services, bool fake,
        IUiScheduler scheduler, FakeOptions? options = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scheduler);
        services.AddSingleton<IUiScheduler>(scheduler);
        services.AddSingleton<TimeProvider>(clock ?? TimeProvider.System);
        services.AddSingleton<FakeOptions>(options ?? (fake ? FakeOptions.FromEnvironment(
            Environment.GetEnvironmentVariable("MAMBO_FAKE_DELAY_MS"), Environment.GetEnvironmentVariable("MAMBO_FAKE_FAILURE_RATE")) : new FakeOptions()));
        services.AddSingleton<IMessenger>(_ => new WeakReferenceMessenger());
        services.AddSingleton<IAppUpdateService>(_ => new GitHubUpdateService(
            fake ? "" : typeof(BackendServices).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(static attribute => attribute.Key == "MamboUpdateRepository")?.Value ?? "",
            typeof(BackendServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mambo", "updates"),
            applicationDirectory: File.Exists(Path.Combine(AppContext.BaseDirectory, "release-manifest.json")) &&
                File.Exists(Path.Combine(AppContext.BaseDirectory, "Mambo.Updater.exe")) ? AppContext.BaseDirectory : null));
        if (!fake)
        {
            services.AddSingleton<ISecretStore>(_ => new WindowsCredentialStore());
            services.AddSingleton<IExternalPlayerValidator>(_ => new MpvExecutableApproval());
            services.AddSingleton(p => new BackendRuntime(new AppPaths(), p.GetRequiredService<ISecretStore>(),
                p.GetRequiredService<IUiScheduler>(), p.GetRequiredService<IMessenger>(), p.GetRequiredService<TimeProvider>(),
                engineFactory: cancellationToken => CreateEngineAsync(p, cancellationToken),
                externalPlayerValidator: p.GetRequiredService<IExternalPlayerValidator>()));
            services.AddSingleton<ISessionService>(p => p.GetRequiredService<BackendRuntime>().Session);
            services.AddSingleton<ILibraryService>(p => p.GetRequiredService<BackendRuntime>().Library);
            services.AddSingleton<ISettingsService>(p => p.GetRequiredService<BackendRuntime>().Settings);
            services.AddSingleton<ILibraryPreferences>(p => p.GetRequiredService<BackendRuntime>().Preferences);
            services.AddSingleton<IImageService>(p => p.GetRequiredService<BackendRuntime>().Images);
            services.AddSingleton<IPlaybackService>(p => p.GetRequiredService<BackendRuntime>().Playback);
            services.AddSingleton<IBulletChatService>(p => p.GetRequiredService<BackendRuntime>().BulletChat);
            services.AddSingleton(p => new AppShutdownCoordinator(p.GetRequiredService<IPlaybackService>(), p.GetRequiredService<IMessenger>(), p.GetRequiredService<BackendRuntime>()));
            return services;
        }
        services.AddSingleton(p => new FakeOperation(p.GetRequiredService<FakeOptions>(), p.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new DemoCatalog());
        services.AddSingleton<ISessionService>(p => new FakeSessionService(p.GetRequiredService<FakeOperation>(),
            p.GetRequiredService<IUiScheduler>(), p.GetRequiredService<IMessenger>(), p.GetRequiredService<IPlaybackService>()));
        services.AddSingleton<ILibraryService>(p => new FakeLibraryService(p.GetRequiredService<DemoCatalog>(),
            p.GetRequiredService<IUiScheduler>(), p.GetRequiredService<FakeOperation>()));
        services.AddSingleton<ISettingsService>(p => new FakeSettingsService(p.GetRequiredService<FakeOperation>(), p.GetRequiredService<IUiScheduler>()));
        services.AddSingleton<ILibraryPreferences>(p => new FakeLibraryPreferences(p.GetRequiredService<IUiScheduler>()));
        services.AddSingleton<IPlaybackService>(p => new FakePlaybackService(p.GetRequiredService<DemoCatalog>(),
            p.GetRequiredService<FakeOperation>(), p.GetRequiredService<FakeOptions>(), p.GetRequiredService<TimeProvider>(),
            p.GetRequiredService<IUiScheduler>(), p.GetRequiredService<IMessenger>()));
        services.AddSingleton<IImageService>(p => new FakeImageService(p.GetRequiredService<DemoCatalog>(), p.GetRequiredService<FakeOperation>()));
        // 演示弹幕也跟随内置引擎，本地真实播放的诊断可以直接看到合成效果。
        services.AddSingleton<IBulletChatService>(p => new BulletChatService(p.GetRequiredService<IPlaybackService>(), p.GetRequiredService<ISettingsService>(),
            new FakeBulletChatProvider(p.GetRequiredService<FakeOperation>()), p.GetRequiredService<IUiScheduler>(),
            static kind => kind is EngineKind.Demo or EngineKind.Embedded));
        services.AddSingleton(p => new AppShutdownCoordinator(p.GetRequiredService<IPlaybackService>(), p.GetRequiredService<IMessenger>()));
        return services;
    }

    private static async Task<IPlayerEngine> CreateEngineAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<BackendRuntime>().Settings;
        var approval = await settings.GetApprovedExternalPlayerAsync(cancellationToken).ConfigureAwait(false);
        if (approval is not null)
            return await ExternalMpvEngine.CreateAsync(approval, services.GetRequiredService<IExternalPlayerValidator>(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        var options = new Dictionary<string, string> { ["hwdec"] = settings.Current.HardwareDecoding == HardwareDecodingMode.Off ? "no" : "d3d11va" };
        return await LibMpvEngine.CreateAsync(1, 1, optionOverrides: options, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
