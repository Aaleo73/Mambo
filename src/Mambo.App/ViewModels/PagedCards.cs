using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

/// <summary>
/// 把分页观察同步成卡片集合：追加页只追加，刷新才重建；按 id 去重。
/// 首屏失败与后续页失败分开表示，便于显示"重试"与"重试加载"。
/// </summary>
public sealed partial class PagedCards : ObservableObject, IDisposable
{
    private readonly IPagedQuery<MediaItem> query;
    private readonly CardContext context;
    private readonly bool landscape;
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);

    public PagedCards(IPagedQuery<MediaItem> query, CardContext context, bool landscape)
    {
        this.query = query;
        this.context = context;
        this.landscape = landscape;
        try
        {
            query.Updated += OnUpdated;
            Apply();
        }
        catch
        {
            FailedConstruction.Release(() => query.Updated -= OnUpdated, query.Dispose);
            throw;
        }
    }

    public ObservableCollection<MediaCardViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsInitialized { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingFirst { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; private set; }

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial bool HasMoreError { get; private set; }

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = "";

    [ObservableProperty]
    public partial int? TotalCount { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public async Task LoadMoreAsync()
    {
        if (!query.HasMore || query.IsLoading) return;
        try { await query.LoadMoreAsync(); }
        catch (OperationCanceledException) { }
    }

    public async Task RefreshAsync()
    {
        try { await query.RefreshAsync(); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        query.Updated -= OnUpdated;
        query.Dispose();
    }

    private void OnUpdated(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var items = query.Items.IsDefault ? [] : query.Items;
        var prefix = Items.Count <= items.Length;
        for (var i = 0; prefix && i < Items.Count; i++)
            if (!ReferenceEquals(Items[i].Item, items[i]) && Items[i].Item != items[i]) prefix = false;
        if (!prefix)
        {
            Items.Clear();
            ids.Clear();
        }
        for (var i = prefix ? Items.Count : 0; i < items.Length; i++)
            if (ids.Add(items[i].Id)) Items.Add(new MediaCardViewModel(items[i], context, landscape));
        IsInitialized = query.IsInitialized;
        IsLoadingFirst = !query.IsInitialized && query.Error is null;
        IsLoadingMore = query.IsInitialized && query.IsLoading && !query.IsRefreshing;
        HasMore = query.HasMore;
        HasError = query.Error is not null && Items.Count == 0;
        HasMoreError = query.Error is not null && Items.Count > 0;
        ErrorText = query.Error?.Message ?? "";
        TotalCount = query.TotalCount;
        IsEmpty = query.IsInitialized && query.Error is null && Items.Count == 0;
    }
}
