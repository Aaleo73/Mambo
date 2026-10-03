using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

/// <summary>首页的一行卡片：独立加载、独立失败、独立重试。</summary>
public sealed partial class RailViewModel : ObservableObject, IDisposable
{
    private readonly IQuery<ImmutableArray<MediaItem>> query;
    private readonly CardContext context;
    private readonly Action? openLink;

    public RailViewModel(string title, IQuery<ImmutableArray<MediaItem>> query, CardContext context, bool landscape, string? linkText = null, Action? openLink = null)
    {
        Title = title;
        this.query = query;
        this.context = context;
        IsLandscape = landscape;
        LinkText = linkText ?? "";
        this.openLink = openLink;
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

    public string Title { get; }
    public string LinkText { get; }
    public bool HasLink => openLink is not null && LinkText.Length > 0;
    public bool IsLandscape { get; }
    public ObservableCollection<MediaCardViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = "";

    /// <summary>加载中、出错或有内容时显示；成功但为空时整行隐藏。</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; private set; } = true;

    [ObservableProperty]
    public partial bool HasItems { get; private set; }

    public void Retry() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        try { await query.RefreshAsync(); }
        catch (OperationCanceledException) { }
    }

    public void OpenLink() => openLink?.Invoke();

    public void Dispose()
    {
        query.Updated -= OnUpdated;
        query.Dispose();
    }

    private void OnUpdated(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var items = query.ItemsOrEmpty();
        if (!items.Select(i => i.Id).SequenceEqual(Items.Select(i => i.Id)) || items.Length != Items.Count || !SameContent(items))
        {
            Items.Clear();
            foreach (var item in items) Items.Add(new MediaCardViewModel(item, context, IsLandscape));
        }
        IsLoading = !query.IsInitialized && query.Error is null;
        HasError = query.Error is not null && Items.Count == 0;
        ErrorText = query.Error?.Message ?? "";
        HasItems = Items.Count > 0;
        IsVisible = IsLoading || HasError || HasItems;
    }

    // 进度变化（播放停止后后端刷新）也需要更新卡片。
    private bool SameContent(ImmutableArray<MediaItem> items)
    {
        for (var i = 0; i < items.Length; i++)
            if (!ReferenceEquals(items[i], Items[i].Item) && items[i] != Items[i].Item) return false;
        return true;
    }
}
