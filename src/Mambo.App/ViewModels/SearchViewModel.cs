using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

public sealed partial class SearchGroupViewModel : ObservableObject, IDisposable
{
    internal SearchGroupViewModel(SearchGroup group, IPagedQuery<MediaItem> query)
    {
        LibraryId = group.LibraryId;
        Name = group.Name;
        Order = group.Order;
        Cards = new PagedCards(query, CardContext.Search, landscape: true);
        Cards.PropertyChanged += OnCardsChanged;
        Cards.Items.CollectionChanged += (_, _) => Update();
        Update();
    }

    public string LibraryId { get; }
    public string Name { get; }
    public int Order { get; }
    public PagedCards Cards { get; }

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    /// <summary>空组隐藏；出错的组只在本组显示错误和重试。</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    [ObservableProperty]
    public partial bool CanLoadMore { get; private set; }

    public void Dispose()
    {
        Cards.PropertyChanged -= OnCardsChanged;
        Cards.Dispose();
    }

    private void OnCardsChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        CountText = Cards.TotalCount is { } total ? $"{total} 项" : $"{Cards.Items.Count} 项";
        IsVisible = Cards.Items.Count > 0 || Cards.HasError;
        CanLoadMore = Cards.HasMore && !Cards.IsLoadingMore && !Cards.HasMoreError;
    }
}

/// <summary>搜索：每个可播放库一组，按库的顺序排列；每组 24 条，"加载更多"手动翻页。</summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService library;
    private readonly CancellationTokenSource scope = new();
    private readonly IQuery<ImmutableArray<SearchGroup>> groups;

    public SearchViewModel(ILibraryService library, string text)
    {
        this.library = library;
        Text = text;
        Subtitle = $"“{text}”的搜索结果";
        NoResultsTitle = $"没有找到“{text}”";
        groups = library.ObserveSearchGroups(scope.Token);
        groups.Updated += (_, _) => BuildGroups();
        BuildGroups();
    }

    public string Text { get; }
    public string Subtitle { get; }
    public string NoResultsTitle { get; }
    public ObservableCollection<SearchGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    public async Task RefreshAsync()
    {
        try { await Task.WhenAll(Groups.Select(g => g.Cards.RefreshAsync()).Append(groups.RefreshAsync())); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        scope.Cancel();
        foreach (var group in Groups) group.Dispose();
        groups.Dispose();
        scope.Dispose();
    }

    private void BuildGroups()
    {
        var wanted = groups.ItemsOrEmpty().OrderBy(g => g.Order).ToList();
        if (!wanted.Select(g => g.LibraryId).SequenceEqual(Groups.Select(g => g.LibraryId)))
        {
            foreach (var old in Groups) { old.PropertyChanged -= OnGroupChanged; old.Dispose(); }
            Groups.Clear();
            foreach (var group in wanted)
            {
                var vm = new SearchGroupViewModel(group, library.ObserveSearch(group.LibraryId, Text, 24, scope.Token));
                vm.PropertyChanged += OnGroupChanged;
                vm.Cards.PropertyChanged += OnGroupChanged;
                Groups.Add(vm);
            }
        }
        UpdateState();
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        IsLoading = !groups.IsInitialized || Groups.Any(g => g.Cards.IsLoadingFirst);
        HasError = groups.Error is not null && !groups.IsInitialized;
        HasNoResults = groups.IsInitialized && !IsLoading && Groups.All(g => g.Cards.IsEmpty);
    }
}
