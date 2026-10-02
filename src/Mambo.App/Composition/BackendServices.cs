using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Mambo.App.Platform;
using Microsoft.Extensions.DependencyInjection;
using Mambo.Player.LibMpv;

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
        services.AddSingleton<FakeOptions>(options ?? new FakeOptions());
        services.AddSingleton<IMessenger>(_ => new WeakReferenceMessenger());
        if (!fake)
        {
            services.AddSingleton<ISecretStore>(_ => new WindowsCredentialStore());
            services.AddSingleton(p => new BackendRuntime(new AppPaths(), p.GetRequiredService<ISecretStore>(),
                p.GetRequiredService<IUiScheduler>(), p.GetRequiredService<IMessenger>(), p.GetRequiredService<TimeProvider>(),
                engineFactory: async cancellationToken => await LibMpvEngine.CreateAsync(1, 1,
                    optionOverrides: new Dictionary<string, string> { ["hwdec"] = p.GetRequiredService<ISettingsService>().Current.HardwareDecoding == HardwareDecodingMode.Off ? "no" : "d3d11va" },
                    cancellationToken: cancellationToken).ConfigureAwait(false)));
            services.AddSingleton<ISessionService>(p => p.GetRequiredService<BackendRuntime>().Session);
            services.AddSingleton<ILibraryService>(p => p.GetRequiredService<BackendRuntime>().Library);
            services.AddSingleton<ISettingsService>(p => p.GetRequiredService<BackendRuntime>().Settings);
            services.AddSingleton<ILibraryPreferences>(p => p.GetRequiredService<BackendRuntime>().Preferences);
            services.AddSingleton<IImageService>(p => p.GetRequiredService<BackendRuntime>().Images);
            services.AddSingleton<IPlaybackService>(p => p.GetRequiredService<BackendRuntime>().Playback);
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
        services.AddSingleton(p => new AppShutdownCoordinator(p.GetRequiredService<IPlaybackService>(), p.GetRequiredService<IMessenger>()));
        return services;
    }
}
