using System.Collections.Immutable;
using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.App.Views.Controls;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    private static async Task RunMediaReadinessMotionAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = "ControlledMediaReadiness";
        var services = window.Services;
        var fallback = services.GetRequiredService<ILibraryService>();
        var context = services.GetRequiredService<WindowContext>();
        using var libraries = fallback.ObserveLibraries(token);
        await libraries.RefreshAsync(token);
        var movies = libraries.Current.First(item => item.Kind == LibraryKind.Movies);
        var shows = libraries.Current.First(item => item.Kind == LibraryKind.TvShows);
        using var movieData = fallback.ObserveLibrary(movies.Id, new(), scopeToken: token);
        using var showData = fallback.ObserveLibrary(shows.Id, new(), scopeToken: token);
        await movieData.RefreshAsync(token);
        await showData.RefreshAsync(token);
        using var fixture = new MotionLibraryFixture(fallback, services.GetRequiredService<IUiScheduler>());
        fixture.HoldLibrary(movies.Id);
        fixture.HoldLibrary(shows.Id);
        using var images = new MotionImageScope(services.GetRequiredService<IImageService>());
        var lateImage = new ImageRef(showData.Items[0].Id, ImageKind.Primary, "motion-library-cold");
        images.Source.Hold(lateImage);
        var heldShow = showData.Items[0] with { Images = [lateImage] };
        var navigation = new Navigator();
        navigation.Navigate(Route.Library(movies.Id));
        using var transitions = new BrowseTransitionCoordinator(navigation, context);
        var backdrop = new HeroBackdropPresenter();
        transitions.Attach(backdrop);
        using var host = new PageHost();
        var mount = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 246, 247, 249)) };
        var well = window.Shell.FindName("WellContent").As<Grid>();
        mount.Children.Add(backdrop);
        mount.Children.Add(host);
        host.Initialize(route => route.Kind switch
        {
            PageKind.Library => new LibraryPage(new LibraryViewModel(fixture, services.GetRequiredService<ILibraryPreferences>(),
                route.Parameter == movies.Id ? movies : shows), context),
            PageKind.Recent => new RecentPage(new RecentViewModel(fixture)),
            PageKind.Detail => new DetailPage(new DetailViewModel(fixture, services.GetRequiredService<PlaybackLauncher>(),
                services.GetRequiredService<IPlaybackService>(), route.Parameter!), context, transitions),
            _ => throw new InvalidOperationException("MotionFixtureRouteUnsupported"),
        }, navigation, transitions);
        void Navigated(object? sender, NavigatedEventArgs args) => host.Show(args);
        navigation.Navigated += Navigated;
        well.Children.Add(mount);
        try
        {
            host.Show(new NavigatedEventArgs(null, navigation.Current, NavigationMode.New));
            var first = host.CurrentPage.As<LibraryPage>();
            fixture.LibraryRequests[^1].Query.Complete(movieData.Items.Take(2).ToImmutableArray());
            await MotionUntilAsync(() => first.ViewModel.Cards.IsInitialized && first.IsLoaded, token);
            await host.PendingTransition.WaitAsync(token);
            await AwaitNextRenderingAsync(token);
            navigation.Navigate(Route.Library(shows.Id));
            var delayed = host.CurrentPage.As<LibraryPage>();
            var delayedRead = fixture.LibraryRequests[^1].Query;
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "MediaWaitKeepsPreviousCompletePage", host.IsWaitingForPresentation &&
                ReferenceEquals(host.PresentedPage, first) && ReferenceEquals(host.CurrentPage, delayed) &&
                first.ViewModel.Cards.Items[0].Id == movieData.Items[0].Id);
            navigation.Navigate(Route.Recent);
            await host.PendingTransition.WaitAsync(token);
            var recent = host.CurrentPage;
            delayedRead.Complete([heldShow]);
            await AwaitNextRenderingAsync(token);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "SupersededMediaReadCannotTakePresentation", recent is RecentPage &&
                ReferenceEquals(host.CurrentPage, recent) && ReferenceEquals(host.PresentedPage, recent) &&
                FocusManager.FindFirstFocusableElement(delayed) is null);
            navigation.Navigate(Route.Library(shows.Id));
            await host.PendingTransition.WaitAsync(token);
            await MotionUntilAsync(() => MotionDescendants<RemoteImage>(delayed).Any(image => Equals(image.Source, lateImage) && image.IsLoading), token);
            MotionCheck(report, "MediaReadinessDoesNotWaitForImages", ReferenceEquals(host.PresentedPage, delayed) &&
                delayed.ViewModel.Cards.IsInitialized && delayed.ViewModel.Cards.Items[0].Id == heldShow.Id);
            images.Source.Release(lateImage);

            var previousCards = delayed.ViewModel.Cards;
            delayed.ViewModel.SetSort(delayed.ViewModel.Sorts.First(option => !option.IsSelected));
            var emptyRead = fixture.LibraryRequests[^1].Query;
            MotionCheck(report, "SameLibraryQueryKeepsCommittedCards", ReferenceEquals(previousCards, delayed.ViewModel.Cards) &&
                delayed.ViewModel.Cards.Items[0].Id == heldShow.Id);
            emptyRead.Complete([]);
            await MotionUntilAsync(() => delayed.ViewModel.ShowEmpty && !ReferenceEquals(previousCards, delayed.ViewModel.Cards), token);
            MotionCheck(report, "EmptyMediaReadCommitsVisibleEmptyState", delayed.FindName("EmptyState").As<FrameworkElement>().Visibility == Visibility.Visible);
            delayed.ViewModel.SetSort(delayed.ViewModel.Sorts.First(option => !option.IsSelected));
            var errorRead = fixture.LibraryRequests[^1].Query;
            errorRead.Fail(new AppError(AppErrorKind.Contract, "motion.fixture-unavailable", "诊断数据暂不可用。", true));
            await MotionUntilAsync(() => delayed.ViewModel.Cards.HasError, token);
            MotionCheck(report, "FailedMediaReadCommitsRetryableState", delayed.FindName("ErrorState").As<FrameworkElement>().Visibility == Visibility.Visible);
            var retry = MotionDescendants<Button>(delayed.FindName("ErrorState").As<FrameworkElement>())
                .First(button => button.Content is string text && text == "重试");
            report.Stage = "RetryFailedMediaRead";
            var refreshes = errorRead.RefreshCount;
            InvokeMotionButton(retry);
            await MotionUntilAsync(() => errorRead.RefreshCount == refreshes + 1 && errorRead.IsLoading, token);
            errorRead.Complete([heldShow]);
            await MotionUntilAsync(() => !delayed.ViewModel.Cards.HasError && delayed.ViewModel.Cards.Items.Count == 1, token);
            MotionCheck(report, "MediaRetryRestoresActualCards", delayed.ViewModel.Cards.Items[0].Id == heldShow.Id && ReferenceEquals(host.PresentedPage, delayed));
            MotionCheck(report, "MediaFixturePresentationRemainsBounded", host.PresentedPageCount <= 2 && host.CachedPageCount <= 4);

            using var seasons = fallback.ObserveSeasons(showData.Items[0].Id, token);
            await seasons.RefreshAsync(token);
            var coldSeason = seasons.Current[1];
            using var episodeData = fallback.ObserveEpisodes(coldSeason.Id, scopeToken: token);
            await episodeData.RefreshAsync(token);
            fixture.HoldEpisodes(coldSeason.Id);
            mount.Width = 640;
            mount.HorizontalAlignment = HorizontalAlignment.Left;
            navigation.Navigate(Route.Detail(showData.Items[0].Id));
            await host.PendingTransition.WaitAsync(token);
            var detail = host.CurrentPage.As<DetailPage>();
            await MotionUntilAsync(() => detail.ViewModel.HasContent && detail.ViewModel.Episodes.Count > 1 && detail.ViewModel.Seasons.Count > 1, token);
            await RunSeasonMotionAsync(detail, fixture, coldSeason, episodeData.Items[0], input, report, token);
            Motion.SetActive(mount, false);
            MotionCheck(report, "DetailRailsStopWhenInactive", !detail.EpisodeRailInteraction.IsPressed &&
                !detail.EpisodeRailInteraction.HasPointerCapture && detail.PeopleRailInteraction is { IsPressed: false, HasPointerCapture: false });
        }
        finally
        {
            navigation.Navigated -= Navigated;
            host.Dispose();
            well.Children.Remove(mount);
        }
    }

    private static async Task RunSeasonMotionAsync(DetailPage detail, MotionLibraryFixture fixture, SeasonInfo coldSeason,
        MediaItem episodeSource, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = "ColdSeasonLayoutAndPagination";
        var scroller = detail.FindName("EpisodeScroller").As<ScrollView>();
        var skeleton = detail.FindName("EpisodeSkeleton").As<FrameworkElement>();
        var slot = VisualTreeHelper.GetParent(scroller).As<FrameworkElement>();
        var firstSeason = detail.ViewModel.SelectedSeasonId;
        var firstButton = MotionDescendants<ToggleButton>(detail).First(button => button.Tag is string id && id == firstSeason);
        firstButton.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await AwaitNextRenderingAsync(token);
        scroller.ScrollTo(Math.Min(316, scroller.ScrollableWidth), 0, new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        await AwaitNextRenderingAsync(token);
        var before = scroller.HorizontalOffset;
        await ClickMotionAsync(input, firstButton, token);
        MotionCheck(report, "SelectingCurrentSeasonPreservesRailOffset", Math.Abs(scroller.HorizontalOffset - before) <= 2);
        var height = slot.ActualHeight;
        var coldButton = MotionDescendants<ToggleButton>(detail).First(button => button.Tag is string id && id == coldSeason.Id);
        await ClickMotionAsync(input, coldButton, token);
        var abandoned = fixture.EpisodeRequests[^1].Query;
        MotionCheck(report, "ColdSeasonUsesOneStableRailSlot", skeleton.Visibility == Visibility.Visible && scroller.Visibility == Visibility.Collapsed &&
            Math.Abs(slot.ActualHeight - height) <= 2);
        await ClickMotionAsync(input, firstButton, token);
        abandoned.Complete([episodeSource]);
        await MotionUntilAsync(() => !detail.ViewModel.IsEpisodesLoading && detail.ViewModel.Episodes.Count > 0, token);
        MotionCheck(report, "AbandonedSeasonCannotReplaceCurrentEpisodes", detail.ViewModel.SelectedSeasonId == firstSeason &&
            detail.ViewModel.Episodes.All(item => item.Item.SeasonId == firstSeason));

        await ClickMotionAsync(input, coldButton, token);
        var read = fixture.EpisodeRequests[^1].Query;
        var pages = Enumerable.Range(1, 40).Select(index => episodeSource with
        {
            Id = "motion-episode-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name = "诊断分集 " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IndexNumber = index,
        }).ToImmutableArray();
        detail.ViewModel.RestoreSelection(coldSeason.Id, pages[35].Id);
        read.Complete(pages.Take(30).ToImmutableArray(), hasMore: true, totalCount: 40);
        await MotionUntilAsync(() => read.LoadMoreCount == 1, token);
        read.Complete(pages.Skip(30).ToImmutableArray(), totalCount: 40);
        await MotionUntilAsync(() => detail.ViewModel.SelectedEpisodeId == pages[35].Id && scroller.HorizontalOffset > 0, token);
        var repeater = detail.FindName("EpisodeRepeater").As<ItemsRepeater>();
        await MotionUntilAsync(() => repeater.TryGetElement(35) is EpisodeCard, token);
        var selected = repeater.TryGetElement(35).As<EpisodeCard>();
        await MotionUntilAsync(() =>
        {
            var bounds = selected.TransformToVisual(scroller).TransformBounds(new Rect(0, 0, selected.ActualWidth, selected.ActualHeight));
            return bounds.Left >= -2 && bounds.Right <= scroller.ActualWidth + 2;
        }, token);
        MotionCheck(report, "PagedSelectedEpisodeIsActuallyPositioned", selected.Episode?.Id == pages[35].Id &&
            skeleton.Visibility == Visibility.Collapsed && scroller.Visibility == Visibility.Visible && Math.Abs(slot.ActualHeight - height) <= 2);
        selected.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await AwaitNextRenderingAsync(token);
        await input.MoveAsync(selected, new Point(16, 16), token);
        await MotionUntilAsync(() => selected.IsMotionHovered, token);
        selected.FindName("Root").As<Button>().Focus(FocusState.Keyboard);
        var selectedModel = selected.Episode;
        selected.Episode = detail.ViewModel.Episodes[34];
        await AwaitNextRenderingAsync(token);
        MotionCheck(report, "ReboundEpisodeClearsMotionAndKeepsKeyboardRing", !selected.IsMotionHovered && !selected.IsMotionPressed &&
            HasResetCardMotion(selected.MotionTarget) && selected.FindName("InnerStroke").As<Border>().Opacity == 1);
        selected.Episode = selectedModel;
        firstButton.Focus(FocusState.Programmatic);
        scroller.ScrollTo(0, 0, new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        await AwaitNextRenderingAsync(token);
        await ExerciseMotionRailAsync("Episode", scroller, detail.EpisodeRailInteraction,
            detail.FindName("EpisodesRight").As<Button>(), () => scroller.HorizontalOffset, input, report, token);
        report.Stage = "PreparingPeopleRail";
        // The shared offline catalog deliberately has only two people. Supply a
        // local long cast so this scenario exercises an actually scrollable rail.
        for (var index = 0; index < 8; index++)
            detail.ViewModel.People.Add(new DetailPersonViewModel(
                new PersonInfo("motion-person-" + index, "诊断演员 " + index, PersonKind.Actor)));
        var peopleScroller = detail.FindName("PeopleScroller").As<ScrollView>();
        peopleScroller.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await AwaitNextRenderingAsync(token);
        await MotionUntilAsync(() => peopleScroller.ScrollableWidth > 1, token);
        await ExerciseMotionRailAsync("People", peopleScroller, detail.PeopleRailInteraction,
            detail.FindName("PeopleRight").As<Button>(), () => peopleScroller.HorizontalOffset, input, report, token);
    }
}
