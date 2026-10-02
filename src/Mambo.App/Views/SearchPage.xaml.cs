using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

public sealed partial class SearchPage : UserControl, INavigablePage, IDisposable
{
    public SearchPage(SearchViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SearchViewModel ViewModel { get; }

    /// <summary>返回搜索页时恢复滚动位置（B5）。</summary>
    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (created) ScrollState.Restore(Scroller, entry.VerticalOffset);
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.VerticalOffset = Scroller.VerticalOffset;
    }

    public void Refresh() => _ = ViewModel.RefreshAsync();
    public void Dispose() => ViewModel.Dispose();

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
