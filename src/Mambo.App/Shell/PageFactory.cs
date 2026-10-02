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
            PageKind.Recent => new PlaceholderPage("RECENT", "最近播放"),
            PageKind.Library => new PlaceholderPage("LIBRARY", LibraryName(route.Parameter)),
            PageKind.Search => new PlaceholderPage("SEARCH", "搜索"),
            _ => new PlaceholderPage("DETAIL", "详情"),
        };
    }

    private string LibraryName(string? id) => Get<ShellViewModel>().LibraryModels.FirstOrDefault(l => l.Id == id)?.Name ?? "资料库";

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
}
