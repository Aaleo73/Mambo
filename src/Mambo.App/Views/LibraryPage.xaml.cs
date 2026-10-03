using System.ComponentModel;
using System.Numerics;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.App.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Views;

public sealed partial class LibraryPage : UserControl, INavigablePage, IDisposable
{
    private readonly GridLoader loader;

    public LibraryPage(LibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        loader = new GridLoader(Scroller, Grid, () => ViewModel.Cards);
        for (var i = 0; i < 12; i++)
        {
            Skeleton.Children.Add(CardSkeleton.Create(landscape: false));
            MoreSkeleton.Children.Add(CardSkeleton.Create(landscape: false));
        }
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ResultsReplaced += OnResultsReplaced;
        ApplyFilterState();
        ApplySortState();
    }

    public LibraryViewModel ViewModel { get; }

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
        SortPopup.IsOpen = false;
        loader.SetActive(false);
        entry.VerticalOffset = Scroller.VerticalOffset;
    }

    public void Refresh() => _ = ViewModel.RefreshAsync();

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.ResultsReplaced -= OnResultsReplaced;
        loader.Dispose();
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryViewModel.FiltersOpen) or nameof(LibraryViewModel.HasFilters)) ApplyFilterState();
        if (e.PropertyName == nameof(LibraryViewModel.SortLabel)) ApplySortState();
    }

    /// <summary>筛选按钮在面板打开或已有筛选条件时保持加深。</summary>
    private void ApplyFilterState() => FilterButton.IsChecked = ViewModel.FiltersOpen || ViewModel.HasFilters;

    /// <summary>弹出面里只列出当前排序以外的选项；当前排序就是它的第一行。</summary>
    private void ApplySortState()
    {
        AutomationProperties.SetName(SortButton, $"排序：{ViewModel.SortLabel}");
        AutomationProperties.SetName(SortHeader, $"排序：{ViewModel.SortLabel}");
        SortOptions.Children.Clear();
        var style = XamlResources.Style(Application.Current.Resources, "PopupItemButtonStyle");
        foreach (var option in ViewModel.Sorts.Where(sort => !sort.IsSelected))
        {
            var item = new Button { Style = style, Content = option.Label, Tag = option };
            item.Click += OnSortOptionClick;
            SortOptions.Children.Add(item);
        }
    }

    private void OnResultsReplaced(object? sender, EventArgs e)
    {
        loader.CancelRestore();
        Scroller.ChangeView(null, 0, null, true);
        loader.Reveal();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.FiltersOpen = !ViewModel.FiltersOpen;
        // ToggleButton 点击后会自己翻转选中态，这里按实际状态改回来。
        ApplyFilterState();
        if (ViewModel.FiltersOpen) FadeIn(FilterPanel);
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e) => SortPopup.IsOpen = true;
    private void OnSortHeaderClick(object sender, RoutedEventArgs e) => SortPopup.IsOpen = false;

    private void OnSortPopupOpened(object? sender, object e)
    {
        SortButton.Opacity = 0;
        FadeIn(SortSurface);
        (SortOptions.Children.FirstOrDefault() as Control)?.Focus(FocusState.Programmatic);
    }

    private void OnSortPopupClosed(object? sender, object e)
    {
        SortButton.Opacity = 1;
        SortButton.Focus(FocusState.Programmatic);
    }

    private void OnSortOptionClick(object sender, RoutedEventArgs e)
    {
        SortPopup.IsOpen = false;
        if ((sender as FrameworkElement)?.Tag is SortOptionViewModel option) ViewModel.SetSort(option);
    }

    /// <summary>弹出面和筛选面板出现时从上方 4px 淡入（160ms）。</summary>
    private static void FadeIn(UIElement element)
    {
        if (!Motion.AnimationsEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easing = Motion.CreateEasing(compositor, Motion.Fluid);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = Motion.Fast;
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(0, -4, 0));
        move.InsertKeyFrame(1, Vector3.Zero, easing);
        move.Duration = Motion.Fast;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", move);
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
