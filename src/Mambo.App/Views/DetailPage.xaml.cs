using System.ComponentModel;
using System.Numerics;
using Mambo.App.Images;
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
using Windows.Foundation;

namespace Mambo.App.Views;

public sealed partial class DetailPage : UserControl, INavigablePage, IDisposable
{
    // 进度环：直径 67、线宽 2.6。虚线长度以线宽为单位，一整圈是 π × (67 − 2.6) ÷ 2.6。
    private const double RingDashUnits = Math.PI * (67 - 2.6) / 2.6;
    private static readonly ScrollingScrollOptions Instant = new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);
    private static readonly ScrollingScrollOptions Animated = new(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore);
    private readonly WindowContext window;
    private WindowContrastObserver? contrastObserver;
    private ScrollViewer? peopleScroller;
    private readonly RailScroller episodeRail;
    private RailScroller? peopleRail;
    private bool disposed;
    private DetailViewState? pendingRestore;
    private ContainerVisual? heroVisual;
    private SpriteVisual? imageVisual;
    private SpriteVisual? scrimVisual;
    private LoadedImageSurface? currentSurface;
    private CancellationTokenSource? artLoading;
    private bool highContrast;
    private bool logoReady;

    public DetailPage(DetailViewModel viewModel, WindowContext window)
    {
        ArgumentNullException.ThrowIfNull(window);
        this.window = window;
        ViewModel = viewModel;
        InitializeComponent();
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
        ApplyProgress();
    }

    public DetailViewModel ViewModel { get; }

    /// <summary>圆钮里的进度环当前是否可见，以及它画出的比例。</summary>
    internal bool PlayProgressVisible => PlayProgress.Visibility == Visibility.Visible && PlayProgress.IsLoaded && PlayProgress.ActualWidth > 0;
    internal double ShownPlayProgress { get; private set; }

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (created && entry.ViewState is DetailViewState saved)
        {
            pendingRestore = saved;
            RestoreSelection();
            ScrollState.Restore(Scroller, entry.VerticalOffset);
        }
        // 缓存页在高对比期间释放了背景图，重新显示时恢复普通主题的图片。
        if (IsLoaded && !highContrast && currentSurface is null) _ = LoadHeroArtAsync();
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.VerticalOffset = Scroller.VerticalOffset;
        entry.ViewState = new DetailViewState(ViewModel.SelectedSeasonId, ViewModel.SelectedEpisodeId);
    }

    public void Refresh() => _ = ViewModel.RefreshAsync();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        contrastObserver?.Dispose();
        contrastObserver = null;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        DetachScrollers();
        ViewModel.TargetEpisodeAvailable -= OnTargetEpisodeAvailable;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
        CancelArt();
        ElementCompositionPreview.SetElementChildVisual(ArtHost, null);
        currentSurface?.Dispose();
        heroVisual?.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed) return;
        contrastObserver ??= new WindowContrastObserver(window, DispatcherQueue, ApplyContrast);
        ApplyContrast(contrastObserver.HighContrast);
        UpdateHeroHeight();
        ScrollToTarget();
        CheckLoadMore();
        UpdateEpisodeArrows();
        AttachPeopleScroller();
        InitializeHeroArt();
        _ = LoadHeroArtAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachScrollers();
        CancelArt();
        contrastObserver?.Dispose();
        contrastObserver = null;
    }

    private void ApplyContrast(bool value)
    {
        if (disposed) return;
        var changed = highContrast != value;
        highContrast = value;
        ArtHost.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        if (highContrast)
        {
            CancelArt();
            ClearHeroSurface();
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
            if (changed) _ = LoadHeroArtAsync();
        }
    }

    private void ClearHeroSurface()
    {
        if (imageVisual?.Brush is CompositionMaskBrush masked)
        {
            var previous = masked.Source as CompositionSurfaceBrush;
            masked.Source = null;
            previous?.Dispose();
        }
        currentSurface?.Dispose();
        currentSurface = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeroHeight();

    private void UpdateHeroHeight()
    {
        Hero.Height = HeroArt.Height(XamlRoot?.Size.Height ?? ActualHeight, ActualWidth);
        LoadingSkeleton.Height = Hero.Height;
        var size = new Vector2((float)ActualWidth, (float)(Hero.Height + HeroArt.FadeExtent));
        if (imageVisual is not null)
        {
            imageVisual.Size = size;
            imageVisual.CenterPoint = new Vector3(size / 2, 0);
        }
        if (scrimVisual is not null) scrimVisual.Size = size;
    }

    private void OnPeopleListLoaded(object sender, RoutedEventArgs e) => AttachPeopleScroller();

    private void AttachPeopleScroller()
    {
        if (disposed || peopleScroller is not null) return;
        peopleScroller = FindScroller(PeopleList);
        if (peopleScroller is not { } scroller) return;
        scroller.ViewChanged += OnPeopleViewChanged;
        scroller.SizeChanged += OnPeopleScrollerSizeChanged;
        if (scroller.Content is FrameworkElement content) content.SizeChanged += OnPeopleScrollerSizeChanged;
        peopleRail = new RailScroller(scroller, () => scroller.HorizontalOffset, () => scroller.ScrollableWidth, () => scroller.ViewportWidth,
            (offset, animate) => scroller.ChangeView(offset, null, null, !animate || !Motion.AnimationsEnabled))
        {
            Pitch = (double)Application.Current.Resources["PersonCardWidth"] + 16,
        };
        UpdatePeopleArrows();
    }

    private void DetachScrollers()
    {
        if (peopleScroller is not null)
        {
            peopleScroller.ViewChanged -= OnPeopleViewChanged;
            peopleScroller.SizeChanged -= OnPeopleScrollerSizeChanged;
            if (peopleScroller.Content is FrameworkElement people) people.SizeChanged -= OnPeopleScrollerSizeChanged;
            peopleScroller = null;
            peopleRail = null;
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
        if (!IsLoaded || disposed || ViewModel.HasEpisodeMoreError) return;
        if (EpisodeScroller.ScrollableWidth - EpisodeScroller.HorizontalOffset <= EpisodeScroller.ViewportWidth * 1.5) _ = ViewModel.LoadMoreAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailViewModel.HasContent)) RestoreSelection();
        if (e.PropertyName == nameof(DetailViewModel.Logo))
        {
            logoReady = false;
            // x:Bind 可能先触发已解码图片的同步 ImageOpened；重新设置来源让标题状态
            // 与本次来源对应，同时在高对比下取消该来源。
            LogoArt.Source = null;
            ApplyContrast(highContrast);
        }
        if (e.PropertyName == nameof(DetailViewModel.HasLogo) && highContrast) ApplyContrast(true);
        if (e.PropertyName == nameof(DetailViewModel.Backdrop)) _ = LoadHeroArtAsync();
        if (e.PropertyName == nameof(DetailViewModel.Meta)) ApplyMeta();
        if (e.PropertyName == nameof(DetailViewModel.Overview)) ApplyOverview();
        if (e.PropertyName == nameof(DetailViewModel.ProgressFraction)) ApplyProgress();
        if (e.PropertyName == nameof(DetailViewModel.IsStarting)) ApplyStarting();
        if (e.PropertyName == nameof(DetailViewModel.IsLoadingMore) && !ViewModel.IsLoadingMore)
            DispatcherQueue.TryEnqueue(() => { CheckLoadMore(); UpdateEpisodeArrows(); });
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
        if (!ViewModel.IsStarting || !Motion.AnimationsEnabled) return;
        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
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

    private void OnTargetEpisodeAvailable(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ScrollToTarget);
    private void ScrollToTarget()
    {
        if (!IsLoaded || disposed || ViewModel.SelectedEpisodeId is not { } id) return;
        for (var index = 0; index < ViewModel.Episodes.Count; index++)
        {
            if (ViewModel.Episodes[index].Id != id) continue;
            EpisodeScroller.UpdateLayout();
            var left = EpisodeRepeater.Margin.Left + index * episodeRail.Pitch;
            var right = left + (double)Application.Current.Resources["LandscapeWidth"];
            var offset = EpisodeScroller.HorizontalOffset;
            if (left < offset) offset = left;
            else if (right > offset + EpisodeScroller.ViewportWidth) offset = right - EpisodeScroller.ViewportWidth;
            EpisodeScroller.ScrollTo(Math.Clamp(offset, 0, EpisodeScroller.ScrollableWidth), 0, Instant);
            break;
        }
    }

    private void InitializeHeroArt()
    {
        if (heroVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(ArtHost).Compositor;
        heroVisual = compositor.CreateContainerVisual();
        var mask = HeroArt.CreateEdgeFade(compositor);
        var imageMask = compositor.CreateMaskBrush();
        imageMask.Mask = mask;
        imageVisual = compositor.CreateSpriteVisual();
        imageVisual.Brush = imageMask;
        scrimVisual = HeroArt.CreateCopyScrim(compositor, mask);
        heroVisual.Children.InsertAtTop(imageVisual);
        heroVisual.Children.InsertAtTop(scrimVisual);
        ElementCompositionPreview.SetElementChildVisual(ArtHost, heroVisual);
        UpdateHeroHeight();
    }

    private void CancelArt()
    {
        artLoading?.Cancel();
        artLoading?.Dispose();
        artLoading = null;
    }

    private async Task LoadHeroArtAsync()
    {
        CancelArt();
        if (!IsLoaded || disposed || highContrast || Visibility != Visibility.Visible || imageVisual is null || XamlRoot is null || ImageLoader.Current is not { } loader) return;
        if (ViewModel.Backdrop is not ImageRef image)
        {
            ClearHeroSurface();
            return;
        }
        var cts = new CancellationTokenSource();
        artLoading = cts;
        var token = cts.Token;
        LoadedImageSurface? loaded = null;
        try
        {
            var width = (int)Math.Ceiling(Math.Max(ActualWidth, 960) * XamlRoot.RasterizationScale);
            using var stream = await loader.FetchStreamAsync(image, width, ImagePriority.Hero, token);
            if (stream is null) return;
            loaded = LoadedImageSurface.StartLoadFromStream(stream, new Size(width, width * 9 / 16.0));
            var completion = new TaskCompletionSource<bool>();
            void OnCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args) => completion.TrySetResult(args.Status == LoadedImageSourceLoadStatus.Success);
            loaded.LoadCompleted += OnCompleted;
            bool ready;
            try { ready = await completion.Task.WaitAsync(token); }
            finally { loaded.LoadCompleted -= OnCompleted; }
            token.ThrowIfCancellationRequested();
            if (!ready) return;
            var brush = imageVisual.Compositor.CreateSurfaceBrush(loaded);
            brush.Stretch = CompositionStretch.UniformToFill;
            var masked = (CompositionMaskBrush)imageVisual.Brush;
            var previous = masked.Source as CompositionSurfaceBrush;
            masked.Source = brush;
            previous?.Dispose();
            var first = currentSurface is null;
            currentSurface?.Dispose();
            currentSurface = loaded;
            loaded = null;
            if (first) SettleBackdrop();
        }
        catch (OperationCanceledException) { }
        finally { loaded?.Dispose(); }
    }

    /// <summary>背景图入场：一边淡入一边从 1.04 落回 1（500ms，settle 缓动）。</summary>
    private void SettleBackdrop()
    {
        if (imageVisual is null || !Motion.AnimationsEnabled) return;
        var compositor = imageVisual.Compositor;
        var easing = Motion.CreateEasing(compositor, Motion.Settle);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = Motion.Settling;
        var zoom = compositor.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(0, new Vector3(1.04f, 1.04f, 1));
        zoom.InsertKeyFrame(1, Vector3.One, easing);
        zoom.Duration = Motion.Settling;
        imageVisual.StartAnimation("Opacity", fade);
        imageVisual.StartAnimation("Scale", zoom);
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
        if (sender is not FrameworkElement { Tag: string id }) return;
        ViewModel.SelectSeason(id);
        if (sender is ToggleButton seasonButton) seasonButton.IsChecked = true;
        EpisodeScroller.ScrollTo(0, 0, Instant);
    }

    private void OnEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is EpisodeCard { Episode: { } episode }) ViewModel.SelectEpisode(episode.Id);
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
