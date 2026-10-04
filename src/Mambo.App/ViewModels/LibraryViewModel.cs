using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

public sealed partial class FilterChipViewModel : ObservableObject
{
    internal FilterChipViewModel(string group, string label, string value)
    {
        Group = group;
        Label = label;
        Value = value;
    }

    public string Group { get; }
    public string Label { get; }
    public string Value { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

public sealed partial class SortOptionViewModel : ObservableObject
{
    internal SortOptionViewModel(LibrarySort sort, SortDirection direction, string label)
    {
        Sort = sort;
        Direction = direction;
        Label = label;
    }

    public LibrarySort Sort { get; }
    public SortDirection Direction { get; }
    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 资料库：排序与筛选按库记忆（ILibraryPreferences）。条件改变时旧结果保留到新结果到达，然后滚回顶部。
/// 年份按年代显示，请求时展开成具体年份；组内取"或"，组间取"与"。
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    public const string GenreGroup = "genre";
    public const string DecadeGroup = "decade";
    public const string RatingGroup = "rating";
    private readonly ILibraryService library;
    private readonly ILibraryPreferences preferences;
    private readonly string libraryId;
    private readonly CancellationTokenSource scope = new();
    private readonly IQuery<FilterOptions> filters = null!;
    private PagedCards? pending;
    private LibraryQuery query;

    public LibraryViewModel(ILibraryService library, ILibraryPreferences preferences, MediaLibrary model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.library = library;
        this.preferences = preferences;
        libraryId = model.Id;
        Title = model.Name;
        query = preferences.Get(libraryId);
        Sorts =
        [
            new(LibrarySort.DateCreated, SortDirection.Descending, "添加日期"),
            new(LibrarySort.Name, SortDirection.Ascending, "名称"),
            new(LibrarySort.CommunityRating, SortDirection.Descending, "评分"),
            new(LibrarySort.ProductionYear, SortDirection.Descending, "年份"),
        ];
        if (model.Kind == LibraryKind.Movies) Sorts.Add(new(LibrarySort.Runtime, SortDirection.Descending, "时长"));
        try
        {
            filters = library.ObserveFilters(libraryId, scope.Token);
            filters.Updated += OnFiltersUpdated;
            Cards = Observe(query);
            Cards.PropertyChanged += OnCardsPropertyChanged;
            Cards.Items.CollectionChanged += OnCardItemsChanged;
            BuildChips();
            ApplyQueryState();
        }
        catch
        {
            FailedConstruction.Release(scope.Cancel,
                () => { if (Cards is not null) Cards.PropertyChanged -= OnCardsPropertyChanged; },
                () => { if (Cards is not null) Cards.Items.CollectionChanged -= OnCardItemsChanged; },
                () => Cards?.Dispose(),
                () => { if (filters is not null) filters.Updated -= OnFiltersUpdated; },
                () => filters?.Dispose(), scope.Dispose);
            throw;
        }
    }

    public string Title { get; }
    public ObservableCollection<SortOptionViewModel> Sorts { get; }
    public ObservableCollection<FilterChipViewModel> GenreChips { get; } = [];
    public ObservableCollection<FilterChipViewModel> DecadeChips { get; } = [];
    public ObservableCollection<FilterChipViewModel> RatingChips { get; } = [];

    /// <summary>当前显示的结果；新条件的首屏到达前保持不变。</summary>
    [ObservableProperty]
    public partial PagedCards Cards { get; private set; }

    [ObservableProperty]
    public partial bool FiltersOpen { get; set; }

    [ObservableProperty]
    public partial int FilterCount { get; private set; }

    [ObservableProperty]
    public partial string SortLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    [ObservableProperty]
    public partial bool ShowFilteredEmpty { get; private set; }

    [ObservableProperty]
    public partial bool ShowEmpty { get; private set; }

    public bool HasFilters => FilterCount > 0;
    public string FilterCountText => FilterCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>新结果替换旧结果后触发，页面据此滚回顶部。</summary>
    public event EventHandler? ResultsReplaced;

    public void SetSort(SortOptionViewModel option)
    {
        ArgumentNullException.ThrowIfNull(option);
        Apply(query with { Sort = option.Sort, Direction = option.Direction });
    }

    public void Toggle(FilterChipViewModel chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        if (chip.Value.Length == 0)
        {
            ClearGroup(chip.Group);
            return;
        }
        var available = filters.Current ?? new FilterOptions();
        switch (chip.Group)
        {
            case GenreGroup:
                Apply(query with { Genres = Flip(query.Genres, chip.Value) });
                break;
            case RatingGroup:
                Apply(query with { OfficialRatings = Flip(query.OfficialRatings, chip.Value) });
                break;
            case DecadeGroup:
                var decade = int.Parse(chip.Value, CultureInfo.InvariantCulture);
                var years = available.Years.Where(y => y / 10 * 10 == decade).ToArray();
                var selected = query.Years.Any(y => y / 10 * 10 == decade);
                Apply(query with { Years = selected ? query.Years.RemoveAll(y => y / 10 * 10 == decade) : query.Years.AddRange(years.Except(query.Years)) });
                break;
        }
    }

    public void ClearGroup(string group) => Apply(group switch
    {
        GenreGroup => query with { Genres = [] },
        DecadeGroup => query with { Years = [] },
        _ => query with { OfficialRatings = [] },
    });

    public void ResetFilters() => Apply(query with { Genres = [], Years = [], OfficialRatings = [] });

    public Task LoadMoreAsync() => Cards.LoadMoreAsync();
    public Task RefreshAsync() => Task.WhenAll(Cards.RefreshAsync(), filters.RefreshAsync());

    public void Dispose()
    {
        scope.Cancel();
        Cards.PropertyChanged -= OnCardsPropertyChanged;
        Cards.Items.CollectionChanged -= OnCardItemsChanged;
        Cards.Dispose();
        pending?.Dispose();
        filters.Updated -= OnFiltersUpdated;
        filters.Dispose();
        scope.Dispose();
    }

    private static ImmutableArray<string> Flip(ImmutableArray<string> values, string value) =>
        values.Contains(value) ? values.Remove(value) : values.Add(value);

    private PagedCards Observe(LibraryQuery value) => new(library.ObserveLibrary(libraryId, value, 60, scope.Token), CardContext.Library, landscape: false);

    private void Apply(LibraryQuery next)
    {
        if (next == query) return;
        query = next;
        _ = preferences.SetAsync(libraryId, next);
        pending?.Dispose();
        pending = Observe(next);
        pending.PropertyChanged += OnPendingPropertyChanged;
        ApplyQueryState();
        SwapIfReady();
    }

    private void OnPendingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => SwapIfReady();

    private void SwapIfReady()
    {
        if (pending is null || (!pending.IsInitialized && !pending.HasError)) return;
        var previous = Cards;
        previous.PropertyChanged -= OnCardsPropertyChanged;
        previous.Items.CollectionChanged -= OnCardItemsChanged;
        pending.PropertyChanged -= OnPendingPropertyChanged;
        Cards = pending;
        Cards.PropertyChanged += OnCardsPropertyChanged;
        Cards.Items.CollectionChanged += OnCardItemsChanged;
        pending = null;
        previous.Dispose();
        UpdateCount();
        UpdateStates();
        ResultsReplaced?.Invoke(this, EventArgs.Empty);
    }

    private void OnCardsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PagedCards.TotalCount) or nameof(PagedCards.IsInitialized) or nameof(PagedCards.HasMore)) UpdateCount();
        if (e.PropertyName is nameof(PagedCards.IsEmpty)) UpdateStates();
    }

    private void OnCardItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateCount();

    private void UpdateCount() =>
        CountText = Cards.TotalCount is { } total ? $"{total.ToString("N0", CultureInfo.GetCultureInfo("zh-CN"))} 项"
            : !Cards.IsInitialized ? "" : Cards.HasMore ? $"已加载 {Cards.Items.Count} 项" : $"{Cards.Items.Count} 项";

    private void ApplyQueryState()
    {
        FilterCount = query.Genres.Length + query.OfficialRatings.Length + query.Years.Select(y => y / 10).Distinct().Count();
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(FilterCountText));
        foreach (var sort in Sorts) sort.IsSelected = sort.Sort == query.Sort;
        SortLabel = Sorts.FirstOrDefault(s => s.IsSelected)?.Label ?? Sorts[0].Label;
        foreach (var chip in GenreChips) chip.IsChecked = chip.Value.Length == 0 ? query.Genres.IsEmpty : query.Genres.Contains(chip.Value);
        foreach (var chip in RatingChips) chip.IsChecked = chip.Value.Length == 0 ? query.OfficialRatings.IsEmpty : query.OfficialRatings.Contains(chip.Value);
        foreach (var chip in DecadeChips)
            chip.IsChecked = chip.Value.Length == 0 ? query.Years.IsEmpty : query.Years.Any(y => (y / 10 * 10).ToString(CultureInfo.InvariantCulture) == chip.Value);
        UpdateStates();
    }

    private void UpdateStates()
    {
        ShowFilteredEmpty = Cards.IsEmpty && HasFilters;
        ShowEmpty = Cards.IsEmpty && !HasFilters;
    }

    private void OnFiltersUpdated(object? sender, EventArgs e) => BuildChips();

    private void BuildChips()
    {
        var options = filters.Current ?? new FilterOptions();
        Sync(GenreChips, options.Genres.Select(g => (g, g)));
        Sync(DecadeChips, options.Years.Select(y => y / 10 * 10).Distinct().OrderByDescending(d => d)
            .Select(d => ($"{d} 年代", d.ToString(CultureInfo.InvariantCulture))));
        Sync(RatingChips, options.OfficialRatings.Select(r => (r, r)));
        ApplyQueryState();

        void Sync(ObservableCollection<FilterChipViewModel> target, IEnumerable<(string Label, string Value)> source)
        {
            var list = source.Prepend((Label: "全部", Value: "")).ToList();
            if (list.Select(p => p.Value).SequenceEqual(target.Select(c => c.Value))) return;
            var group = ReferenceEquals(target, GenreChips) ? GenreGroup : ReferenceEquals(target, DecadeChips) ? DecadeGroup : RatingGroup;
            target.Clear();
            foreach (var (label, value) in list) target.Add(new FilterChipViewModel(group, label, value));
        }
    }
}
