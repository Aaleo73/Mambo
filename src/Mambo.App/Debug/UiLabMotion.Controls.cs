using System.Collections.Immutable;
using System.Numerics;
using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.App.Views.Controls;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    private static async Task RunCardAndRailMotionAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = "HomeRailLifecycle";
        var services = window.Services;
        var navigation = services.GetRequiredService<Navigator>();
        navigation.Navigate(Route.Home);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        var home = window.Shell.PageHost.CurrentPage.As<HomePage>();
        await home.PendingPresentation.WaitAsync(token);
        await MotionUntilAsync(() => MotionDescendants<CardRail>(home).Any(rail => rail.FindName("Scroller").As<ScrollView>().ScrollableWidth > 1), token);
        var homeRail = MotionDescendants<CardRail>(home).First(rail => rail.FindName("Scroller").As<ScrollView>().ScrollableWidth > 1);
        var homeScroller = homeRail.FindName("Scroller").As<ScrollView>();
        await ExerciseMotionRailAsync("Home", homeScroller, homeRail.RailInteraction, homeRail.FindName("RightArrow").As<Button>(),
            () => homeScroller.HorizontalOffset, input, report, token);
        navigation.Navigate(Route.Settings);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        await AwaitNextRenderingAsync(token);
        var inactiveOffset = homeScroller.HorizontalOffset;
        homeRail.RailInteraction.Page(1);
        await AwaitNextRenderingAsync(token);
        MotionCheck(report, "CachedHomeRailStopsWhileHidden", !homeRail.RailInteraction.IsPressed &&
            !homeRail.RailInteraction.IsDragging && !homeRail.RailInteraction.HasPointerCapture &&
            Math.Abs(homeScroller.HorizontalOffset - inactiveOffset) <= 2);
        navigation.Navigate(Route.Home);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        await home.PendingPresentation.WaitAsync(token);
        await MotionUntilAsync(() => homeRail.RailInteraction.IsAttached, token);
        homeScroller.ScrollTo(0, 0, new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        await AwaitNextRenderingAsync(token);
        InvokeMotionButton(homeRail.FindName("RightArrow").As<Button>());
        await MotionUntilAsync(() => homeScroller.HorizontalOffset > 1, token);
        homeRail.RailInteraction.Stop();
        MotionCheck(report, "CachedHomeRailReloadRemainsOperable", homeRail.RailInteraction.IsAttached &&
            !homeRail.RailInteraction.IsPressed && homeScroller.HorizontalOffset > 1);
        navigation.Navigate(Route.Settings);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);

        report.Stage = "CardRebindingAndImageOwnership";
        var library = services.GetRequiredService<ILibraryService>();
        using var libraries = library.ObserveLibraries(token);
        await libraries.RefreshAsync(token);
        using var data = library.ObserveLibrary(libraries.Current.First(item => item.Kind == LibraryKind.Movies).Id, new(), scopeToken: token);
        await data.RefreshAsync(token);
        var firstImage = new ImageRef(data.Items[0].Id, ImageKind.Primary, "motion-card-first");
        var secondImage = new ImageRef(data.Items[1].Id, ImageKind.Primary, "motion-card-second");
        var firstModel = new MediaCardViewModel(data.Items[0] with { Images = [firstImage] }, CardContext.Library, false);
        var secondModel = new MediaCardViewModel(data.Items[1] with { Images = [secondImage] }, CardContext.Library, false);
        using var images = new MotionImageScope(services.GetRequiredService<IImageService>());
        images.Source.Hold(firstImage);
        images.Source.Hold(secondImage);
        var poster = new PosterCard { Item = firstModel };
        var landscape = new LandscapeCard { Item = new MediaCardViewModel(data.Items[1], CardContext.ContinueWatching, true) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        controls.Children.Add(poster);
        controls.Children.Add(landscape);
        var mount = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 246, 247, 249)) };
        mount.Children.Add(controls);
        var well = window.Shell.FindName("WellContent").As<Grid>();
        Motion.SetEntranceSuppressed(mount, true);
        well.Children.Add(mount);
        try
        {
            var picture = poster.FindName("Picture").As<RemoteImage>();
            await MotionUntilAsync(() => picture.IsLoaded && picture.IsLoading && images.Source.Requested(firstImage), token);
            await input.MoveAsync(poster, new Point(16, 16), token);
            await MotionUntilAsync(() => poster.IsMotionHovered, token);
            MotionCheck(report, "CardHoverUsesCurrentContainer", poster.IsMotionHovered);
            poster.FindName("Root").As<Button>().Focus(FocusState.Keyboard);
            poster.Item = secondModel;
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "ReboundCardClearsOldPointerMotion", !poster.IsMotionHovered && !poster.IsMotionPressed &&
                HasResetCardMotion(poster.MotionTarget) && poster.FindName("InnerStroke").As<Border>().Opacity == 1);
            images.Source.Release(secondImage);
            await MotionUntilAsync(() => Equals(picture.Source, secondImage) && picture.CurrentImage is not null && !picture.IsLoading, token);
            var secondBitmap = picture.CurrentImage;
            MotionCheck(report, "ParentTransitionSuppressesColdImageReveal", !picture.IsRevealing &&
                ReferenceEquals(secondBitmap, images.Loader.TryGetDecoded(secondImage, (int)Math.Ceiling(picture.DecodeWidth))) &&
                poster.FindName("Copy").As<FrameworkElement>().Opacity == 1 &&
                MotionDescendants<TextBlock>(poster.FindName("Copy").As<FrameworkElement>()).First().Text == secondModel.Title);
            images.Source.Release(firstImage);
            await AwaitNextRenderingAsync(token);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "CancelledImageCannotRefillReboundCard", ReferenceEquals(secondBitmap, picture.CurrentImage) && Equals(picture.Source, secondImage));
            Motion.SetEntranceSuppressed(mount, false);
            poster.Item = firstModel;
            await MotionUntilAsync(() => Equals(picture.Source, firstImage) && picture.CurrentImage is not null && !picture.IsLoading, token);
            await MotionUntilAsync(() => !picture.IsRevealing, token);
            poster.Item = secondModel;
            await MotionUntilAsync(() => ReferenceEquals(secondBitmap, picture.CurrentImage), token);
            MotionCheck(report, "CachedImageDoesNotRevealAgain", !picture.IsRevealing && poster.FindName("Copy").As<FrameworkElement>().Opacity == 1);
            var noImage = new MediaCardViewModel(data.Items[0] with { Images = [] }, CardContext.Library, false);
            poster.Item = noImage;
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "MissingImageKeepsReadableCopy", picture.CurrentImage is null && !picture.IsRevealing &&
                poster.FindName("Copy").As<FrameworkElement>().Opacity == 1);
            poster.Item = secondModel;
            await AwaitNextRenderingAsync(token);
            controls.Children.Remove(poster);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "UnloadedCardReleasesItsImageAndMotion", !picture.IsLoading && picture.CurrentImage is null &&
                !poster.IsMotionHovered && !poster.IsMotionPressed && HasResetCardMotion(poster.MotionTarget));
            controls.Children.Insert(0, poster);
            await MotionUntilAsync(() => picture.CurrentImage is not null, token);
            MotionCheck(report, "ReusedCardRestoresOnlyCurrentImage", ReferenceEquals(picture.CurrentImage, secondBitmap) && !picture.IsRevealing);
            await input.MoveAsync(landscape, new Point(16, 16), token);
            await MotionUntilAsync(() => landscape.IsMotionHovered, token);
            landscape.FindName("Root").As<Button>().Focus(FocusState.Keyboard);
            var landscapeModel = landscape.Item;
            landscape.Item = new MediaCardViewModel(data.Items[0], CardContext.ContinueWatching, true);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "ReboundLandscapeClearsMotionAndKeepsKeyboardRing", !landscape.IsMotionHovered && !landscape.IsMotionPressed &&
                HasResetCardMotion(landscape.MotionTarget) && landscape.FindName("InnerStroke").As<Border>().Opacity == 1);
            landscape.Item = landscapeModel;
            var entry = navigation.Current;
            InvokeMotionButton(landscape.FindName("PlayButton").As<Button>());
            var playback = services.GetRequiredService<IPlaybackService>();
            await MotionUntilAsync(() => playback.Current is not null && !window.Shell.IsTransitioning &&
                window.Shell.ActivePlayer is { HasAttachedSurface: true }, token);
            MotionCheck(report, "CardPlayDoesNotNavigateToDetail", ReferenceEquals(entry, navigation.Current) &&
                playback.Current!.Snapshot.Entry?.ItemId == data.Items[1].Id);
            await playback.Current!.CloseAsync(token);
            await window.Shell.PendingPresentation.WaitAsync(token);
            MotionCheck(report, "InactiveCardsReturnWithoutOldFeedback", !poster.IsMotionHovered && !poster.IsMotionPressed &&
                !landscape.IsMotionHovered && !landscape.IsMotionPressed && HasResetCardMotion(landscape.MotionTarget));
            InvokeMotionButton(poster.FindName("Root").As<Button>());
            await MotionUntilAsync(() => navigation.Current.Route.Kind == PageKind.Detail, token);
            MotionCheck(report, "CardBodyOnlyNavigates", playback.Current is null && navigation.Current.Route.Parameter == secondModel.Id);
            poster.Item = null;
            controls.Children.Clear();
            navigation.GoBack();
            await window.Shell.PageHost.PendingTransition.WaitAsync(token);
            MotionCheck(report, "CardReturnDoesNotRequireItsSourceContainer", navigation.Current.Route.Equals(entry.Route) && playback.Current is null);
        }
        finally
        {
            Motion.SetActive(mount, false);
            well.Children.Remove(mount);
        }
        await RunOnboardingMotionAsync(window, input, report, token);
    }

    private static async Task RunOnboardingMotionAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = "OnboardingPhrase";
        var services = window.Services;
        var library = services.GetRequiredService<ILibraryService>();
        var scheduler = services.GetRequiredService<IUiScheduler>();
        using var session = new Mambo.Core.Fakes.FakeSessionService(
            new Mambo.Core.Fakes.FakeOperation(new Mambo.Core.Fakes.FakeOptions { Delay = TimeSpan.Zero }),
            scheduler, new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger());
        var navigation = new Navigator();
        using var shell = new ShellViewModel(session, library, navigation);
        await session.LogoutAsync(token);
        await MotionUntilAsync(() => shell.State == SessionState.LoggedOut, token);
        var context = services.GetRequiredService<WindowContext>();
        using var transitions = new BrowseTransitionCoordinator(navigation, context);
        using var home = new HomePage(shell, session, library, navigation, new TitleBarService(), context, transitions) { Width = 640 };
        var well = window.Shell.FindName("WellContent").As<Grid>();
        well.Children.Add(home);
        home.OnNavigatedTo(navigation.Current, NavigationMode.New, created: true);
        try
        {
            var title = home.FindName("OnboardingTitle").As<OnboardingTitle>();
            await MotionUntilAsync(() => title.IsLoaded && title.ActualHeight > 0, token);
            var words = MotionDescendants<TextBlock>(title).ToArray();
            var firstRow = words[0].TransformToVisual(title).TransformBounds(new Rect(0, 0, words[0].ActualWidth, words[0].ActualHeight));
            var secondRow = words[^1].TransformToVisual(title).TransformBounds(new Rect(0, 0, words[^1].ActualWidth, words[^1].ActualHeight));
            MotionCheck(report, "OnboardingFitsTwoSeparateLines", firstRow.Bottom <= secondRow.Top && title.ActualWidth <= home.ActualWidth - 80 + 2);
            var positions = words.Select(word => word.TransformToVisual(title).TransformPoint(default)).ToArray();
            await input.MoveAsync(words[0], new Point(words[0].ActualWidth / 2, words[0].ActualHeight / 2), token);
            await AwaitNextRenderingAsync(token);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "OnboardingHoverDoesNotDisplaceText", words.Select((word, index) =>
            {
                var current = word.TransformToVisual(title).TransformPoint(default);
                return Math.Abs(current.X - positions[index].X) < 0.1 && Math.Abs(current.Y - positions[index].Y) < 0.1;
            }).All(value => value));
            await ClickMotionAsync(input, home.FindName("OnboardingButton").As<Button>(), token);
            MotionCheck(report, "OnboardingWholePhraseNavigatesToSettings", navigation.Current.Route.Kind == PageKind.Settings &&
                services.GetRequiredService<ISessionService>().State == SessionState.LoggedIn);
        }
        finally
        {
            home.OnNavigatedFrom(new NavEntry(Route.Home));
            well.Children.Remove(home);
        }
    }

    private static bool HasResetCardMotion(UIElement? target)
    {
        if (target is null) return false;
        return ElementCompositionPreview.GetElementVisual(target).Properties.TryGetVector3("Translation", out var value) != CompositionGetValueStatus.Succeeded ||
            value == Vector3.Zero;
    }

    private static async Task ExerciseMotionRailAsync(string name, FrameworkElement surface, RailScroller rail, Button next,
        Func<double> offset, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = name + "RailInteraction";
        surface.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await AwaitNextRenderingAsync(token);
        await MotionUntilAsync(() => next.IsEnabled && rail.IsAttached, token);
        var intermediate = false;
        var viewChanged = false;
        void Changed(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            viewChanged = true;
            intermediate = args.IsIntermediate;
        }
        if (surface is ScrollViewer viewer) viewer.ViewChanged += Changed;
        try
        {
            var start = offset();
            InvokeMotionButton(next);
            await MotionUntilAsync(() => offset() > start + 1 &&
                (surface is ScrollView scroll ? scroll.State == ScrollingInteractionState.Idle : viewChanged && !intermediate), token);
            MotionCheck(report, name + "RailArrowMovesActualViewport", offset() > start + 1);
            var y = Math.Min(48, surface.ActualHeight / 2);
            await input.PressLeftAsync(surface, new Point(surface.ActualWidth * 0.4, y), token);
            await AwaitNextRenderingAsync(token);
            report.Measurements[name + "RailPressObserved"] = rail.IsPressed ? 1 : 0;
            var dragFrom = offset();
            report.Measurements[name + "RailOffsetAtPress"] = dragFrom;
            await input.MoveAsync(surface, new Point(surface.ActualWidth * 0.65, y), token);
            report.Stage = name + "RailAwaitingCapture";
            await MotionUntilAsync(() => rail.IsDragging && rail.HasPointerCapture, token);
            report.Stage = name + "RailAwaitingDragOffset";
            await MotionUntilAsync(() => offset() < dragFrom - 1, token);
            MotionCheck(report, name + "RailOwnsActiveDrag", rail.IsPressed && rail.IsDragging && rail.HasPointerCapture);
            Motion.SetActive(surface, false);
            rail.Stop();
            var cancelled = offset();
            await input.ReleaseLeftAsync(token);
            await AwaitNextRenderingAsync(token);
            report.Measurements[name + "RailOffsetAtCancel"] = cancelled;
            report.Measurements[name + "RailOffsetAfterRelease"] = offset();
            MotionCheck(report, name + "RailCancellationReleasesCaptureWithoutSnap", !rail.IsPressed && !rail.IsDragging && !rail.HasPointerCapture &&
                Math.Abs(offset() - cancelled) <= 2);
            Motion.SetActive(surface, true);
        }
        finally
        {
            report.Measurements[name + "RailFinalOffset"] = offset();
            report.Measurements[name + "RailFinalPressed"] = rail.IsPressed ? 1 : 0;
            report.Measurements[name + "RailFinalDragging"] = rail.IsDragging ? 1 : 0;
            report.Measurements[name + "RailFinalCapture"] = rail.HasPointerCapture ? 1 : 0;
            rail.Stop();
            if (surface is ScrollViewer ended) ended.ViewChanged -= Changed;
        }
    }
}
