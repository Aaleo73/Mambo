using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Mambo.App.Composition;

public static class UiServices
{
    // 显式工厂，避免 AOT 下的反射激活。
    public static IServiceCollection AddUiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(_ => new Navigator());
        services.AddSingleton(_ => new ToastService());
        services.AddSingleton(_ => new DialogService());
        services.AddSingleton(p => new AppUpdateViewModel(p.GetRequiredService<IAppUpdateService>(), p.GetRequiredService<ISettingsService>(),
            p.GetRequiredService<DialogService>(), p.GetRequiredService<ToastService>(), p.GetRequiredService<Navigator>(), p.GetRequiredService<WindowContext>()));
        services.AddSingleton(p => new ThemeService(p.GetRequiredService<ISettingsService>()));
        services.AddSingleton(_ => new WindowContext());
        services.AddSingleton(_ => new TitleBarService());
        services.AddSingleton(p => new BrowseTransitionCoordinator(p.GetRequiredService<Navigator>(), p.GetRequiredService<WindowContext>()));
        services.AddSingleton(p => new ShellViewModel(p.GetRequiredService<ISessionService>(), p.GetRequiredService<ILibraryService>(),
            p.GetRequiredService<Navigator>()));
        services.AddSingleton(p => new ImageLoader(p.GetRequiredService<IImageService>()));
        services.AddSingleton(p => new PlaybackLauncher(p.GetRequiredService<IPlaybackService>(), p.GetRequiredService<DialogService>(),
            p.GetRequiredService<ToastService>()));
        services.AddSingleton(p => new CardActions(p.GetRequiredService<Navigator>(), p.GetRequiredService<ILibraryService>(),
            p.GetRequiredService<PlaybackLauncher>()));
        services.AddSingleton(p => new PageFactory(p));
        return services;
    }
}
