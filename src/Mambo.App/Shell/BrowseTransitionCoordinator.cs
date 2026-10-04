using Mambo.App.ViewModels;
using Mambo.Core.Contracts;

namespace Mambo.App.Shell;

internal enum BrowseTransitionKind { None, CrossFade, MediaCrossFade, DetailEnter, DetailLeave }

/// <summary>浏览交接与唯一背景的窗口级所有者，不持有卡片容器、坐标或封面快照。</summary>
public sealed class BrowseTransitionCoordinator : IDisposable
{
    private readonly Navigator navigator;
    private HeroBackdropPresenter? backdrop;
    private CancellationTokenSource navigationLifetime = new();
    private NavEntry? owner;
    private NavEntry? committedHeroOwner;
    private HeroSlideViewModel? committedHero;
    private HeroSlideViewModel? openingHero;
    private HeroReturn? heroReturn;
    private NavEntry? returningOwner;
    private int generation;
    private int completedGeneration = -1;
    private bool disposed;

    public BrowseTransitionCoordinator(Navigator navigator, WindowContext window)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(window);
        this.navigator = navigator;
        Window = window;
    }

    internal WindowContext Window { get; }
    internal HeroBackdropPresenter? Backdrop => backdrop;
    internal Task PendingBackdrop => backdrop?.PendingTransition ?? Task.CompletedTask;
    internal NavEntry? Owner => owner;

    internal event Action<Exception>? Failed;
    internal event Action<NavEntry>? NavigationCompleted;

    internal void ReportFailure(Exception error)
    {
        if (disposed) return;
        if (Failed is { } report) report(error);
        else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }

    internal void Attach(HeroBackdropPresenter presenter)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(presenter);
        if (ReferenceEquals(backdrop, presenter)) return;
        if (backdrop is not null) throw new InvalidOperationException("浏览背景已连接。");
        backdrop = presenter;
        presenter.Initialize(Window);
    }

    internal BrowseTransitionKind BeginNavigation(NavigatedEventArgs args)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        AdvanceGeneration();
        owner = args.To;
        returningOwner = null;
        if (openingHero is { } opening && args.Mode == NavigationMode.New &&
            ReferenceEquals(args.From, committedHeroOwner) && args.To.Route.Equals(Route.Detail(opening.Id)))
        {
            heroReturn = new HeroReturn(args.From!, args.To, opening);
        }
        else if (heroReturn is { } saved && args.Mode == NavigationMode.Back &&
            ReferenceEquals(args.From, saved.Detail) && ReferenceEquals(args.To, saved.Source))
        {
            returningOwner = args.To;
        }
        else
        {
            heroReturn = null;
        }
        committedHeroOwner = null;
        committedHero = null;
        if (args.To.Route.Kind is not (PageKind.Home or PageKind.Detail)) backdrop?.Clear();

        if (args.From is null || args.Mode == NavigationMode.Reset ||
            (args.Mode == NavigationMode.Replace && args.From.Route.Kind == PageKind.Search && args.To.Route.Kind == PageKind.Search))
            return BrowseTransitionKind.None;
        if (args.To.Route.Kind == PageKind.Detail && args.From.Route.Kind != PageKind.Detail &&
            args.Mode is NavigationMode.New or NavigationMode.Forward)
            return BrowseTransitionKind.DetailEnter;
        if (args.From.Route.Kind == PageKind.Detail && args.To.Route.Kind != PageKind.Detail)
            return BrowseTransitionKind.DetailLeave;
        if (args.From.Route.Kind is PageKind.Library or PageKind.Recent && args.To.Route.Kind is PageKind.Library or PageKind.Recent)
            return BrowseTransitionKind.MediaCrossFade;
        return BrowseTransitionKind.CrossFade;
    }

    internal void CompleteNavigation(NavEntry presentedOwner)
    {
        if (disposed || !ReferenceEquals(presentedOwner, owner) || completedGeneration == generation) return;
        completedGeneration = generation;
        if (ReferenceEquals(returningOwner, presentedOwner))
        {
            returningOwner = null;
            heroReturn = null;
        }
        NavigationCompleted?.Invoke(presentedOwner);
    }

    internal HeroSlideViewModel? RestoredHero(NavEntry requestedOwner) =>
        ReferenceEquals(requestedOwner, returningOwner) ? heroReturn?.Slide : null;

    internal void CommitHero(NavEntry requestedOwner, HeroSlideViewModel slide)
    {
        if (disposed || !ReferenceEquals(requestedOwner, owner) || requestedOwner.Route.Kind != PageKind.Home) return;
        committedHeroOwner = requestedOwner;
        committedHero = slide;
    }

    internal void OpenHero(HeroSlideViewModel slide)
    {
        if (disposed || navigator.ForwardBlocked || !ReferenceEquals(owner, navigator.Current) ||
            !ReferenceEquals(owner, committedHeroOwner) || !ReferenceEquals(slide, committedHero)) return;
        openingHero = slide;
        try { navigator.Navigate(Route.Detail(slide.Id)); }
        finally { openingHero = null; }
    }

    internal async Task<PreparedBackdrop?> PrepareBackdropAsync(NavEntry requestedOwner, ImageRef? image,
        int decodeWidth, CancellationToken cancellationToken)
    {
        if (disposed || !ReferenceEquals(requestedOwner, owner) || backdrop is null) return null;
        var version = generation;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(navigationLifetime.Token, cancellationToken);
        var transferred = false;
        try
        {
            var prepared = await backdrop.PrepareAsync(image, decodeWidth, linked.Token);
            if (prepared is null) return null;
            if (disposed || prepared.IsDisposed || version != generation || !ReferenceEquals(requestedOwner, owner) || linked.IsCancellationRequested)
            {
                prepared.Dispose();
                return null;
            }
            prepared.Owner = requestedOwner;
            prepared.OwnerGeneration = version;
            prepared.OwnerCancellation = linked;
            transferred = true;
            return prepared;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return null; }
        finally { if (!transferred) linked.Dispose(); }
    }

    internal Task<bool> CommitBackdropAsync(NavEntry requestedOwner, PreparedBackdrop prepared, bool animate, Action? committed = null)
    {
        var version = prepared.OwnerGeneration;
        bool IsCurrent() => !disposed && version == generation && ReferenceEquals(requestedOwner, owner) &&
            ReferenceEquals(prepared.Owner, requestedOwner);
        if (!IsCurrent() || backdrop is null)
        {
            prepared.Dispose();
            return Task.FromResult(false);
        }
        return backdrop.CommitAsync(prepared, animate, IsCurrent, committed);
    }

    internal void UpdateBackdropGeometry(NavEntry requestedOwner, double verticalOffset, double width, double height)
    {
        if (!disposed && ReferenceEquals(requestedOwner, owner)) backdrop?.UpdateGeometry(verticalOffset, width, height);
    }

    internal void SettleBackdrop() => backdrop?.Settle();

    internal void Clear()
    {
        if (disposed) return;
        AdvanceGeneration();
        owner = committedHeroOwner = returningOwner = null;
        committedHero = openingHero = null;
        heroReturn = null;
        backdrop?.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear();
        disposed = true;
        navigationLifetime.Dispose();
        backdrop?.Dispose();
        backdrop = null;
        Failed = null;
        NavigationCompleted = null;
    }

    private void AdvanceGeneration()
    {
        generation++;
        navigationLifetime.Cancel();
        navigationLifetime.Dispose();
        navigationLifetime = new CancellationTokenSource();
    }

    private sealed record HeroReturn(NavEntry Source, NavEntry Detail, HeroSlideViewModel Slide);
}
