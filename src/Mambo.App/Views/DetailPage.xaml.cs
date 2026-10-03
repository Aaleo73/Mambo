using System.ComponentModel;
using System.Numerics;
using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;

namespace Mambo.App.Views;

public sealed partial class DetailPage : UserControl, INavigablePage, IDisposable
{
    private readonly WindowContext window;
    private WindowContrastObserver? contrastObserver;
    private ScrollViewer? episodeScroller;
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
        ViewModel.TargetEpisodeAvailable += OnTargetEpisodeAvailable;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        EpisodeList.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnEpisodeWheel), true);
    }

    public DetailViewModel ViewModel { get; }

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
        DetachScroller();
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
        AttachScroller();
        InitializeHeroArt();
        _ = LoadHeroArtAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachScroller();
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
        Hero.Height = Math.Clamp(Math.Round((XamlRoot?.Size.Height ?? ActualHeight) * 0.62), 500, 600);
        var size = new Vector2((float)ActualWidth, (float)(Hero.Height + 96));
        if (imageVisual is not null) imageVisual.Size = size;
        if (scrimVisual is not null) scrimVisual.Size = size;
    }
    private void OnEpisodeListLoaded(object sender, RoutedEventArgs e) => AttachScroller();

    private void AttachScroller()
    {
        if (disposed || episodeScroller is not null) return;
        episodeScroller = FindScroller(EpisodeList);
        if (episodeScroller is not null)
        {
            episodeScroller.ViewChanged += OnEpisodeViewChanged;
            episodeScroller.SizeChanged += OnEpisodeScrollerSizeChanged;
        }
        ScrollToTarget();
        CheckLoadMore();
    }

    private void DetachScroller()
    {
        if (episodeScroller is null) return;
        episodeScroller.ViewChanged -= OnEpisodeViewChanged;
        episodeScroller.SizeChanged -= OnEpisodeScrollerSizeChanged;
        episodeScroller = null;
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

    private void OnEpisodeViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => CheckLoadMore();
    private void OnEpisodeScrollerSizeChanged(object sender, SizeChangedEventArgs e) => CheckLoadMore();
    private void CheckLoadMore()
    {
        if (episodeScroller is not { } scroller || !IsLoaded || ViewModel.HasEpisodeMoreError) return;
        if (scroller.ScrollableWidth - scroller.HorizontalOffset <= scroller.ViewportWidth * 1.5) _ = ViewModel.LoadMoreAsync();
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
        if (e.PropertyName == nameof(DetailViewModel.IsLoadingMore) && !ViewModel.IsLoadingMore)
            DispatcherQueue.TryEnqueue(CheckLoadMore);
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
        var target = ViewModel.Episodes.FirstOrDefault(e => e.Id == id);
        if (target is not null) EpisodeList.ScrollIntoView(target, ScrollIntoViewAlignment.Default);
    }

    private void OnBackdropOpened()
    {
        if (highContrast || !Motion.AnimationsEnabled || !IsLoaded || Visibility != Visibility.Visible) return;
        ConnectedAnimationService.GetForCurrentView().GetAnimation("poster")?.TryStart(ArtHost);
    }

    private void InitializeHeroArt()
    {
        if (heroVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(ArtHost).Compositor;
        heroVisual = compositor.CreateContainerVisual();
        var mask = compositor.CreateLinearGradientBrush();
        mask.StartPoint = Vector2.Zero;
        mask.EndPoint = new Vector2(0, 1);
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0.56f, Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0.76f, Windows.UI.Color.FromArgb(140, 255, 255, 255)));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));
        var imageMask = compositor.CreateMaskBrush();
        imageMask.Mask = mask;
        imageVisual = compositor.CreateSpriteVisual();
        imageVisual.Brush = imageMask;
        var shade = compositor.CreateLinearGradientBrush();
        shade.StartPoint = Vector2.Zero;
        shade.EndPoint = new Vector2(1, 0);
        shade.ColorStops.Add(compositor.CreateColorGradientStop(0, Windows.UI.Color.FromArgb(184, 0, 0, 0)));
        shade.ColorStops.Add(compositor.CreateColorGradientStop(0.42f, Windows.UI.Color.FromArgb(112, 0, 0, 0)));
        shade.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));
        var shadeMask = compositor.CreateMaskBrush();
        shadeMask.Source = shade;
        shadeMask.Mask = mask;
        scrimVisual = compositor.CreateSpriteVisual();
        scrimVisual.Brush = shadeMask;
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
            brush.VerticalAlignmentRatio = 0.25f;
            var masked = (CompositionMaskBrush)imageVisual.Brush;
            var previous = masked.Source as CompositionSurfaceBrush;
            masked.Source = brush;
            previous?.Dispose();
            currentSurface?.Dispose();
            currentSurface = loaded;
            loaded = null;
            OnBackdropOpened();
        }
        catch (OperationCanceledException) { }
        finally { loaded?.Dispose(); }
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
        if (sender is FrameworkElement { Tag: string id })
        {
            ViewModel.SelectSeason(id);
            if (sender is ToggleButton seasonButton) seasonButton.IsChecked = true;
            episodeScroller?.ChangeView(0, null, null, true);
        }
    }
    private void OnEpisodeClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string id }) ViewModel.SelectEpisode(id); }
    private async void OnEpisodePlayClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string id }) await ViewModel.PlayEpisodeAsync(id); }
    private void OnEpisodesLeftClick(object sender, RoutedEventArgs e) => ScrollEpisodes(-1);
    private void OnEpisodesRightClick(object sender, RoutedEventArgs e) => ScrollEpisodes(1);
    private void OnSeasonsLeftClick(object sender, RoutedEventArgs e) => ScrollSeasons(-1);
    private void OnSeasonsRightClick(object sender, RoutedEventArgs e) => ScrollSeasons(1);
    private void ScrollSeasons(int direction) => SeasonScroller.ChangeView(Math.Clamp(SeasonScroller.HorizontalOffset + direction * SeasonScroller.ViewportWidth * 0.82, 0, SeasonScroller.ScrollableWidth), null, null, !Motion.AnimationsEnabled);
    private void ScrollEpisodes(int direction)
    {
        if (episodeScroller is { } scroller) scroller.ChangeView(Math.Clamp(scroller.HorizontalOffset + direction * scroller.ViewportWidth * 0.82, 0, scroller.ScrollableWidth), null, null, !Motion.AnimationsEnabled);
    }
    private void OnEpisodeWheel(object sender, PointerRoutedEventArgs e)
    {
        if (episodeScroller is not { } scroller) return;
        var point = e.GetCurrentPoint(EpisodeList).Properties;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!point.IsHorizontalMouseWheel && !shift) return;
        var delta = point.IsHorizontalMouseWheel ? point.MouseWheelDelta : -point.MouseWheelDelta;
        scroller.ChangeView(Math.Clamp(scroller.HorizontalOffset + delta, 0, scroller.ScrollableWidth), null, null, !Motion.AnimationsEnabled);
        e.Handled = true;
    }

    private sealed record DetailViewState(string? SeasonId, string? EpisodeId);
}
