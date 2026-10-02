using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Mambo.App.Shell;

/// <summary>按路由创建页面；页面和视图模型都是瞬时的，由 PageHost 决定保留多久。</summary>
public sealed class PageFactory(IServiceProvider services)
{
    public FrameworkElement Create(Route route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Kind switch
        {
            PageKind.Home => new HomePage(Get<ShellViewModel>(), Get<ISessionService>(), Get<ILibraryService>(), Get<Navigator>(),
                Get<TitleBarService>(), Get<WindowContext>()),
            PageKind.Settings => new SettingsPage(
                new SettingsViewModel(Get<ISessionService>(), Get<ISettingsService>(), Get<IPlaybackService>(), Get<Navigator>(),
                    Get<ToastService>(), Get<DialogService>(), Get<ThemeService>()),
                Get<WindowContext>(), Get<ToastService>(), Get<DialogService>()),
            PageKind.Recent => new RecentPage(new RecentViewModel(Get<ILibraryService>())),
            PageKind.Library => new LibraryPage(new LibraryViewModel(Get<ILibraryService>(), Get<ILibraryPreferences>(), Library(route.Parameter ?? ""))),
            PageKind.Search => new SearchPage(new SearchViewModel(Get<ILibraryService>(), route.Parameter ?? "")),
            _ => new PlaceholderPage("DETAIL", "详情"),
        };
    }

    private MediaLibrary Library(string id) =>
        Get<ShellViewModel>().LibraryModels.FirstOrDefault(l => l.Id == id) ?? new MediaLibrary(id, "资料库", LibraryKind.Mixed);

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
}
