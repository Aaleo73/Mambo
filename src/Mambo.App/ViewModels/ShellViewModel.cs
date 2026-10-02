using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

public sealed partial class SidebarLibraryItem : ObservableObject
{
    internal SidebarLibraryItem(MediaLibrary library)
    {
        Id = library.Id;
        Name = library.Name;
        Kind = library.Kind;
        Glyph = library.Kind switch
        {
            LibraryKind.Movies => "",
            LibraryKind.TvShows => "",
            _ => "",
        };
    }

    public string Id { get; }
    public string Name { get; }
    public string Glyph { get; }
    internal LibraryKind Kind { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>外壳状态：会话、侧栏媒体库、导航高亮与标题栏标题。</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ISessionService session;
    private readonly ILibraryService library;
    private readonly Navigator navigator;
    private IQuery<ImmutableArray<MediaLibrary>>? libraries;
    private CancellationTokenSource? accountScope;
    private string? accountKey;
    private bool sessionKnown;

    public ShellViewModel(ISessionService session, ILibraryService library, Navigator navigator)
    {
        this.session = session;
        this.library = library;
        this.navigator = navigator;
        session.Changed += OnSessionChanged;
        navigator.Navigated += OnNavigated;
        navigator.StateChanged += OnNavigatorStateChanged;
        ApplySession(initial: true);
        UpdateNavigationState();
    }

    public ObservableCollection<SidebarLibraryItem> Libraries { get; } = [];

    /// <summary>首次收到会话通知前，未登录视为恢复中，避免启动时闪出引导页。</summary>
    [ObservableProperty]
    public partial SessionState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchTip))]
    public partial bool IsLoggedIn { get; private set; }

    public string? SearchTip => IsLoggedIn ? null : "连接服务器后可用";

    [ObservableProperty]
    public partial bool HasLibraries { get; private set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; private set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; private set; }

    [ObservableProperty]
    public partial bool IsHomeActive { get; private set; }

    [ObservableProperty]
    public partial bool IsRecentActive { get; private set; }

    [ObservableProperty]
    public partial bool IsSettingsActive { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string TitleText { get; set; } = "";

    public IReadOnlyList<MediaLibrary> LibraryModels { get; private set; } = [];

    /// <summary>账号变化（登录、注销、失效）时触发，外壳据此丢弃页面并重置历史。</summary>
    public event EventHandler? AccountChanged;

    public void Dispose()
    {
        session.Changed -= OnSessionChanged;
        navigator.Navigated -= OnNavigated;
        navigator.StateChanged -= OnNavigatorStateChanged;
        DisposeLibraries();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        sessionKnown = true;
        ApplySession(initial: false);
    }

    private void ApplySession(bool initial)
    {
        var current = session.Current;
        State = !sessionKnown && session.State == SessionState.LoggedOut ? SessionState.Restoring : session.State;
        IsLoggedIn = session.State == SessionState.LoggedIn || (current is not null && session.State == SessionState.Restoring);
        var key = current is null ? null : current.ServerId + "|" + current.UserId;
        if (key == accountKey && !initial) return;
        var changed = key != accountKey;
        accountKey = key;
        DisposeLibraries();
        if (key is not null)
        {
            accountScope = new CancellationTokenSource();
            libraries = library.ObserveLibraries(accountScope.Token);
            libraries.Updated += OnLibrariesUpdated;
            OnLibrariesUpdated(null, EventArgs.Empty);
        }
        if (changed && !initial) AccountChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLibrariesUpdated(object? sender, EventArgs e)
    {
        var items = libraries.ItemsOrEmpty();
        LibraryModels = items;
        // 后台刷新得到相同的库时不重建，避免侧栏闪烁。
        if (!items.Select(l => (l.Id, l.Name, l.Kind)).SequenceEqual(Libraries.Select(l => (l.Id, l.Name, l.Kind))))
        {
            Libraries.Clear();
            foreach (var item in items) Libraries.Add(new SidebarLibraryItem(item));
        }
        HasLibraries = Libraries.Count > 0;
        UpdateNavigationState();
    }

    private void DisposeLibraries()
    {
        if (libraries is not null)
        {
            libraries.Updated -= OnLibrariesUpdated;
            libraries.Dispose();
            libraries = null;
        }
        accountScope?.Cancel();
        accountScope?.Dispose();
        accountScope = null;
        LibraryModels = [];
        Libraries.Clear();
        HasLibraries = false;
    }

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        if (e.To.Route.Kind == PageKind.Search) SearchText = e.To.Route.Parameter ?? "";
        UpdateNavigationState();
    }

    private void OnNavigatorStateChanged(object? sender, EventArgs e) => UpdateNavigationState();

    private void UpdateNavigationState()
    {
        var route = navigator.Current.Route;
        CanGoBack = navigator.CanGoBack;
        CanGoForward = navigator.CanGoForward;
        IsHomeActive = route.Kind == PageKind.Home;
        IsRecentActive = route.Kind == PageKind.Recent;
        IsSettingsActive = route.Kind == PageKind.Settings;
        foreach (var item in Libraries) item.IsActive = route.Kind == PageKind.Library && route.Parameter == item.Id;
    }
}
