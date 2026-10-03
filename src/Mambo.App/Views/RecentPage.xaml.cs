using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

public sealed partial class RecentPage : UserControl, INavigablePage, IDisposable
{
    private readonly GridLoader loader;

    public RecentPage(RecentViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        loader = new GridLoader(Scroller, Grid, () => ViewModel.Cards);
        for (var i = 0; i < 8; i++)
        {
            Skeleton.Children.Add(CardSkeleton.Create(landscape: true));
            MoreSkeleton.Children.Add(CardSkeleton.Create(landscape: true));
        }
    }

    public RecentViewModel ViewModel { get; }

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        loader.SetActive(true);
        if (created) _ = loader.RestoreAsync(entry.VerticalOffset);
        else DispatcherQueue.TryEnqueue(loader.Check);
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        loader.SetActive(false);
        entry.VerticalOffset = Scroller.VerticalOffset;
    }

    public void Refresh() => _ = ViewModel.Cards.RefreshAsync();
    public void Dispose() { loader.Dispose(); ViewModel.Dispose(); }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = ViewModel.Cards.RefreshAsync();
    private void OnLoadMoreClick(object sender, RoutedEventArgs e) => _ = ViewModel.Cards.LoadMoreAsync();
    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => loader.Check();
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e) => loader.Check();
    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) => loader.Prepare(args);
}
