using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

/// <summary>
/// 首页内容：hero、"最近播放"行、每个可播放库一行"最新"。
/// 媒体库列表决定整体状态（加载中 → 骨架屏，失败 → "首页加载失败"）；各行独立失败、独立重试。
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService library;
    private readonly Navigator navigator;
    private readonly CancellationTokenSource scope = new();
    private readonly IQuery<ImmutableArray<MediaLibrary>> libraries;
    private readonly IQuery<ImmutableArray<MediaItem>> hero;
    private readonly RailViewModel continueRail;
    private readonly Dictionary<string, RailViewModel> latest = [];

    public HomeViewModel(ILibraryService library, Navigator navigator)
    {
        this.library = library;
        this.navigator = navigator;
        libraries = library.ObserveLibraries(scope.Token);
        hero = library.ObserveHero(scope.Token);
        continueRail = new RailViewModel("最近播放", library.ObserveContinueWatching(scope.Token), CardContext.ContinueWatching, landscape: true,
            "查看全部", () => navigator.Navigate(Route.Recent));
        Rails.Add(continueRail);
        libraries.Updated += OnLibrariesUpdated;
        hero.Updated += OnHeroUpdated;
        OnLibrariesUpdated(null, EventArgs.Empty);
        OnHeroUpdated(null, EventArgs.Empty);
    }

    public ObservableCollection<RailViewModel> Rails { get; } = [];
    public IReadOnlyList<HeroSlideViewModel> Slides { get; private set; } = [];

    public event EventHandler? SlidesChanged;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial bool HasContent { get; private set; }

    public async Task RefreshAsync()
    {
        var work = new List<Task> { libraries.RefreshAsync(), hero.RefreshAsync() };
        work.AddRange(Rails.Select(r => r.RefreshAsync()));
        try { await Task.WhenAll(work); }
        catch (OperationCanceledException) { }
    }

    public void Retry() => _ = RefreshAsync();

    public void Dispose()
    {
        libraries.Updated -= OnLibrariesUpdated;
        hero.Updated -= OnHeroUpdated;
        scope.Cancel();
        libraries.Dispose();
        hero.Dispose();
        foreach (var rail in Rails) rail.Dispose();
        scope.Dispose();
    }

    private void OnLibrariesUpdated(object? sender, EventArgs e)
    {
        var items = libraries.ItemsOrEmpty();
        var wanted = items.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in latest.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            Rails.Remove(latest[stale]);
            latest[stale].Dispose();
            latest.Remove(stale);
        }
        var position = 1;
        foreach (var item in items)
        {
            if (!latest.TryGetValue(item.Id, out var rail))
            {
                var id = item.Id;
                rail = new RailViewModel($"最新 · {item.Name}", library.ObserveLatest(id, scope.Token), CardContext.Latest, landscape: false,
                    "查看全部", () => navigator.Navigate(Route.Library(id)));
                latest[id] = rail;
                Rails.Insert(Math.Min(position, Rails.Count), rail);
            }
            else if (Rails.IndexOf(rail) != position)
            {
                Rails.Move(Rails.IndexOf(rail), Math.Min(position, Rails.Count - 1));
            }
            position++;
        }
        IsLoading = !libraries.IsInitialized && libraries.Error is null;
        HasError = libraries.Error is not null && !libraries.IsInitialized;
        HasContent = libraries.IsInitialized;
    }

    private void OnHeroUpdated(object? sender, EventArgs e)
    {
        var slides = hero.ItemsOrEmpty().Where(i => i.Images.Length > 0).Select(i => new HeroSlideViewModel(i)).ToList();
        if (slides.Select(s => s.Id).SequenceEqual(Slides.Select(s => s.Id))) return;
        Slides = slides;
        SlidesChanged?.Invoke(this, EventArgs.Empty);
    }
}
