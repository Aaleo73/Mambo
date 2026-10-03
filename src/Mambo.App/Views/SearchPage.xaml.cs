using Mambo.App.Shell;
using Mambo.App.ViewModels;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Mambo.App.Views;

public sealed partial class SearchPage : UserControl, INavigablePage, IDisposable
{
    private CancellationTokenSource? restoration;
    private IDisposable? scrollRestore;
    public SearchPage(SearchViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnUserScroll), true);
    }

    public SearchViewModel ViewModel { get; }

    /// <summary>返回搜索页时恢复滚动位置（B5）。</summary>
    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (created)
        {
            restoration = new CancellationTokenSource();
            _ = RestoreAsync(entry.ViewState as SearchViewState, entry.VerticalOffset, restoration);
        }
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CancelRestore();
        entry.VerticalOffset = Scroller.VerticalOffset;
        entry.ViewState = new SearchViewState(ViewModel.Groups.ToDictionary(group => group.LibraryId, group => group.Cards.Items.Count, StringComparer.Ordinal));
    }

    public void Refresh() { CancelRestore(); _ = ViewModel.RefreshAsync(); }
    public void Dispose()
    {
        CancelRestore();
        Scroller.RemoveHandler(PointerWheelChangedEvent, new PointerEventHandler(OnUserScroll));
        ViewModel.Dispose();
    }

    private async Task RestoreAsync(SearchViewState? state, double offset, CancellationTokenSource operation)
    {
        var token = operation.Token;
        try
        {
            await WaitForStateAsync(ViewModel, () => !ViewModel.IsLoading, token);
            if (state is not null)
                foreach (var group in ViewModel.Groups.ToArray())
                {
                    if (!state.LoadedCounts.TryGetValue(group.LibraryId, out var count)) continue;
                    while (group.Cards.Items.Count < count && group.Cards.HasMore && !group.Cards.HasError && !group.Cards.HasMoreError)
                    {
                        token.ThrowIfCancellationRequested();
                        var before = group.Cards.Items.Count;
                        await group.Cards.LoadMoreAsync().WaitAsync(token);
                        if (!ViewModel.Groups.Contains(group)) break;
                        await WaitForStateAsync(group.Cards, () => !group.Cards.IsLoadingMore, token);
                        await Task.Yield();
                        if (group.Cards.Items.Count <= before) break;
                    }
                }
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            scrollRestore = ScrollState.Restore(Scroller, offset);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(restoration, operation)) { restoration = null; operation.Dispose(); }
        }
    }

    private static async Task WaitForStateAsync(INotifyPropertyChanged source, Func<bool> ready, CancellationToken token)
    {
        if (ready()) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs args) { if (ready()) completion.TrySetResult(); }
        source.PropertyChanged += OnChanged;
        try { OnChanged(null, new PropertyChangedEventArgs(null)); await completion.Task.WaitAsync(token); }
        finally { source.PropertyChanged -= OnChanged; }
    }

    private void CancelRestore()
    {
        restoration?.Cancel(); restoration?.Dispose(); restoration = null;
        scrollRestore?.Dispose(); scrollRestore = null;
    }

    private void OnUserScroll(object sender, PointerRoutedEventArgs args) => CancelRestore();
    private sealed record SearchViewState(Dictionary<string, int> LoadedCounts);

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshAsync();

    private void OnGroupRetryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchGroupViewModel group) _ = group.Cards.RefreshAsync();
    }

    private void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchGroupViewModel group) _ = group.Cards.LoadMoreAsync();
    }
}
