using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;

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
        if (!fake) throw new InvalidOperationException("真实后端尚未注册，请等待 P1 平台层接入。");
        services.AddSingleton<IUiScheduler>(scheduler);
        services.AddSingleton<TimeProvider>(clock ?? TimeProvider.System);
        services.AddSingleton<FakeOptions>(options ?? new FakeOptions());
        services.AddSingleton<IMessenger>(_ => new WeakReferenceMessenger());
        services.AddSingleton(p => new FakeOperation(p.GetRequiredService<FakeOptions>(), p.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new DemoCatalog());
        services.AddSingleton<ISessionService>(p => new FakeSessionService(p.GetRequiredService<FakeOperation>(),
            p.GetRequiredService<IUiScheduler>(), p.GetRequiredService<IMessenger>()));
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
