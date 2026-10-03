using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

public sealed partial class SearchGroupViewModel : ObservableObject, IDisposable
{
    private readonly string normalizedSearch;

    internal SearchGroupViewModel(SearchGroup group, IPagedQuery<MediaItem> query, string normalizedSearch)
    {
        LibraryId = group.LibraryId;
        Name = group.Name;
        this.normalizedSearch = normalizedSearch;
        Cards = new PagedCards(query, CardContext.Search, landscape: true);
        Cards.PropertyChanged += OnCardsChanged;
        Cards.Items.CollectionChanged += OnItemsChanged;
        UpdateMatchRank(Cards.Items, reset: true);
        Update();
    }

    public string LibraryId { get; }
    public string Name { get; }
    internal int SourceOrder { get; set; }
    internal int MatchRank { get; private set; } = 3;
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
        Cards.Items.CollectionChanged -= OnItemsChanged;
        Cards.Dispose();
    }

    private void OnCardsChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { } added)
            UpdateMatchRank(added.Cast<MediaCardViewModel>(), reset: false);
        else
            UpdateMatchRank(Cards.Items, reset: true);
        Update();
    }

    private void UpdateMatchRank(IEnumerable<MediaCardViewModel> cards, bool reset)
    {
        var rank = reset ? 3 : MatchRank;
        if (normalizedSearch.Length > 0 && rank > 0)
        {
            foreach (var card in cards)
            {
                var title = NormalizeSearch(card.Title);
                var candidate = title.Equals(normalizedSearch, StringComparison.OrdinalIgnoreCase) ? 0
                    : title.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase) ? 1
                    : title.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
                rank = Math.Min(rank, candidate);
                if (rank == 0) break;
            }
        }
        if (MatchRank == rank) return;
        MatchRank = rank;
        OnPropertyChanged(nameof(MatchRank));
    }

    internal static string NormalizeSearch(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private void Update()
    {
        CountText = Cards.TotalCount is { } total ? $"{total} 项" : $"{Cards.Items.Count} 项";
        IsVisible = Cards.Items.Count > 0 || Cards.HasError;
        CanLoadMore = Cards.HasMore && !Cards.IsLoadingMore && !Cards.HasMoreError;
    }
}

/// <summary>搜索：按已加载标题的相关性排列媒体库，同级保留库顺序；每组 24 条，"加载更多"手动翻页。</summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService library;
    private readonly CancellationTokenSource scope = new();
    private readonly IQuery<ImmutableArray<SearchGroup>> groups = null!;
    private readonly string normalizedSearch;

    public SearchViewModel(ILibraryService library, string text)
    {
        this.library = library;
        Text = text;
        normalizedSearch = SearchGroupViewModel.NormalizeSearch(text);
        Subtitle = $"“{text}”的搜索结果";
        NoResultsTitle = $"没有找到“{text}”";
        try
        {
            groups = library.ObserveSearchGroups(scope.Token);
            groups.Updated += OnGroupsUpdated;
            BuildGroups();
        }
        catch
        {
            FailedConstruction.Release(scope.Cancel,
                () => { if (groups is not null) groups.Updated -= OnGroupsUpdated; },
                () => { foreach (var group in Groups) FailedConstruction.Release(() => ReleaseGroup(group)); },
                () => groups?.Dispose(), scope.Dispose);
            throw;
        }
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
        try { await Task.WhenAll(Groups.ToArray().Select(g => g.Cards.RefreshAsync()).Append(groups.RefreshAsync())); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        scope.Cancel();
        foreach (var group in Groups) ReleaseGroup(group);
        groups.Updated -= OnGroupsUpdated;
        groups.Dispose();
        scope.Dispose();
    }

    private void OnGroupsUpdated(object? sender, EventArgs e) => BuildGroups();

    private void BuildGroups()
    {
        var wanted = groups.ItemsOrEmpty().OrderBy(g => g.Order).ToArray();
        var wantedIds = wanted.Select(g => g.LibraryId).ToHashSet(StringComparer.Ordinal);
        for (var index = Groups.Count - 1; index >= 0; index--)
        {
            var old = Groups[index];
            if (wantedIds.Contains(old.LibraryId)) continue;
            ReleaseGroup(old);
            Groups.RemoveAt(index);
        }
        for (var index = 0; index < wanted.Length; index++)
        {
            var group = wanted[index];
            var vm = Groups.FirstOrDefault(existing => existing.LibraryId == group.LibraryId);
            if (vm is null)
            {
                vm = new SearchGroupViewModel(group,
                    library.ObserveSearch(group.LibraryId, Text, 24, scope.Token), normalizedSearch);
                vm.PropertyChanged += OnGroupChanged;
                vm.Cards.PropertyChanged += OnGroupChanged;
                Groups.Add(vm);
            }
            vm.SourceOrder = index;
        }
        OrderGroups();
        UpdateState();
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SearchGroupViewModel && e.PropertyName == nameof(SearchGroupViewModel.MatchRank))
            OrderGroups();
        UpdateState();
    }

    private void OrderGroups()
    {
        var ordered = Groups.OrderBy(group => group.MatchRank).ThenBy(group => group.SourceOrder).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = Groups.IndexOf(ordered[index]);
            if (current != index) Groups.Move(current, index);
        }
    }

    private void ReleaseGroup(SearchGroupViewModel group)
    {
        group.PropertyChanged -= OnGroupChanged;
        group.Cards.PropertyChanged -= OnGroupChanged;
        group.Dispose();
    }

    private void UpdateState()
    {
        IsLoading = (!groups.IsInitialized && groups.Error is null) || Groups.Any(g => g.Cards.IsLoadingFirst);
        HasError = groups.Error is not null && !groups.IsInitialized;
        HasNoResults = groups.IsInitialized && !IsLoading && Groups.All(g => g.Cards.IsEmpty);
    }
}
