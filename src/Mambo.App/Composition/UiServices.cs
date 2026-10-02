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
        services.AddSingleton(_ => new ThemeService());
        services.AddSingleton(_ => new WindowContext());
        services.AddSingleton(p => new ShellViewModel(p.GetRequiredService<ISessionService>(), p.GetRequiredService<ILibraryService>(),
            p.GetRequiredService<Navigator>()));
        services.AddSingleton(p => new PageFactory(p));
        return services;
    }
}
