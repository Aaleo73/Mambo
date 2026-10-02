using System.ComponentModel;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mambo.App.Views;

public sealed partial class LibraryPage : UserControl, INavigablePage, IDisposable
{
    private readonly GridLoader loader;

    public LibraryPage(LibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        loader = new GridLoader(Scroller, Grid, () => ViewModel.Cards);
        foreach (var option in viewModel.Sorts)
        {
            var item = new RadioMenuFlyoutItem { Text = option.Label, GroupName = "Sort", IsChecked = option.IsSelected, Tag = option };
            item.Click += OnSortClick;
            SortMenu.Items.Add(item);
        }
        for (var i = 0; i < 18; i++)
            Skeleton.Children.Add(new Border { Style = (Style)Application.Current.Resources["SkeletonBlockStyle"], Width = 150, Height = 265 });
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ResultsReplaced += OnResultsReplaced;
    }

    public LibraryViewModel ViewModel { get; }

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

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.ResultsReplaced -= OnResultsReplaced;
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.FiltersOpen))
            FilterButton.Style = (Style)Application.Current.Resources[ViewModel.FiltersOpen ? "SoftAccentButtonStyle" : "SoftButtonStyle"];
        if (e.PropertyName == nameof(LibraryViewModel.SortLabel))
            foreach (var item in SortMenu.Items.OfType<RadioMenuFlyoutItem>())
                item.IsChecked = item.Tag is SortOptionViewModel { IsSelected: true };
    }

    private void OnResultsReplaced(object? sender, EventArgs e)
    {
        Scroller.ChangeView(null, 0, null, true);
        loader.Reveal();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e) => ViewModel.FiltersOpen = !ViewModel.FiltersOpen;

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SortOptionViewModel option) ViewModel.SetSort(option);
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: FilterChipViewModel chip } toggle) return;
        ViewModel.Toggle(chip);
        toggle.IsChecked = chip.IsChecked;
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.ResetFilters();
    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = ViewModel.Cards.RefreshAsync();
    private void OnLoadMoreClick(object sender, RoutedEventArgs e) => _ = ViewModel.LoadMoreAsync();
    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => loader.Check();
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e) => loader.Check();
    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) => loader.Prepare(args);
}
