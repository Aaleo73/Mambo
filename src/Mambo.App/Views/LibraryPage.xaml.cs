using System.ComponentModel;
using System.Numerics;
using System.Runtime.ExceptionServices;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.App.Views.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Mambo.App.Views;

public sealed partial class LibraryPage : UserControl, INavigablePage, ITransitionReadyPage, IMotionParticipant, IDisposable
{
    private readonly GridLoader loader;
    private readonly WindowContext window;
    private readonly Dictionary<UIElement, CompositionScopedBatch> animations = [];
    private readonly PopupTransition sortTransition;
    private readonly PageInputScope resultsInput;
    private readonly PageInputScope filterInput;
    private readonly PageInputScope sortInput;
    private readonly Visual filterVisual;
    private readonly Visual scrollerVisual;
    private readonly CompositionPropertySet filterMotion;
    private readonly InsetClip filterClip;
    private readonly InsetClip resultsClip;
    private readonly PointerEventHandler sortRootPointerHandler;
    private readonly KeyEventHandler sortKeyHandler;
    private CompositionScopedBatch? filterBatch;
    private TaskCompletionSource? filterCompletion;
    private Task pendingSortTransition = Task.CompletedTask;
    private UIElement? sortEventRoot;
    private long filterGeneration;
    private long filterBatchGeneration;
    private long sortGeneration;
    private float expandedOccupancy;
    private float committedOccupancy;
    private double filterScrollOffset;
    private bool filterTargetOpen;
    private bool settlingFilter;
    private bool sortOpen;
    private WindowMotionObserver? motionObserver;
    private PagedCards observedCards;
    private ResultBranch displayedBranch;
    private bool branchQueued;
    private bool active;
    private bool disposed;

    public LibraryPage(LibraryViewModel viewModel, WindowContext window)
    {
        ArgumentNullException.ThrowIfNull(window);
        this.window = window;
        ViewModel = viewModel;
        InitializeComponent();
        sortTransition = new PopupTransition(SortSurface);
        resultsInput = new PageInputScope(Scroller, ExceptionDispatchInfo.Throw);
        filterInput = new PageInputScope(FilterPanel, ExceptionDispatchInfo.Throw);
        sortInput = new PageInputScope(SortSurface, ExceptionDispatchInfo.Throw);
        filterInput.SetEnabled(false);
        sortInput.SetEnabled(false);
        ElementCompositionPreview.SetIsTranslationEnabled(Scroller, true);
        filterVisual = ElementCompositionPreview.GetElementVisual(FilterPanel);
        scrollerVisual = ElementCompositionPreview.GetElementVisual(Scroller);
        scrollerVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        filterVisual.Opacity = 0;
        filterMotion = filterVisual.Compositor.CreatePropertySet();
        filterMotion.InsertScalar("Occupancy", 0);
        filterClip = filterVisual.Compositor.CreateInsetClip();
        resultsClip = filterVisual.Compositor.CreateInsetClip();
        sortRootPointerHandler = OnSortRootPointerPressed;
        sortKeyHandler = OnSortKeyDown;
        SortSurface.AddHandler(KeyDownEvent, sortKeyHandler, true);
        window.ActiveChanged += OnWindowActiveChanged;
        loader = new GridLoader(Scroller, () => ViewModel.Cards);
        observedCards = ViewModel.Cards;
        observedCards.PropertyChanged += OnCardsChanged;
        displayedBranch = GetBranch();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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

    internal Task PendingFilterTransition => filterCompletion?.Task ?? Task.CompletedTask;
    internal Task PendingSortTransition => pendingSortTransition;
    internal bool IsFilterTransitioning => filterCompletion is not null;
    internal bool IsSortOpen => sortOpen;
    internal bool IsSortPresented => SortPopup.IsOpen;
    internal bool GlobalSortHandlersAttached => sortEventRoot is not null;

    private bool CanAnimate => active && IsLoaded && window.IsActive && Motion.IsActive(this) &&
        !Motion.IsEntranceSuppressed(this) && (motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled);

    Task ITransitionReadyPage.WaitForPresentationAsync(CancellationToken cancellationToken) =>
        loader.WaitForPresentationAsync(cancellationToken);

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        active = true;
        SettleFilter(remeasure: true);
        displayedBranch = GetBranch();
        loader.SetActive(true);
        if (created) _ = loader.RestoreAsync(entry.VerticalOffset);
        else DispatcherQueue.TryEnqueue(loader.Check);
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        active = false;
        SettleMotion();
        RequestSortOpen(false, animate: false, restoreFocus: false);
        loader.SetActive(false);
        entry.VerticalOffset = Scroller.VerticalOffset;
    }

