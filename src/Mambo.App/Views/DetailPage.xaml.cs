using System.ComponentModel;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.App.Views.Controls;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Views;

public sealed partial class DetailPage : UserControl, INavigablePage, IMotionParticipant, IDisposable
{
    // 进度环：直径 67、线宽 2.6。虚线长度以线宽为单位，一整圈是 π × (67 − 2.6) ÷ 2.6。
    private const double RingDashUnits = Math.PI * (67 - 2.6) / 2.6;
    private static readonly ScrollingScrollOptions Instant = new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);
    private static readonly ScrollingScrollOptions Animated = new(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore);
    private readonly WindowContext window;
    private readonly BrowseTransitionCoordinator transitions;
    private WindowMotionObserver? motionObserver;
    private WindowContrastObserver? contrastObserver;
    private XamlRoot? observedRoot;
    private ScrollViewer? peopleScroller;
    private readonly RailScroller episodeRail;
    private RailScroller? peopleRail;
    private bool peopleAttached;
    private bool active;
    private NavEntry? owner;
    private bool disposed;
    private DetailViewState? pendingRestore;
    private IDisposable? scrollRestore;
    private CancellationTokenSource? backdropLoading;
    private CancellationTokenSource? episodePositioning;
    private CompositionScopedBatch? contentBatch;
    private int backdropGeneration;
    private int contentGeneration;
    private int selectionGeneration;
    private bool backdropRequested;
    private ImageRef? requestedBackdrop;
    private int requestedDecodeWidth;
    private bool contentPresented;
    private bool animateEpisodePosition;
    private string? positionedSeason;
    private string? positionedEpisode;
    private bool highContrast;
    private bool logoReady;

    public DetailPage(DetailViewModel viewModel, WindowContext window, BrowseTransitionCoordinator transitions)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(transitions);
        this.window = window;
        this.transitions = transitions;
        ViewModel = viewModel;
        InitializeComponent();
        contentPresented = ViewModel.HasContent;
        episodeRail = new RailScroller(EpisodeScroller, () => EpisodeScroller.HorizontalOffset, () => EpisodeScroller.ScrollableWidth, () => EpisodeScroller.ViewportWidth,
            (offset, animate) => EpisodeScroller.ScrollTo(offset, 0, animate && Motion.AnimationsEnabled ? Animated : Instant))
        {
            Pitch = (double)Application.Current.Resources["LandscapeWidth"] + (double)Application.Current.Resources["RailSpacing"],
            IsWheelEnabled = false,
        };
        // 原版的文字阴影：标题 0 2px 10px .65，评分信息 0 1px 3px .65，其余 0 1px 4px .6。
        SoftShadow.AttachDrop(TitleShadow, Heading, 10, 2, 0.65f);
        SoftShadow.AttachDrop(MetaShadow, MetaRow, 3, 1, 0.65f);
        SoftShadow.AttachDrop(EpisodeShadow, EpisodeText, 4, 1, 0.6f);
        SoftShadow.AttachDrop(OverviewShadow, OverviewText, 4, 1, 0.6f);
        SoftShadow.AttachDrop(LabelShadow, LabelBlock, 4, 1, 0.6f);
        for (var i = 0; i < 4; i++) EpisodeSkeleton.Children.Add(CardSkeleton.Create(landscape: true));
        ViewModel.TargetEpisodeAvailable += OnTargetEpisodeAvailable;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ApplyMeta();
        ApplyEpisodeTrack();
        ApplyProgress();
    }

    public DetailViewModel ViewModel { get; }

    /// <summary>圆钮里的进度环当前是否可见，以及它画出的比例。</summary>
    internal bool PlayProgressVisible => PlayProgress.Visibility == Visibility.Visible && PlayProgress.IsLoaded && PlayProgress.ActualWidth > 0;
    internal double ShownPlayProgress { get; private set; }
    internal RailScroller EpisodeRailInteraction => episodeRail;
    internal RailScroller? PeopleRailInteraction => peopleRail;

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        active = true;
        owner = entry;
        animateEpisodePosition = false;
        if (!created || entry.ViewState is DetailViewState) contentPresented = true;
        CancelBackdrop();
        if (created && entry.ViewState is DetailViewState saved)
        {
            pendingRestore = saved;
            RestoreSelection();
            scrollRestore?.Dispose();
            scrollRestore = ScrollState.Restore(Scroller, entry.VerticalOffset);
        }
        if (IsLoaded)
        {
            episodeRail.Attach();
            UpdateBackdropGeometry();
            _ = LoadBackdropAsync();
            RequestEpisodePosition();
            AttachPeopleScroller();
            ApplyStarting();
        }
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.VerticalOffset = Scroller.VerticalOffset;
        entry.ViewState = new DetailViewState(ViewModel.SelectedSeasonId, ViewModel.SelectedEpisodeId);
        active = false;
        owner = null;
        scrollRestore?.Dispose();
        scrollRestore = null;
        CancelBackdrop();
        CancelEpisodePosition();
        SettleMotion();
        DetachScrollers();
    }

    public void Refresh() => _ = ViewModel.RefreshAsync();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        active = false;
        owner = null;
        SettleMotion();
        CancelBackdrop();
        CancelEpisodePosition();
        scrollRestore?.Dispose();
        scrollRestore = null;
        contrastObserver?.Dispose();
        contrastObserver = null;
        motionObserver?.Dispose();
        motionObserver = null;
        window.ActiveChanged -= OnWindowActiveChanged;
        ObserveRoot(null);
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        DetachScrollers();
        episodeRail.Dispose();
        peopleRail?.Dispose();
        ViewModel.TargetEpisodeAvailable -= OnTargetEpisodeAvailable;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed) return;
        if (active) episodeRail.Attach();
        contrastObserver ??= new WindowContrastObserver(window, DispatcherQueue, ApplyContrast);
        motionObserver ??= new WindowMotionObserver(window, DispatcherQueue, OnMotionChanged);
        window.ActiveChanged -= OnWindowActiveChanged;
        window.ActiveChanged += OnWindowActiveChanged;
        ObserveRoot(XamlRoot);
        ApplyContrast(contrastObserver.HighContrast);
        UpdateHeroHeight();
        RequestEpisodePosition();
        CheckLoadMore();
        UpdateEpisodeArrows();
        AttachPeopleScroller();
        ApplyStarting();
        _ = LoadBackdropAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachScrollers();
        SettleMotion();
        CancelBackdrop();
        CancelEpisodePosition();
        scrollRestore?.Dispose();
        scrollRestore = null;
        window.ActiveChanged -= OnWindowActiveChanged;
        ObserveRoot(null);
        motionObserver?.Dispose();
        motionObserver = null;
        contrastObserver?.Dispose();
        contrastObserver = null;
    }

    private void ApplyContrast(bool value)
    {
        if (disposed) return;
        var changed = highContrast != value;
        highContrast = value;
        if (highContrast)
        {
            CancelBackdrop();
            SettleMotion();
            logoReady = false;
            LogoArt.Source = null;
            LogoArt.Visibility = Visibility.Collapsed;
            Heading.Visibility = Visibility.Visible;
        }
        else
        {
            LogoArt.Source = ViewModel.Logo;
            LogoArt.Visibility = ViewModel.HasLogo ? Visibility.Visible : Visibility.Collapsed;
            Heading.Visibility = logoReady && ViewModel.HasLogo ? Visibility.Collapsed : Visibility.Visible;
            if (changed) _ = LoadBackdropAsync();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeroHeight();

    private void ObserveRoot(XamlRoot? root)
    {
        if (ReferenceEquals(observedRoot, root)) return;
        if (observedRoot is not null) observedRoot.Changed -= OnRootChanged;
        observedRoot = root;
        if (observedRoot is not null) observedRoot.Changed += OnRootChanged;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateHeroHeight();

    private void UpdateHeroHeight()
    {
        Hero.Height = HeroArt.Height(XamlRoot?.Size.Height ?? ActualHeight, ActualWidth);
        LoadingSkeleton.Height = Hero.Height;
        UpdateBackdropGeometry();
        _ = LoadBackdropAsync();
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateBackdropGeometry();

    private void UpdateBackdropGeometry()
    {
        if (active && IsLoaded && owner is { } current)
            transitions.UpdateBackdropGeometry(current, Scroller.VerticalOffset, ActualWidth, Hero.Height + HeroArt.FadeExtent);
    }

    private void OnPeopleListLoaded(object sender, RoutedEventArgs e) => AttachPeopleScroller();

    private void AttachPeopleScroller()
    {
        if (disposed || !active || peopleAttached) return;
        peopleScroller ??= FindScroller(PeopleList);
        if (peopleScroller is not { } scroller) return;
        peopleAttached = true;
        scroller.ViewChanged += OnPeopleViewChanged;
        scroller.SizeChanged += OnPeopleScrollerSizeChanged;
        if (scroller.Content is FrameworkElement content) content.SizeChanged += OnPeopleScrollerSizeChanged;
        peopleRail ??= new RailScroller(scroller, () => scroller.HorizontalOffset, () => scroller.ScrollableWidth, () => scroller.ViewportWidth,
            (offset, animate) => scroller.ChangeView(offset, null, null, !animate || !Motion.AnimationsEnabled))
        {
            Pitch = (double)Application.Current.Resources["PersonCardWidth"] + 16,
        };
        peopleRail.Attach();
        UpdatePeopleArrows();
    }

    private void DetachScrollers()
    {
        episodeRail.Detach();
        peopleRail?.Detach();
        if (peopleAttached && peopleScroller is not null)
        {
            peopleScroller.ViewChanged -= OnPeopleViewChanged;
            peopleScroller.SizeChanged -= OnPeopleScrollerSizeChanged;
            if (peopleScroller.Content is FrameworkElement people) people.SizeChanged -= OnPeopleScrollerSizeChanged;
            peopleAttached = false;
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnEpisodeViewChanged(ScrollView sender, object args) { CheckLoadMore(); UpdateEpisodeArrows(); }
    private void OnEpisodeExtentChanged(ScrollView sender, object args) { CheckLoadMore(); UpdateEpisodeArrows(); }
    private void OnEpisodeScrollerSizeChanged(object sender, SizeChangedEventArgs e) { CheckLoadMore(); UpdateEpisodeArrows(); }
    private void OnPeopleViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdatePeopleArrows();
    private void OnPeopleScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePeopleArrows();
    private void UpdateEpisodeArrows() => episodeRail?.UpdateArrows(EpisodeArrows, EpisodesLeft, EpisodesRight);
    private void UpdatePeopleArrows() => peopleRail?.UpdateArrows(PeopleArrows, PeopleLeft, PeopleRight);

    private void CheckLoadMore()
    {
        if (!active || !IsLoaded || disposed || ViewModel.HasEpisodeMoreError) return;
        if (EpisodeScroller.ScrollableWidth - EpisodeScroller.HorizontalOffset <= EpisodeScroller.ViewportWidth * 1.5) _ = ViewModel.LoadMoreAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed) return;
        if (e.PropertyName == nameof(DetailViewModel.HasContent))
        {
            RestoreSelection();
            PresentContent();
            _ = LoadBackdropAsync();
            RequestEpisodePosition();
        }
        if (e.PropertyName == nameof(DetailViewModel.HasError)) _ = LoadBackdropAsync();
        if (e.PropertyName is nameof(DetailViewModel.IsEpisodesLoading) or nameof(DetailViewModel.IsSeriesLoading))
        {
            ApplyEpisodeTrack();
            var generation = selectionGeneration;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!disposed && active && generation == selectionGeneration) RequestEpisodePosition();
            });
        }
        if (e.PropertyName == nameof(DetailViewModel.SelectedSeasonId))
        {
            positionedSeason = null;
            positionedEpisode = null;
            CancelEpisodePosition();
        }
        if (e.PropertyName == nameof(DetailViewModel.Logo))
        {
            logoReady = false;
            // x:Bind 可能先触发已解码图片的同步 ImageOpened；重新设置来源让标题状态
            // 与本次来源对应，同时在高对比下取消该来源。
            LogoArt.Source = null;
            ApplyContrast(highContrast);
        }
        if (e.PropertyName == nameof(DetailViewModel.HasLogo) && highContrast) ApplyContrast(true);
        if (e.PropertyName == nameof(DetailViewModel.Backdrop)) _ = LoadBackdropAsync();
        if (e.PropertyName == nameof(DetailViewModel.Meta)) ApplyMeta();
        if (e.PropertyName == nameof(DetailViewModel.Overview)) ApplyOverview();
        if (e.PropertyName == nameof(DetailViewModel.ProgressFraction)) ApplyProgress();
        if (e.PropertyName == nameof(DetailViewModel.IsStarting)) ApplyStarting();
        if (e.PropertyName == nameof(DetailViewModel.IsLoadingMore) && !ViewModel.IsLoadingMore)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (disposed || !active) return;
                CheckLoadMore();
                UpdateEpisodeArrows();
            });
    }

    private void ApplyMeta()
    {
        var meta = ViewModel.Meta;
        MetaArea.Visibility = HeroArt.BuildMeta(MetaRow, meta.Rating, meta.Year, meta.Genres, meta.OfficialRating)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyOverview();
    }

    private void ApplyOverview() => OverviewArea.Visibility = ViewModel.Overview.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyProgress()
    {
        ShownPlayProgress = ViewModel.ProgressFraction;
        PlayProgressArc.StrokeDashArray = [Math.Max(ShownPlayProgress, 0) * RingDashUnits, 1000];
    }

    /// <summary>正在启动播放：圆钮轻轻呼吸，不换图标，也不加转圈。</summary>
    private void ApplyStarting()
    {
        var visual = ElementCompositionPreview.GetElementVisual(PlayButton);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        if (!active || !IsLoaded || disposed || !window.IsActive || !Motion.IsActive(this) ||
            Motion.IsEntranceSuppressed(PlayButton) || highContrast || !ViewModel.IsStarting ||
            !(motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled)) return;
        using var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, 1);
        pulse.InsertKeyFrame(0.5f, 0.55f);
        pulse.InsertKeyFrame(1, 1);
        pulse.Duration = TimeSpan.FromSeconds(1.6);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", pulse);
    }

    private void RestoreSelection()
    {
        if (!ViewModel.HasContent || pendingRestore is not { } saved) return;
        pendingRestore = null;
        ViewModel.RestoreSelection(saved.SeasonId, saved.EpisodeId);
    }

    private void OnTargetEpisodeAvailable(object? sender, EventArgs e) => RequestEpisodePosition();

    private void ApplyEpisodeTrack()
    {
        var loading = ViewModel.IsEpisodesLoading || (ViewModel.IsSeriesLoading && ViewModel.Episodes.Count == 0);
        EpisodeSkeleton.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        EpisodeScroller.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelEpisodePosition()
    {
        selectionGeneration++;
        episodePositioning?.Cancel();
        episodePositioning?.Dispose();
        episodePositioning = null;
    }

    private void RequestEpisodePosition()
    {
        if (!active || !IsLoaded || disposed || ViewModel.SelectedEpisodeId is not { } id) return;
        var season = ViewModel.SelectedSeasonId;
        if (positionedSeason == season && positionedEpisode == id) return;
        CancelEpisodePosition();
        var operation = new CancellationTokenSource();
        episodePositioning = operation;
        _ = PositionEpisodeAsync(season, id, selectionGeneration, operation);
    }

    private async Task PositionEpisodeAsync(string? season, string id, int generation, CancellationTokenSource operation)
    {
        var token = operation.Token;
        try
        {
            await GridLoader.NextRenderAsync(token);
            token.ThrowIfCancellationRequested();
            if (!active || !IsLoaded || disposed || generation != selectionGeneration ||
                ViewModel.SelectedSeasonId != season || ViewModel.SelectedEpisodeId != id) return;
            for (var index = 0; index < ViewModel.Episodes.Count; index++)
            {
                if (ViewModel.Episodes[index].Id != id) continue;
                var left = EpisodeRepeater.Margin.Left + index * episodeRail.Pitch;
                var right = left + (double)Application.Current.Resources["LandscapeWidth"];
                var offset = EpisodeScroller.HorizontalOffset;
                if (left < offset) offset = left;
                else if (right > offset + EpisodeScroller.ViewportWidth) offset = right - EpisodeScroller.ViewportWidth;
                var animate = animateEpisodePosition && (motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled);
                EpisodeScroller.ScrollTo(Math.Clamp(offset, 0, EpisodeScroller.ScrollableWidth), 0, animate ? Animated : Instant);
                positionedSeason = season;
                positionedEpisode = id;
                break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (active && !disposed && generation == selectionGeneration) transitions.ReportFailure(error);
        }
        finally
        {
            if (ReferenceEquals(episodePositioning, operation))
            {
                episodePositioning = null;
                operation.Dispose();
            }
        }
    }

    private void CancelBackdrop()
    {
        backdropGeneration++;
        backdropRequested = false;
        backdropLoading?.Cancel();
        backdropLoading?.Dispose();
        backdropLoading = null;
    }

    private async Task LoadBackdropAsync()
    {
        if (!active || !IsLoaded || disposed || highContrast || XamlRoot is null || owner is not { } current ||
            (!ViewModel.HasContent && !ViewModel.HasError)) return;
        var image = ViewModel.HasContent ? ViewModel.Backdrop as ImageRef : null;
        var width = (int)Math.Ceiling(Math.Max(ActualWidth, 960) * XamlRoot.RasterizationScale);
        if (backdropRequested && Equals(requestedBackdrop, image) && requestedDecodeWidth >= width) return;
        CancelBackdrop();
        backdropRequested = true;
        requestedBackdrop = image;
        requestedDecodeWidth = width;
        var generation = backdropGeneration;
        var operation = new CancellationTokenSource();
        backdropLoading = operation;
        var token = operation.Token;
        PreparedBackdrop? prepared = null;
        try
        {
            prepared = await transitions.PrepareBackdropAsync(current, image, width, token);
            token.ThrowIfCancellationRequested();
            if (prepared is null || disposed || !active || generation != backdropGeneration || !ReferenceEquals(owner, current)) return;
            UpdateBackdropGeometry();
            var ready = prepared;
            prepared = null; // Commit consumes the prepared result, including a rejected owner.
            await transitions.CommitBackdropAsync(current, ready, motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (active && !disposed && generation == backdropGeneration && ReferenceEquals(owner, current))
                transitions.ReportFailure(error);
        }
        finally
        {
            prepared?.Dispose();
            if (ReferenceEquals(backdropLoading, operation))
            {
                backdropLoading = null;
                operation.Dispose();
            }
        }
    }

    private void PresentContent()
    {
        if (!ViewModel.HasContent || contentPresented) return;
        contentPresented = true;
        SettleContent();
        if (!active || !IsLoaded || disposed || highContrast || !window.IsActive || !Motion.IsActive(this) ||
            Motion.IsEntranceSuppressed(this) || !(motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled)) return;
        Motion.SetEntranceSuppressed(Scroller, true);
        var visual = ElementCompositionPreview.GetElementVisual(Scroller);
        visual.Opacity = 0;
        using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        using var easing = Motion.CreateEasing(visual.Compositor, Motion.EaseOut);
        fade.Duration = Motion.Feedback;
        fade.InsertKeyFrame(1, 1, easing);
        var generation = contentGeneration;
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        contentBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (generation != contentGeneration || !ReferenceEquals(contentBatch, batch)) return;
            SettleContent();
            ApplyStarting();
        };
        visual.StartAnimation("Opacity", fade);
        batch.End();
    }

    private void SettleContent()
    {
        contentGeneration++;
        contentBatch?.Dispose();
        contentBatch = null;
        var visual = ElementCompositionPreview.GetElementVisual(Scroller);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        Motion.SetEntranceSuppressed(Scroller, false);
    }

    void IMotionParticipant.SettleMotion() => SettleMotion();

    private void SettleMotion()
    {
        StopRailScrolling();
        SettleContent();
        var play = ElementCompositionPreview.GetElementVisual(PlayButton);
        play.StopAnimation("Opacity");
        play.Opacity = 1;
    }

    private void OnMotionChanged(bool enabled)
    {
        if (enabled) return;
        SettleMotion();
    }

    private void OnWindowActiveChanged(object? sender, EventArgs e)
    {
        if (window.IsActive) ApplyStarting();
        else SettleMotion();
    }

    private void StopRailScrolling()
    {
        episodeRail.Stop();
        peopleRail?.Stop();
    }

    private void OnLogoOpened(object? sender, EventArgs e)
    {
        logoReady = true;
        Heading.Visibility = highContrast ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e) => await ViewModel.PlayAsync();
    private async void OnReplayClick(object sender, RoutedEventArgs e) => await ViewModel.PlayAsync(fromBeginning: true);
    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshAsync();
    private void OnLoadMoreClick(object sender, RoutedEventArgs e) => _ = ViewModel.LoadMoreAsync();

    private void OnSeasonClick(object sender, RoutedEventArgs e)
    {
        if (!active || sender is not FrameworkElement { Tag: string id }) return;
        if (sender is ToggleButton seasonButton) seasonButton.IsChecked = true;
        if (ViewModel.SelectedSeasonId == id) return;
        CancelEpisodePosition();
        animateEpisodePosition = true;
        ViewModel.SelectSeason(id);
        EpisodeScroller.ScrollTo(0, 0, Instant);
        RequestEpisodePosition();
    }

    private void OnEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (!active || sender is not EpisodeCard { Episode: { } episode } || ViewModel.SelectedEpisodeId == episode.Id) return;
        CancelEpisodePosition();
        animateEpisodePosition = true;
        ViewModel.SelectEpisode(episode.Id);
        RequestEpisodePosition();
    }

    private async void OnEpisodePlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is EpisodeCard { Episode: { } episode }) await ViewModel.PlayEpisodeAsync(episode.Id);
    }

    private void OnEpisodesLeftClick(object sender, RoutedEventArgs e) => episodeRail?.Page(-1);
    private void OnEpisodesRightClick(object sender, RoutedEventArgs e) => episodeRail?.Page(1);
    private void OnPeopleLeftClick(object sender, RoutedEventArgs e) => peopleRail?.Page(-1);
    private void OnPeopleRightClick(object sender, RoutedEventArgs e) => peopleRail?.Page(1);

    private sealed record DetailViewState(string? SeasonId, string? EpisodeId);
}
