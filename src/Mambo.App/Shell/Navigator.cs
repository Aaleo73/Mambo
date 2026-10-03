namespace Mambo.App.Shell;

public enum PageKind
{
    Home,
    Recent,
    Library,
    Detail,
    Search,
    Settings,
}

public enum NavigationMode
{
    New,
    Back,
    Forward,
    Replace,
    Reset,
}

public sealed class Route : IEquatable<Route>
{
    public Route(PageKind kind, string? parameter = null)
    {
        Kind = kind;
        Parameter = parameter;
    }

    public PageKind Kind { get; }
    public string? Parameter { get; }
    public string Key => Parameter is null ? Kind.ToString() : $"{Kind}:{Parameter}";

    public static Route Home { get; } = new(PageKind.Home);
    public static Route Recent { get; } = new(PageKind.Recent);
    public static Route Settings { get; } = new(PageKind.Settings);
    public static Route Library(string libraryId) => new(PageKind.Library, libraryId);
    public static Route Detail(string itemId) => new(PageKind.Detail, itemId);
    public static Route Search(string text) => new(PageKind.Search, text);

    public bool Equals(Route? other) => other is not null && other.Kind == Kind && string.Equals(other.Parameter, Parameter, StringComparison.Ordinal);
    public override bool Equals(object? obj) => Equals(obj as Route);
    public override int GetHashCode() => HashCode.Combine(Kind, Parameter);
}

/// <summary>一条历史记录；页面离开时把滚动位置等视图状态写回这里。</summary>
public sealed class NavEntry(Route route)
{
    public Route Route { get; } = route;
    public double VerticalOffset { get; set; }
    public object? ViewState { get; set; }
}

public sealed class NavigatedEventArgs(NavEntry? from, NavEntry to, NavigationMode mode) : EventArgs
{
    public NavEntry? From { get; } = from;
    public NavEntry To { get; } = to;
    public NavigationMode Mode { get; } = mode;
}

/// <summary>
/// 前进、后退各保留 12 条。重复导航到当前页不做任何事；已在搜索页时再次搜索替换当前记录；
/// 新导航清空前进栈；历史不持久化。
/// </summary>
public sealed class Navigator
{
    public const int MaxHistory = 12;
    private readonly LinkedList<NavEntry> back = new();
    private readonly LinkedList<NavEntry> forward = new();

    public NavEntry Current { get; private set; } = new(Route.Home);
    public bool CanGoBack => back.Count > 0 || BackInterceptor?.CanHandle == true;
    public bool CanGoForward => forward.Count > 0 && !ForwardBlocked;

    /// <summary>播放层打开时禁止前进，后退改为关闭播放层。</summary>
    public bool ForwardBlocked
    {
        get;
        set { field = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public IBackInterceptor? BackInterceptor
    {
        get;
        set { field = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public event EventHandler<NavigatedEventArgs>? Navigated;
    public event EventHandler? StateChanged;

    public void Navigate(Route route)
    {
        if (ForwardBlocked) return;
        if (route.Equals(Current.Route)) return;
        if (route.Kind == PageKind.Search && Current.Route.Kind == PageKind.Search)
        {
            Go(new NavEntry(route), NavigationMode.Replace);
            return;
        }
        Push(back, Current);
        forward.Clear();
        Go(new NavEntry(route), NavigationMode.New);
    }

    /// <summary>重新创建当前失败页面；保留当前记录与前进、后退历史。</summary>
    public void RetryCurrent()
    {
        if (ForwardBlocked) return;
        Go(Current, NavigationMode.Replace);
    }

    public bool GoBack()
    {
        if (BackInterceptor?.TryHandleBack() == true) return true;
        if (back.Last is not { } target) return false;
        back.RemoveLast();
        Push(forward, Current);
        Go(target.Value, NavigationMode.Back);
        return true;
    }

    public bool GoForward()
    {
        if (!CanGoForward || forward.Last is not { } target) return false;
        forward.RemoveLast();
        Push(back, Current);
        Go(target.Value, NavigationMode.Forward);
        return true;
    }

    /// <summary>登录、注销或账号失效后清空历史。</summary>
    public void Reset(Route route)
    {
        back.Clear();
        forward.Clear();
        Go(new NavEntry(route), NavigationMode.Reset);
    }

    private void Go(NavEntry target, NavigationMode mode)
    {
        var from = Current;
        Current = target;
        Navigated?.Invoke(this, new NavigatedEventArgs(from, target, mode));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Push(LinkedList<NavEntry> stack, NavEntry entry)
    {
        stack.AddLast(entry);
        if (stack.Count > MaxHistory) stack.RemoveFirst();
    }
}

public interface IBackInterceptor
{
    bool CanHandle { get; }
    bool TryHandleBack();
}
