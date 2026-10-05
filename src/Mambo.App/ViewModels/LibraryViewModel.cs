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
    private LibraryQuery displayedQuery;
    private CancellationTokenSource? applyCancellation;
    private bool disposed;

    public LibraryViewModel(ILibraryService library, ILibraryPreferences preferences, MediaLibrary model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.library = library;
        this.preferences = preferences;
        libraryId = model.Id;
        Title = model.Name;
        var savedQuery = preferences.Get(libraryId);
        query = savedQuery.Years.IsEmpty ? savedQuery : savedQuery with
        {
            Years = savedQuery.Years.Select(year => year / 10 * 10).Distinct()
                .SelectMany(decade => Enumerable.Range(Math.Max(1, decade), decade == 0 ? 9 : 10)).Order().ToImmutableArray(),
        };
        displayedQuery = query;
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
            if (!SameQuery(savedQuery, query)) _ = SavePreferencesAsync(query, scope.Token);
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

    [ObservableProperty]
    public partial bool IsApplying { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplyError))]
    public partial string ApplyErrorText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsLoadingFilters { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterError))]
    public partial string FilterErrorText { get; private set; } = "";

    public bool HasApplyError => ApplyErrorText.Length > 0;
    public bool HasFilterError => FilterErrorText.Length > 0;

    public bool HasFilters => FilterCount > 0;

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
        switch (chip.Group)
        {
            case GenreGroup:
                Apply(query with { Genres = Flip(query.Genres, chip.Value) }, debounce: true);
                break;
            case RatingGroup:
                Apply(query with { OfficialRatings = Flip(query.OfficialRatings, chip.Value) }, debounce: true);
                break;
            case DecadeGroup:
                var decade = int.Parse(chip.Value, CultureInfo.InvariantCulture);
                // Options may still be arriving. A decade always means all ten years,
                // including years absent from the first loaded page.
                var years = Enumerable.Range(Math.Max(1, decade), decade == 0 ? 9 : 10);
                var selected = query.Years.Any(y => y / 10 * 10 == decade);
                Apply(query with { Years = selected ? query.Years.RemoveAll(y => y / 10 * 10 == decade) : query.Years.AddRange(years.Except(query.Years)) }, debounce: true);
                break;
        }
    }

    public void ClearGroup(string group) => Apply(group switch
    {
        GenreGroup => query with { Genres = [] },
        DecadeGroup => query with { Years = [] },
        _ => query with { OfficialRatings = [] },
    });

    public Task LoadMoreAsync() => IsApplying ? Task.CompletedTask : Cards.LoadMoreAsync();
    public Task RefreshAsync() => Task.WhenAll(RetryResultsAsync(), RefreshFiltersAsync());
    public async Task RefreshFiltersAsync()
    {
        try { await filters.RefreshAsync(scope.Token); }
        catch (OperationCanceledException) { }
    }
    public Task RetryResultsAsync()
    {
        if (pending is not null || IsApplying || HasApplyError)
        {
            Apply(query, force: true);
            return Task.CompletedTask;
        }
        return Cards.RefreshAsync();
    }

    public void CancelApply()
    {
        DiscardPending();
        query = displayedQuery;
        IsApplying = false;
        ApplyErrorText = "";
        ApplyQueryState();
        _ = SavePreferencesAsync(query, scope.Token);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        scope.Cancel();
        DiscardPending();
        Cards.PropertyChanged -= OnCardsPropertyChanged;
        Cards.Items.CollectionChanged -= OnCardItemsChanged;
        Cards.Dispose();
        filters.Updated -= OnFiltersUpdated;
        filters.Dispose();
        scope.Dispose();
    }

    private static ImmutableArray<string> Flip(ImmutableArray<string> values, string value) =>
        values.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? values.RemoveAll(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)) : values.Add(value);

    private PagedCards Observe(LibraryQuery value) => new(library.ObserveLibrary(libraryId, value, 60, scope.Token), CardContext.Library, landscape: false);

    private static bool SameQuery(LibraryQuery left, LibraryQuery right) =>
        left.Sort == right.Sort && left.Direction == right.Direction &&
        left.Genres.SequenceEqual(right.Genres, StringComparer.OrdinalIgnoreCase) && left.Years.SequenceEqual(right.Years) &&
        left.OfficialRatings.SequenceEqual(right.OfficialRatings, StringComparer.OrdinalIgnoreCase);

    private void Apply(LibraryQuery next, bool debounce = false, bool force = false)
    {
        if (disposed || !force && SameQuery(next, query)) return;
        DiscardPending();
        query = next;
        ApplyErrorText = "";
        if (!force && SameQuery(next, displayedQuery) && Cards.IsInitialized && !Cards.HasError)
        {
            IsApplying = false;
            ApplyQueryState();
            _ = SavePreferencesAsync(next, scope.Token);
            return;
        }
        IsApplying = true;
        ApplyQueryState();
        applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        _ = ApplyAsync(next, debounce, applyCancellation.Token);
    }

    private void DiscardPending()
    {
        applyCancellation?.Cancel();
        applyCancellation?.Dispose();
        applyCancellation = null;
        if (pending is null) return;
        pending.PropertyChanged -= OnPendingPropertyChanged;
        pending.Dispose();
        pending = null;
    }

    private async Task ApplyAsync(LibraryQuery next, bool debounce, CancellationToken token)
    {
        try
        {
            if (debounce) await Task.Delay(200, token);
            token.ThrowIfCancellationRequested();
            pending = Observe(next);
            pending.PropertyChanged += OnPendingPropertyChanged;
            SwapIfReady();
            await SavePreferencesAsync(next, token);
        }
        catch (OperationCanceledException) { }
        catch (AppException error)
        {
            if (token.IsCancellationRequested) return;
            IsApplying = false;
            ApplyErrorText = error.Error.Message;
        }
    }

    private async Task SavePreferencesAsync(LibraryQuery value, CancellationToken token)
    {
        try { await preferences.SetAsync(libraryId, value, token); }
        catch (OperationCanceledException) { }
        catch (AppException error)
        {
            if (!disposed && !token.IsCancellationRequested) ApplyErrorText = error.Error.Message;
        }
    }

    private void OnPendingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => SwapIfReady();

    private void SwapIfReady()
    {
        if (disposed || pending is null) return;
        if (pending.HasError && Cards.Items.Count > 0)
        {
            IsApplying = false;
            ApplyErrorText = pending.ErrorText;
            return;
        }
        // PagedCards publishes IsInitialized before the rest of its properties. Wait until
        // its first-loading state has settled so the previous grid is replaced atomically.
        if (!pending.HasError && (!pending.IsInitialized || pending.IsLoadingFirst)) return;
        var previous = Cards;
        previous.PropertyChanged -= OnCardsPropertyChanged;
        previous.Items.CollectionChanged -= OnCardItemsChanged;
        pending.PropertyChanged -= OnPendingPropertyChanged;
        Cards = pending;
        Cards.PropertyChanged += OnCardsPropertyChanged;
        Cards.Items.CollectionChanged += OnCardItemsChanged;
        pending = null;
        displayedQuery = query;
        IsApplying = false;
        ApplyErrorText = "";
        previous.Dispose();
        BuildChips();
        UpdateCount();
        UpdateStates();
        ResultsReplaced?.Invoke(this, EventArgs.Empty);
    }

    private void OnCardsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PagedCards.TotalCount) or nameof(PagedCards.IsInitialized) or nameof(PagedCards.HasMore)) UpdateCount();
        if (e.PropertyName is nameof(PagedCards.IsEmpty)) UpdateStates();
        if (e.PropertyName is nameof(PagedCards.IsInitialized) or nameof(PagedCards.IsLoadingMore)) BuildChips();
    }

    private void OnCardItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateCount();
    }

    private void UpdateCount() =>
        CountText = Cards.TotalCount is { } total ? $"{total.ToString("N0", CultureInfo.GetCultureInfo("zh-CN"))} 项"
            : !Cards.IsInitialized ? "" : Cards.HasMore ? $"已加载 {Cards.Items.Count} 项" : $"{Cards.Items.Count} 项";

    private void ApplyQueryState()
    {
        FilterCount = query.Genres.Length + query.OfficialRatings.Length + query.Years.Select(y => y / 10).Distinct().Count();
        OnPropertyChanged(nameof(HasFilters));
        foreach (var sort in Sorts) sort.IsSelected = sort.Sort == query.Sort;
        SortLabel = Sorts.FirstOrDefault(s => s.IsSelected)?.Label ?? Sorts[0].Label;
        foreach (var chip in GenreChips) chip.IsChecked = chip.Value.Length == 0 ? query.Genres.IsEmpty : query.Genres.Contains(chip.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var chip in RatingChips) chip.IsChecked = chip.Value.Length == 0 ? query.OfficialRatings.IsEmpty : query.OfficialRatings.Contains(chip.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var chip in DecadeChips)
            chip.IsChecked = chip.Value.Length == 0 ? query.Years.IsEmpty : query.Years.Any(y => (y / 10 * 10).ToString(CultureInfo.InvariantCulture) == chip.Value);
        UpdateStates();
    }

    private void UpdateStates()
    {
        var displayedHasFilters = !displayedQuery.Genres.IsEmpty || !displayedQuery.Years.IsEmpty || !displayedQuery.OfficialRatings.IsEmpty;
        ShowFilteredEmpty = Cards.IsEmpty && displayedHasFilters;
        ShowEmpty = Cards.IsEmpty && !displayedHasFilters;
    }

    private void OnFiltersUpdated(object? sender, EventArgs e) => BuildChips();

    private void BuildChips()
    {
        var options = filters.Current ?? new FilterOptions();
        var items = Cards.Items.Select(card => card.Item).ToArray();
        IsLoadingFilters = filters.IsRefreshing;
        FilterErrorText = filters.Error?.Message ?? "";
        Sync(GenreChips, options.Genres.Concat(items.SelectMany(item => item.Genres)).Concat(query.Genres)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(g => (g, g)));
        Sync(DecadeChips, options.Years.Concat(items.Where(item => item.ProductionYear.HasValue).Select(item => item.ProductionYear!.Value))
            .Concat(query.Years).Where(y => y is > 0 and <= 9999).Select(y => y / 10 * 10).Distinct().OrderByDescending(d => d)
            .Select(d => ($"{d} 年代", d.ToString(CultureInfo.InvariantCulture))));
        Sync(RatingChips, options.OfficialRatings.Concat(items.Select(item => item.OfficialRating).OfType<string>()).Concat(query.OfficialRatings)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(r => (r, r)));
        ApplyQueryState();

        void Sync(ObservableCollection<FilterChipViewModel> target, IEnumerable<(string Label, string Value)> source)
        {
            var list = source.Prepend((Label: "全部", Value: "")).ToList();
            if (list.Select(p => p.Value).SequenceEqual(target.Select(c => c.Value))) return;
            var group = ReferenceEquals(target, GenreChips) ? GenreGroup : ReferenceEquals(target, DecadeChips) ? DecadeGroup : RatingGroup;
            // Preserve existing chip instances and keyboard focus as more options arrive.
            for (var i = 0; i < list.Count; i++)
            {
                var (label, value) = list[i];
                var existing = target.FirstOrDefault(chip => chip.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (existing is null) target.Insert(i, new FilterChipViewModel(group, label, value));
                else if (target.IndexOf(existing) != i) target.Move(target.IndexOf(existing), i);
            }
            while (target.Count > list.Count) target.RemoveAt(target.Count - 1);
        }
    }
}
