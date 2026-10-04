using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

public sealed partial class RecentPage : UserControl, INavigablePage, ITransitionReadyPage, IDisposable
{
    private readonly GridLoader loader;
    private bool disposed;

    public RecentPage(RecentViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        loader = new GridLoader(Scroller, () => ViewModel.Cards);
        for (var i = 0; i < 8; i++)
        {
            Skeleton.Children.Add(CardSkeleton.Create(landscape: true));
            MoreSkeleton.Children.Add(CardSkeleton.Create(landscape: true));
        }
        Unloaded += OnUnloaded;
    }

    public RecentViewModel ViewModel { get; }

    Task ITransitionReadyPage.WaitForPresentationAsync(CancellationToken cancellationToken) =>
        loader.WaitForPresentationAsync(cancellationToken);

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
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Unloaded -= OnUnloaded;
        loader.Dispose();
        ViewModel.Dispose();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => loader.SetActive(false);

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = ViewModel.Cards.RefreshAsync();
    private void OnLoadMoreClick(object sender, RoutedEventArgs e) => _ = ViewModel.Cards.LoadMoreAsync();
    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => loader.Check();
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e) => loader.Check();
}
