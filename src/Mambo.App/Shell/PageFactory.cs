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
            PageKind.Settings => CreateOwned(
                new SettingsViewModel(Get<ISessionService>(), Get<ISettingsService>(), Get<IPlaybackService>(), Get<Navigator>(),
                    Get<ToastService>(), Get<DialogService>(), Get<ThemeService>()),
                model => new SettingsPage(model, Get<WindowContext>(), Get<ToastService>(), Get<DialogService>())),
            PageKind.Recent => CreateOwned(new RecentViewModel(Get<ILibraryService>()), model => new RecentPage(model)),
            PageKind.Library => CreateOwned(new LibraryViewModel(Get<ILibraryService>(), Get<ILibraryPreferences>(), Library(route.Parameter ?? "")), model => new LibraryPage(model)),
            PageKind.Search => CreateOwned(new SearchViewModel(Get<ILibraryService>(), route.Parameter ?? ""), model => new SearchPage(model)),
            PageKind.Detail => CreateOwned(new DetailViewModel(Get<ILibraryService>(), Get<PlaybackLauncher>(), Get<IPlaybackService>(), route.Parameter ?? ""), model => new DetailPage(model, Get<WindowContext>())),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    // XAML 构造失败时页面尚未返回给 PageHost；已创建的模型仍须停止查询、取消页面作用域。
    private static FrameworkElement CreateOwned<T>(T model, Func<T, FrameworkElement> create) where T : IDisposable
    {
        try { return create(model); }
        catch
        {
            try { model.Dispose(); }
            catch (Exception) { }
            throw;
        }
    }

    private MediaLibrary Library(string id) =>
        Get<ShellViewModel>().LibraryModels.FirstOrDefault(l => l.Id == id) ?? new MediaLibrary(id, "资料库", LibraryKind.Mixed);

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
}