    public void Refresh() => _ = ViewModel.RefreshAsync();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        active = false;
        RequestSortOpen(false, animate: false, restoreFocus: false);
        SettleMotion();
        sortTransition.Dispose();
        resultsInput.Dispose();
        filterInput.Dispose();
        sortInput.Dispose();
        filterClip.Dispose();
        resultsClip.Dispose();
        filterMotion.Dispose();
        SortSurface.RemoveHandler(KeyDownEvent, sortKeyHandler);
        ClearSortOptions();
        window.ActiveChanged -= OnWindowActiveChanged;
        motionObserver?.Dispose();
        motionObserver = null;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        BodyHost.SizeChanged -= OnBodySizeChanged;
        FilterPanel.SizeChanged -= OnFilterSizeChanged;
        SortPopup.Opened -= OnSortPopupOpened;
        SortPopup.Closed -= OnSortPopupClosed;
        FilterButton.Click -= OnFilterClick;
        SortButton.Click -= OnSortButtonClick;
        SortHeader.Click -= OnSortHeaderClick;
        observedCards.PropertyChanged -= OnCardsChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.ResultsReplaced -= OnResultsReplaced;
        loader.Dispose();
        ViewModel.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed) return;
        motionObserver ??= new WindowMotionObserver(window, DispatcherQueue, OnMotionChanged);
        loader.SetActive(active);
        SettleFilter(remeasure: true);
        if (!motionObserver.AnimationsEnabled) SettleMotion();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (disposed) return;
        loader.SetActive(false);
        RequestSortOpen(false, animate: false, restoreFocus: false);
        SettleMotion();
        motionObserver?.Dispose();
        motionObserver = null;
    }

    private void OnMotionChanged(bool enabled)
    {
        if (!disposed && !enabled) SettleMotion();
    }

    private void OnWindowActiveChanged(object? sender, EventArgs e)
    {
        if (!disposed && !window.IsActive) SettleMotion();
    }

    void IMotionParticipant.SettleMotion()
    {
        if (!disposed) SettleMotion();
    }

    private void SettleMotion()
    {
        while (animations.Count > 0) SettleElement(animations.Keys.First());
        SettleFilter();
        if (!active || !IsLoaded || !Motion.IsActive(this))
            RequestSortOpen(false, animate: false, restoreFocus: false);
        else
            SettleSort();
    }

    private void SettleElement(UIElement element)
    {
        if (animations.Remove(element, out var batch)) batch.Dispose();
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        if (visual.Properties.TryGetVector3("Translation", out _) == CompositionGetValueStatus.Succeeded)
            visual.Properties.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        Motion.SetEntranceSuppressed(element, false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryViewModel.FiltersOpen) or nameof(LibraryViewModel.HasFilters)) ApplyFilterState();
        if (e.PropertyName == nameof(LibraryViewModel.FiltersOpen)) ChangeFilterPresentation();
        if (e.PropertyName == nameof(LibraryViewModel.SortLabel)) ApplySortState();
        if (e.PropertyName == nameof(LibraryViewModel.Cards))
        {
            observedCards.PropertyChanged -= OnCardsChanged;
            observedCards = ViewModel.Cards;
            observedCards.PropertyChanged += OnCardsChanged;
            loader.NotifySourceChanged();
            QueueBranch();
        }
        if (e.PropertyName is nameof(LibraryViewModel.ShowFilteredEmpty) or nameof(LibraryViewModel.ShowEmpty)) QueueBranch();
    }

    private void OnCardsChanged(object? sender, PropertyChangedEventArgs e) => QueueBranch();

    private void QueueBranch()
    {
        if (disposed || branchQueued) return;
        branchQueued = true;
        if (!DispatcherQueue.TryEnqueue(ApplyBranch)) branchQueued = false;
    }

    private ResultBranch GetBranch() => ViewModel.Cards.Items.Count > 0 ? ResultBranch.Items
        : ViewModel.Cards.HasError ? ResultBranch.Error
        : ViewModel.Cards.IsLoadingFirst ? ResultBranch.Loading
        : ViewModel.ShowFilteredEmpty ? ResultBranch.FilteredEmpty : ResultBranch.Empty;

    private UIElement BranchElement(ResultBranch branch) => branch switch
    {
        ResultBranch.Items => Grid,
        ResultBranch.Loading => Skeleton,
        ResultBranch.Error => ErrorState,
        ResultBranch.FilteredEmpty => FilteredEmptyState,
        _ => EmptyState,
    };

    private void ApplyBranch()
    {
        branchQueued = false;
        if (disposed) return;
        var next = GetBranch();
        if (next == displayedBranch) return;
        SettleElement(BranchElement(displayedBranch));
        displayedBranch = next;
        FadeIn(BranchElement(next));
    }

    /// <summary>筛选按钮在面板打开或已有筛选条件时保持加深。</summary>
    private void ApplyFilterState() => FilterButton.IsChecked = ViewModel.FiltersOpen || ViewModel.HasFilters;

    /// <summary>弹出面里只列出当前排序以外的选项；当前排序就是它的第一行。</summary>
    private void ApplySortState()
    {
        AutomationProperties.SetName(SortButton, $"排序：{ViewModel.SortLabel}");
        AutomationProperties.SetName(SortHeader, $"排序：{ViewModel.SortLabel}");
        ClearSortOptions();
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
        filterScrollOffset = 0;
        Scroller.ChangeView(null, 0, null, true);
        QueueBranch();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.FiltersOpen = !ViewModel.FiltersOpen;
        // ToggleButton 点击后会自己翻转选中态，这里按实际状态改回来。
        ApplyFilterState();
    }

    private void ClearSortOptions()
    {
        foreach (var item in SortOptions.Children.OfType<Button>()) item.Click -= OnSortOptionClick;
        SortOptions.Children.Clear();
    }

    private void ChangeFilterPresentation()
    {
        if (disposed) return;
        var open = ViewModel.FiltersOpen;
        if (open == filterTargetOpen) return;
        if (!CanAnimate)
        {
            SettleFilter(remeasure: open);
            return;
        }

        var reversing = IsFilterTransitioning;
        if (!reversing) filterScrollOffset = Scroller.VerticalOffset;
        if (!reversing && open) expandedOccupancy = MeasureFilterOccupancy();
        if (expandedOccupancy <= 0)
        {
            SettleFilter(remeasure: open);
            return;
        }

        if (resultsInput.CaptureFocus() || !open && filterInput.CaptureFocus())
            FilterButton.Focus(FocusState.Programmatic);
        resultsInput.SetEnabled(false);
        filterInput.SetEnabled(open);
        Motion.SetEntranceSuppressed(Scroller, true);
        filterTargetOpen = open;
        FilterPanel.Visibility = Visibility.Visible;

        var superseded = filterCompletion;
        filterGeneration++;
        ReleaseFilterBatch();
        filterCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        superseded?.TrySetResult();
        try
        {
            if (!reversing)
            {
                // Both directions use the full viewport, so revealing the bottom never exposes a gap.
                filterMotion.InsertScalar("Occupancy", committedOccupancy);
                StartFilterGeometry();
                Scroller.Margin = new Thickness(0, 4, 0, 0);
            }

            var compositor = filterVisual.Compositor;
            filterBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            filterBatchGeneration = filterGeneration;
            filterBatch.Completed += OnFilterCompleted;
            using var occupancyEase = Motion.CreateEasing(compositor, Motion.EaseOut);
            using var occupancy = compositor.CreateScalarKeyFrameAnimation();
            occupancy.Duration = Motion.Content;
            occupancy.InsertKeyFrame(1, open ? expandedOccupancy : 0, occupancyEase);
            using var opacityEase = Motion.CreateEasing(compositor, open ? Motion.EaseOut : Motion.EaseIn);
            using var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Duration = open ? Motion.Feedback : Motion.Exit;
            opacity.InsertKeyFrame(1, open ? 1 : 0, opacityEase);
            // Target-only animations retain the compositor's current value when direction changes.
            filterMotion.StartAnimation("Occupancy", occupancy);
            filterVisual.StartAnimation("Opacity", opacity);
            filterBatch.End();
        }
        catch
        {
            SettleFilter();
            throw;
        }
    }

    private float MeasureFilterOccupancy()
    {
        FilterPanel.Visibility = Visibility.Visible;
        if (BodyHost.ActualWidth <= 0) return expandedOccupancy;
        FilterPanel.Measure(new Size(BodyHost.ActualWidth, double.PositiveInfinity));
        var margin = FilterPanel.Margin.Top + FilterPanel.Margin.Bottom;
        var maximum = 180 + FilterPanel.BorderThickness.Top + FilterPanel.BorderThickness.Bottom + margin;
        return (float)Math.Clamp(FilterPanel.DesiredSize.Height, 0, maximum);
    }

    private void StartFilterGeometry()
    {
        var compositor = filterVisual.Compositor;
        using var translation = compositor.CreateExpressionAnimation("Vector3(0, state.Occupancy, 0)");
        translation.SetReferenceParameter("state", filterMotion);
        scrollerVisual.Properties.StartAnimation("Translation", translation);
        using var bottom = compositor.CreateExpressionAnimation("state.Occupancy");
        bottom.SetReferenceParameter("state", filterMotion);
        resultsClip.StartAnimation("BottomInset", bottom);
        scrollerVisual.Clip = resultsClip;
        using var reveal = compositor.CreateExpressionAnimation("Max(0, extent * (1 - state.Occupancy / expanded))");
        reveal.SetReferenceParameter("state", filterMotion);
        reveal.SetScalarParameter("expanded", expandedOccupancy);
        reveal.SetScalarParameter("extent", Math.Max(0, expandedOccupancy - (float)(FilterPanel.Margin.Top + FilterPanel.Margin.Bottom)));
        filterClip.StartAnimation("BottomInset", reveal);
        filterVisual.Clip = filterClip;
    }

    private void OnFilterCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, filterBatch) || filterBatchGeneration != filterGeneration) return;
        SettleFilter();
    }

    private void ReleaseFilterBatch()
    {
        if (filterBatch is null) return;
        filterBatch.Completed -= OnFilterCompleted;
        filterBatch.Dispose();
        filterBatch = null;
    }

    private void SettleFilter(bool remeasure = false)
    {
        if (settlingFilter || filterMotion is null) return;
        settlingFilter = true;
        try
        {
            filterGeneration++;
            ReleaseFilterBatch();
            filterTargetOpen = ViewModel.FiltersOpen;
            if (filterTargetOpen && remeasure) expandedOccupancy = MeasureFilterOccupancy();
            committedOccupancy = filterTargetOpen ? expandedOccupancy : 0;
            filterMotion.StopAnimation("Occupancy");
            filterMotion.InsertScalar("Occupancy", committedOccupancy);
            filterVisual.StopAnimation("Opacity");
            filterVisual.Opacity = filterTargetOpen ? 1 : 0;
            scrollerVisual.Properties.StopAnimation("Translation");
            scrollerVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            resultsClip.StopAnimation("BottomInset");
            filterClip.StopAnimation("BottomInset");
            scrollerVisual.Clip = null;
            filterVisual.Clip = null;
            var top = 4 + committedOccupancy;
            var layoutChanged = Scroller.Margin.Top != top;
            var wasTransitioning = filterCompletion is not null;
            var offset = wasTransitioning ? filterScrollOffset : Scroller.VerticalOffset;
            Scroller.Margin = new Thickness(0, top, 0, 0);
            FilterPanel.Visibility = filterTargetOpen ? Visibility.Visible : Visibility.Collapsed;
            filterInput.SetEnabled(filterTargetOpen);
            resultsInput.SetEnabled(true);
            Motion.SetEntranceSuppressed(Scroller, false);
            if ((layoutChanged || wasTransitioning) && IsLoaded)
            {
                BodyHost.UpdateLayout();
                var clamped = Math.Clamp(offset, 0, Scroller.ScrollableHeight);
                if (Math.Abs(Scroller.VerticalOffset - clamped) > 0.01)
                    Scroller.ChangeView(null, clamped, null, true);
            }
            var completed = filterCompletion;
            filterCompletion = null;
            completed?.TrySetResult();
        }
        finally { settlingFilter = false; }
    }

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!disposed) SettleFilter(remeasure: true);
    }

    private void OnFilterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (disposed || settlingFilter || !ViewModel.FiltersOpen) return;
        var occupancy = e.NewSize.Height + FilterPanel.Margin.Top + FilterPanel.Margin.Bottom;
        if (Math.Abs(occupancy - expandedOccupancy) > 0.5) SettleFilter(remeasure: true);
    }

    internal void RequestSortOpen(bool open) => RequestSortOpen(open, animate: CanAnimate, restoreFocus: true);

    private void RequestSortOpen(bool open, bool animate, bool restoreFocus)
    {
        if (open && (disposed || !active || !IsLoaded || !Motion.IsActive(this))) return;
        if (sortOpen == open)
        {
            if (!animate) SettleSort();
            return;
        }

        var version = ++sortGeneration;
        sortOpen = open;
        var focusInPopup = SortSurface.XamlRoot is { } root &&
            FocusManager.GetFocusedElement(root) is DependencyObject focused && PageInputScope.Contains(SortSurface, focused);
        SortButton.Opacity = open ? 0 : 1;
        SortButton.IsHitTestVisible = !open;
        SortButton.IsTabStop = !open;
        sortInput.SetEnabled(open);
        try
        {
            if (open)
            {
                AttachSortHandlers();
                var wasPresented = SortPopup.IsOpen;
                var transition = sortTransition.OpenAsync(animate);
                SortPopup.IsOpen = true;
                if (wasPresented) FocusSortOption();
                pendingSortTransition = CompleteSortTransitionAsync(transition, version);
            }
            else
            {
                DetachSortHandlers();
                if (restoreFocus && focusInPopup && active && !disposed && IsLoaded && Motion.IsActive(this))
                    SortButton.Focus(FocusState.Programmatic);
                pendingSortTransition = CompleteSortTransitionAsync(sortTransition.CloseAsync(animate), version);
            }
        }
        catch
        {
            SettleSort();
            throw;
        }
    }

    private async Task CompleteSortTransitionAsync(Task transition, long version)
    {
        await transition;
        if (version != sortGeneration || sortOpen) return;
        SortPopup.IsOpen = false;
    }

    private void SettleSort()
    {
        sortGeneration++;
        sortTransition.Settle(sortOpen);
        if (!sortOpen) DetachSortHandlers();
        SortPopup.IsOpen = sortOpen;
        pendingSortTransition = Task.CompletedTask;
    }

    private void AttachSortHandlers()
    {
        if (sortEventRoot is not null || XamlRoot?.Content is not UIElement root) return;
        sortEventRoot = root;
        root.AddHandler(PointerPressedEvent, sortRootPointerHandler, true);
        // Dismiss before the Shell's bubbling Escape navigation, even when focus is outside the popup.
        root.AddHandler(PreviewKeyDownEvent, sortKeyHandler, true);
        root.AddHandler(KeyDownEvent, sortKeyHandler, true);
    }

    private void DetachSortHandlers()
    {
        if (sortEventRoot is not { } root) return;
        sortEventRoot = null;
        root.RemoveHandler(PointerPressedEvent, sortRootPointerHandler);
        root.RemoveHandler(PreviewKeyDownEvent, sortKeyHandler);
        root.RemoveHandler(KeyDownEvent, sortKeyHandler);
    }

    private void OnSortRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!sortOpen || e.OriginalSource is not DependencyObject source ||
            PageInputScope.Contains(SortSurface, source) || PageInputScope.Contains(SortButton, source)) return;
        // Never handle an outside press: the same gesture may navigate or open another control.
        RequestSortOpen(false, animate: CanAnimate, restoreFocus: false);
    }

    private void OnSortKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!sortOpen || e.Key != VirtualKey.Escape) return;
        RequestSortOpen(false);
        e.Handled = true;
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e) => RequestSortOpen(true);
    private void OnSortHeaderClick(object sender, RoutedEventArgs e) => RequestSortOpen(false);

    private void OnSortPopupOpened(object? sender, object e)
    {
        if (SortPopup.IsOpen && sortOpen && active && !disposed) FocusSortOption();
    }

    private void FocusSortOption() =>
        (SortOptions.Children.FirstOrDefault() as Control ?? SortHeader).Focus(FocusState.Programmatic);

    private void OnSortPopupClosed(object? sender, object e)
    {
        // Popup events can arrive after a close has already been reversed.
        if (!SortPopup.IsOpen && sortOpen) RequestSortOpen(false, animate: false, restoreFocus: false);
    }

    private void OnSortOptionClick(object sender, RoutedEventArgs e)
    {
        if (!sortOpen || sender is not FrameworkElement { Tag: SortOptionViewModel option }) return;
        RequestSortOpen(false);
        ViewModel.SetSort(option);
    }


    /// <summary>结果仅在 loading/error/empty/items 分支改变时反馈；items→items 保留原网格。</summary>
    private void FadeIn(UIElement element)
    {
        SettleElement(element);
        if (disposed || !CanAnimate || Motion.IsEntranceSuppressed(element)) return;
        Motion.SetEntranceSuppressed(element, true);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        using var easing = Motion.CreateEasing(compositor, Motion.EaseOut);
        using var fade = compositor.CreateScalarKeyFrameAnimation();
        visual.Opacity = 0;
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = Motion.Feedback;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        animations.Add(element, batch);
        visual.StartAnimation("Opacity", fade);
        batch.Completed += (_, _) =>
        {
            if (animations.TryGetValue(element, out var current) && ReferenceEquals(current, batch)) SettleElement(element);
        };
        batch.End();
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

    private enum ResultBranch { Loading, Items, FilteredEmpty, Empty, Error }
}
