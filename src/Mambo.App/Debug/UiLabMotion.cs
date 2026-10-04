using System.Collections.Immutable;
using System.Diagnostics;
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
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    private static async Task RunMotionAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        var navigation = window.Services.GetRequiredService<Navigator>();
        var original = navigation.Current.Route;
        try
        {
            await RunPopupMotionAsync(window, input, report, token);
            await RunHeroMotionAsync(window, input, report, token);
            await RunMediaReadinessMotionAsync(window, input, report, token);
            await RunCardAndRailMotionAsync(window, input, report, token);
            report.Stage = "Completed";
        }
        catch (Exception error)
        {
            report.FailureKind = error is UiInputProbe.InputFailure ? error.Message : error.GetType().Name;
        }
        finally
        {
            var dialog = window.Shell.FindName("Dialogs").As<DialogHost>();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do
            {
                if (dialog.IsPresented) InvokeMotionButton(dialog.FindName("CancelButton").As<Button>());
                await AwaitNextRenderingAsync(cleanup.Token);
                await dialog.PendingTransition.WaitAsync(cleanup.Token);
                await AwaitNextRenderingAsync(cleanup.Token);
            } while (dialog.IsPresented || window.Services.GetRequiredService<DialogService>().IsOpen);
            var playback = window.Services.GetRequiredService<IPlaybackService>();
            if (playback.Current is { } session)
                await session.CloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await window.Shell.PendingPresentation.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            navigation.Navigate(original);
            await window.Shell.PageHost.PendingTransition.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            report.ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        }
        report.Passed = report.FailureKind.Length == 0;
    }

    private static void MotionCheck(MotionReport report, string name, bool passed)
    {
        report.Stage = name;
        report.Checks[name] = passed;
        if (!passed) throw new InvalidOperationException(name);
    }

    private static async Task MotionUntilAsync(Func<bool> ready, CancellationToken token, double seconds = 3)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException();
            await AwaitNextRenderingAsync(token);
        }
    }

    private static void InvokeMotionButton(Button button)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("MotionInvokeUnavailable");
        invoke.Invoke();
    }

    private static Task ClickMotionAsync(UiInputProbe input, FrameworkElement element, CancellationToken token) =>
        input.ClickAsync(element, new Point(element.ActualWidth / 2, element.ActualHeight / 2), token);

    private static IEnumerable<T> MotionDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in MotionDescendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task RunPopupMotionAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        var services = window.Services;
        var navigation = services.GetRequiredService<Navigator>();
        using var libraries = services.GetRequiredService<ILibraryService>().ObserveLibraries(token);
        await libraries.RefreshAsync(token);
        var movieLibrary = libraries.Current.First(item => item.Kind == LibraryKind.Movies);
        navigation.Navigate(Route.Library(movieLibrary.Id));
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        var page = window.Shell.PageHost.CurrentPage.As<LibraryPage>();
        await MotionUntilAsync(() => page.ViewModel.Cards.IsInitialized, token);
        var scroller = page.FindName("Scroller").As<ScrollViewer>();
        var body = page.FindName("BodyHost").As<Grid>();
        var filters = page.FindName("FilterPanel").As<FrameworkElement>();
        var button = page.FindName("FilterButton").As<FrameworkElement>();
        page.ViewModel.FiltersOpen = false;
        await page.PendingFilterTransition.WaitAsync(token);
        scroller.ChangeView(null, Math.Min(500, scroller.ScrollableHeight), null, true);
        await AwaitNextRenderingAsync(token);
        var offset = scroller.VerticalOffset;
        report.Stage = "FilterReversal";
        await ClickMotionAsync(input, button, token);
        // SendInput queues the click; a Rendering callback is not an input-pump barrier.
        // Observe the opened state and occupied layout while the transition is live.
        await MotionUntilAsync(() => page.ViewModel.FiltersOpen &&
            (!Motion.AnimationsEnabled || page.IsFilterTransitioning &&
                Math.Abs(scroller.ActualHeight - body.ActualHeight + scroller.Margin.Top + scroller.Margin.Bottom) <= 2), token);
        var entering = page.PendingFilterTransition;
        MotionCheck(report, "FilterOccupiesWithoutDisablingItsButton", page.ViewModel.FiltersOpen && button.IsHitTestVisible &&
            (!Motion.AnimationsEnabled || !scroller.IsHitTestVisible && filters.Visibility == Visibility.Visible &&
                Math.Abs(scroller.ActualHeight - body.ActualHeight + scroller.Margin.Top + scroller.Margin.Bottom) <= 2));
        page.ViewModel.FiltersOpen = false;
        var leaving = page.PendingFilterTransition;
        page.ViewModel.FiltersOpen = true;
        await page.PendingFilterTransition.WaitAsync(token);
        await entering.WaitAsync(token);
        await leaving.WaitAsync(token);
        MotionCheck(report, "FilterLatestOpenPreservesScroll", filters.Visibility == Visibility.Visible && scroller.Margin.Top > 4 &&
            !page.IsFilterTransitioning && scroller.IsHitTestVisible && Math.Abs(scroller.VerticalOffset - offset) <= 2);
        page.ViewModel.FiltersOpen = false;
        body.Width = Math.Max(360, body.ActualWidth - 40);
        await AwaitNextRenderingAsync(token);
        await page.PendingFilterTransition.WaitAsync(token);
        await AwaitNextRenderingAsync(token);
        report.Measurements["FilterScrollBefore"] = offset;
        report.Measurements["FilterScrollAfterResize"] = scroller.VerticalOffset;
        report.Measurements["FilterMarginAfterResize"] = scroller.Margin.Top;
        MotionCheck(report, "FilterResizeSettlesLatestClosed", filters.Visibility == Visibility.Collapsed &&
            !page.IsFilterTransitioning && Math.Abs(scroller.Margin.Top - 4) <= 0.1 &&
            Math.Abs(scroller.VerticalOffset - Math.Clamp(offset, 0, scroller.ScrollableHeight)) <= 2);
        body.Width = double.NaN;
        await AwaitNextRenderingAsync(token);

        var sortButton = page.FindName("SortButton").As<Button>();
        var sortHeader = page.FindName("SortHeader").As<Button>();
        async Task OpenSortAsync(string scenario)
        {
            report.Stage = scenario;
            InvokeMotionButton(sortButton);
            await MotionUntilAsync(() => page.IsSortOpen, token);
            await page.PendingSortTransition.WaitAsync(token);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, scenario, page.IsSortOpen && page.IsSortPresented && page.GlobalSortHandlersAttached);
        }
        await OpenSortAsync("SortInitialOpen");
        InvokeMotionButton(sortHeader);
        await AwaitNextRenderingAsync(token);
        MotionCheck(report, "SortRetiresBeforePhysicalDismiss", !page.IsSortOpen && !page.GlobalSortHandlersAttached && sortButton.IsHitTestVisible &&
            (!Motion.AnimationsEnabled || page.IsSortPresented));
        var previousClose = page.PendingSortTransition;
        InvokeMotionButton(sortButton);
        await AwaitNextRenderingAsync(token);
        await previousClose.WaitAsync(token);
        await page.PendingSortTransition.WaitAsync(token);
        MotionCheck(report, "SortReopenRejectsOldClose", page.IsSortOpen && page.IsSortPresented && page.GlobalSortHandlersAttached);
        var entry = navigation.Current;
        await input.KeyAsync(VirtualKey.Escape, token);
        await page.PendingSortTransition.WaitAsync(token);
        MotionCheck(report, "SortPopupEscapeStaysOnPage", !page.IsSortPresented && !page.GlobalSortHandlersAttached && ReferenceEquals(entry, navigation.Current));
        await OpenSortAsync("SortReopenAfterPopupEscape");
        window.Shell.FindName("MinimizeButton").As<Button>().Focus(FocusState.Programmatic);
        await input.KeyAsync(VirtualKey.Escape, token);
        await page.PendingSortTransition.WaitAsync(token);
        MotionCheck(report, "SortRootEscapeStaysOnPage", !page.IsSortPresented && !page.GlobalSortHandlersAttached && ReferenceEquals(entry, navigation.Current));
        await OpenSortAsync("SortReopenAfterRootEscape");
        var replacementCount = 0;
        void Replaced(object? sender, EventArgs args) => replacementCount++;
        page.ViewModel.ResultsReplaced += Replaced;
        try
        {
            var option = page.FindName("SortOptions").As<StackPanel>().Children.OfType<Button>().First();
            InvokeMotionButton(option);
            InvokeMotionButton(option);
            await MotionUntilAsync(() => replacementCount != 0, token);
            await page.PendingSortTransition.WaitAsync(token);
            MotionCheck(report, "SortSelectionCommitsOnce", replacementCount == 1 && !page.IsSortPresented && !page.GlobalSortHandlersAttached);
        }
        finally { page.ViewModel.ResultsReplaced -= Replaced; }
        await OpenSortAsync("SortReopenAfterSelection");
        await input.ClickAsync(page, new Point(12, 12), token);
        await page.PendingSortTransition.WaitAsync(token);
        MotionCheck(report, "SortOutsidePressDismisses", !page.IsSortPresented && !page.GlobalSortHandlersAttached);
        await OpenSortAsync("SortReopenAfterOutsidePress");
        navigation.Navigate(Route.Settings);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        MotionCheck(report, "SortLeavingDoesNotRetainInput", !page.IsSortPresented && !page.GlobalSortHandlersAttached);

        report.Stage = "DialogQueue";
        var dialog = window.Shell.FindName("Dialogs").As<DialogHost>();
        var dialogs = services.GetRequiredService<DialogService>();
        window.Shell.Sidebar.FocusNavigation();
        var priorFocus = FocusManager.GetFocusedElement(window.Shell.XamlRoot);
        var first = dialogs.ConfirmAsync(new ConfirmRequest("第一项确认", "本地呈现诊断", "确认"));
        var second = dialogs.ConfirmAsync(new ConfirmRequest("第二项确认", "请求按队列呈现", "确认"));
        await dialog.PendingTransition.WaitAsync(token);
        await AwaitNextRenderingAsync(token);
        var softButton = dialog.FindName("CancelButton").As<Button>();
        var buttonSurface = VisualTreeHelper.GetChild(softButton, 0).As<FrameworkElement>();
        var boundsBeforePress = buttonSurface.TransformToVisual(dialog).TransformBounds(new Rect(0, 0, buttonSurface.ActualWidth, buttonSurface.ActualHeight));
        await input.PressLeftAsync(softButton, new Point(softButton.ActualWidth / 2, softButton.ActualHeight / 2), token);
        var boundsDuringPress = buttonSurface.TransformToVisual(dialog).TransformBounds(new Rect(0, 0, buttonSurface.ActualWidth, buttonSurface.ActualHeight));
        MotionCheck(report, "OrdinaryButtonPressDoesNotScale", Math.Abs(boundsBeforePress.Width - boundsDuringPress.Width) < 0.1 &&
            Math.Abs(boundsBeforePress.Height - boundsDuringPress.Height) < 0.1);
        await input.MoveAsync(dialog, new Point(32, 60), token);
        await input.ReleaseLeftAsync(token);
        InvokeMotionButton(dialog.FindName("ConfirmButton").As<Button>());
        InvokeMotionButton(dialog.FindName("CancelButton").As<Button>());
        await AwaitNextRenderingAsync(token);
        window.Shell.Sidebar.FocusNavigation();
        MotionCheck(report, "DialogRetirementKeepsModalFence", !Motion.AnimationsEnabled ||
            dialog.IsClosing && dialog.Visibility == Visibility.Visible && dialog.FocusFenceActive && !first.IsCompleted &&
            PageInputScope.Contains(dialog, FocusManager.GetFocusedElement(window.Shell.XamlRoot) as DependencyObject));
        MotionCheck(report, "DialogFirstCommandWins", await first.WaitAsync(token));
        await MotionUntilAsync(() => dialog.IsPresented && dialog.FindName("TitleText").As<TextBlock>().Text == "第二项确认", token);
        await dialog.PendingTransition.WaitAsync(token);
        InvokeMotionButton(dialog.FindName("CancelButton").As<Button>());
        MotionCheck(report, "DialogQueueCancelRestoresFocus", !await second.WaitAsync(token) && !dialog.IsPresented && !dialog.FocusFenceActive &&
            ReferenceEquals(priorFocus, FocusManager.GetFocusedElement(window.Shell.XamlRoot)));
        var escape = dialogs.ConfirmAsync(new ConfirmRequest("键盘取消", "本地呈现诊断", "确认"));
        await dialog.PendingTransition.WaitAsync(token);
        await input.KeyAsync(VirtualKey.Escape, token);
        MotionCheck(report, "DialogEscapeCancels", !await escape.WaitAsync(token) && !dialog.FocusFenceActive);
        var scrim = dialogs.ConfirmAsync(new ConfirmRequest("背景取消", "本地呈现诊断", "确认"));
        await dialog.PendingTransition.WaitAsync(token);
        entry = navigation.Current;
        await input.ClickAsync(dialog, new Point(32, Math.Max(60, dialog.ActualHeight / 2)), token);
        MotionCheck(report, "DialogScrimCancelsWithoutBackgroundNavigation", !await scrim.WaitAsync(token) && ReferenceEquals(entry, navigation.Current));

        var overlay = window.Shell.FindName("OverlayLayer").As<Grid>();
        using (var disposableService = new DialogService())
        using (var disposableHost = new DialogHost())
        {
            disposableHost.Initialize(services.GetRequiredService<WindowContext>());
            disposableService.Attach(disposableHost);
            overlay.Children.Add(disposableHost);
            try
            {
                var active = disposableService.ConfirmAsync(new ConfirmRequest("释放中的请求", "本地呈现诊断", "确认"));
                var queued = disposableService.ConfirmAsync(new ConfirmRequest("排队中的请求", "本地呈现诊断", "确认"));
                await disposableHost.PendingTransition.WaitAsync(token);
                disposableHost.Dispose();
                disposableService.Dispose();
                MotionCheck(report, "DialogDisposeCompletesActiveAndQueuedFalse", !await active.WaitAsync(token) && !await queued.WaitAsync(token) &&
                    !disposableHost.FocusFenceActive && !disposableHost.IsPresented);
            }
            finally { overlay.Children.Remove(disposableHost); }
        }
        window.Shell.Sidebar.FocusNavigation();
        await RunToastMotionAsync(window, input, report, token);
    }

    private static async Task RunToastMotionAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = "ToastRetirement";
        var overlay = window.Shell.FindName("OverlayLayer").As<Grid>();
        using var service = new ToastService();
        service.Attach(window.DispatcherQueue);
        using var host = new ToastHost(service, window.Services.GetRequiredService<WindowContext>());
        overlay.Children.Add(host);
        try
        {
            await AwaitNextRenderingAsync(token);
            var actions = 0;
            service.Show(ToastKind.Error, "持续错误提示");
            var persistent = service.Items[^1];
            service.Show(ToastKind.Info, "带操作的提示", "执行", () => actions++);
            var actionable = service.Items[^1];
            service.Show(ToastKind.Warning, "第三条提示");
            await host.PendingTransition.WaitAsync(token);
            var retiringNode = host.GetPresentedNode(actionable)!;
            var actionButton = MotionDescendants<Button>(retiringNode).First(item => item.Content is string text && text == "执行");
            InvokeMotionButton(actionButton);
            InvokeMotionButton(actionButton);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "ToastActionImmediateAndOnce", actions == 1 && !service.Items.Contains(actionable) &&
                (!Motion.AnimationsEnabled || host.ExitingCount == 1 && !retiringNode.IsHitTestVisible &&
                    AutomationProperties.GetAccessibilityView(retiringNode) == AccessibilityView.Raw));
            service.Show(ToastKind.Success, "退出期间的新提示");
            MotionCheck(report, "ToastCapacityIncludesRetiringLayers", host.PresentedCount <= 3 && host.GetPresentedNode(actionable) is null &&
                service.Items.Contains(persistent));
            service.Invoke(actionable);
            MotionCheck(report, "ToastStaleActionCannotRepeat", actions == 1);
            service.Show(ToastKind.Info, "容量驱逐检查");
            MotionCheck(report, "ToastCapacityPreservesPersistentError", service.Items.Count == 3 && host.PresentedCount <= 3 && service.Items.Contains(persistent));
            foreach (var item in service.Items.ToArray()) service.Dismiss(item);
            await host.PendingTransition.WaitAsync(token);
            service.Show(ToastKind.Error, "错误不会自动关闭");
            persistent = service.Items[^1];
            service.Show(ToastKind.Info, "悬停暂停，离开后继续驻留");
            var resident = service.Items[^1];
            await host.PendingTransition.WaitAsync(token);
            await ClickFreeHoverAsync(input, host.GetPresentedNode(resident)!, token);
            await Task.Delay(TimeSpan.FromMilliseconds(3150), token);
            MotionCheck(report, "ToastHoverAndPersistentErrorSurviveResidence", service.Items.Contains(resident) && service.Items.Contains(persistent));
            await input.MoveAsync(window.Shell.FindName("MinimizeButton").As<FrameworkElement>(), new Point(8, 8), token);
            await MotionUntilAsync(() => !service.Items.Contains(resident), token, seconds: 4);
            await host.PendingTransition.WaitAsync(token);
            MotionCheck(report, "ToastResidenceResumesAndErrorRemains", service.Items.Contains(persistent) && host.GetPresentedNode(resident) is null);
            host.Dispose();
            service.Show(ToastKind.Info, "释放后不会重建呈现树");
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "ToastDisposedHostDoesNotReattach", host.PresentedCount == 0 && host.ExitingCount == 0 && host.PendingTransition.IsCompleted);
        }
        finally { overlay.Children.Remove(host); }
    }

    private static Task ClickFreeHoverAsync(UiInputProbe input, FrameworkElement target, CancellationToken token) =>
        input.MoveAsync(target, new Point(Math.Min(24, target.ActualWidth / 2), target.ActualHeight / 2), token);

    private static async Task RunHeroMotionAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = "HeroControlledImages";
        var services = window.Services;
        var navigation = services.GetRequiredService<Navigator>();
        var transitions = services.GetRequiredService<BrowseTransitionCoordinator>();
        var backdrop = window.Shell.FindName("HeroBackdrop").As<HeroBackdropPresenter>();
        using var query = services.GetRequiredService<ILibraryService>().ObserveHero(token);
        await query.RefreshAsync(token);
        var items = query.Current.Take(3).ToArray();
        var originalSlides = query.Current.Select(item => new HeroSlideViewModel(item)).ToArray();
        MotionCheck(report, "HeroFixtureHasDistinctArtwork", items.Length == 3 &&
            items.Select(ImagePicker.Backdrop).Distinct().Count() == 3 && items.All(item => ImagePicker.Backdrop(item) is not null));
        var slides = items.Select(item => new HeroSlideViewModel(item with
        {
            Images = item.Images.Where(image => image.Kind != ImageKind.Logo)
                .Append(new ImageRef(item.Id, ImageKind.Logo, "motion-logo")).ToImmutableArray(),
        })).ToArray();
        navigation.Navigate(Route.Settings);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        using var images = new MotionImageScope(services.GetRequiredService<IImageService>());
        images.Source.Hold(slides[0].Logo!);
        HomePage? home = null;
        try
        {
            navigation.Navigate(Route.Home);
            await window.Shell.PageHost.PendingTransition.WaitAsync(token);
            home = window.Shell.PageHost.CurrentPage.As<HomePage>();
            await home.PendingPresentation.WaitAsync(token);
            var hero = home.Carousel;
            hero.SetSlides(slides);
            await SelectHeroAsync(hero, 0, token);
            await SettleHeroAsync(hero, backdrop, token);
            var foreground = DisplayedHeroPanel(hero);
            MotionCheck(report, "HeroCommitsBeforeDelayedLogo", hero.DisplayedSlide?.Id == slides[0].Id &&
                backdrop.DisplayedSource == slides[0].Backdrop && backdrop.DisplayedSurface is not null &&
                foreground.FindName("TitleText").As<TextBlock>().Visibility == Visibility.Visible &&
                HeroCueMatches(hero, 0));
            var firstSurface = backdrop.DisplayedSurface;
            var firstFetches = backdrop.FetchStartedCount;
            images.Source.Release(slides[0].Logo!);
            await MotionUntilAsync(() => foreground.FindName("LogoImage").As<Image>().Source is not null, token);
            MotionCheck(report, "LateLogoDoesNotRestartForeground", hero.PendingForeground.IsCompleted &&
                ReferenceEquals(firstSurface, backdrop.DisplayedSurface) && backdrop.FetchStartedCount == firstFetches);
            var sameTransition = hero.PendingTransition;
            await SelectHeroAsync(hero, 0, token);
            MotionCheck(report, "RepeatedHeroDotIsStable", ReferenceEquals(sameTransition, hero.PendingTransition) &&
                ReferenceEquals(firstSurface, backdrop.DisplayedSurface));

            images.Source.Hold(slides[1].Backdrop!);
            images.Source.Hold(slides[1].Logo!);
            await SelectHeroAsync(hero, 1, token);
            await MotionUntilAsync(() => images.Source.Requested(slides[1].Backdrop!), token);
            MotionCheck(report, "SlowHeroKeepsWholeCommittedItem", hero.DisplayedSlide?.Id == slides[0].Id &&
                hero.RequestedSlide?.Id == slides[1].Id && backdrop.DisplayedSource == slides[0].Backdrop && HeroCueMatches(hero, 0));
            var beforeDetailFetches = backdrop.FetchStartedCount;
            var beforeDetailDecodes = backdrop.DecodeStartedCount;
            InvokeMotionButton(hero.FindName("Root").As<Button>());
            await MotionUntilAsync(() => navigation.Current.Route.Kind == PageKind.Detail, token);
            MotionCheck(report, "PendingHeroCannotBecomeClickTarget", navigation.Current.Route.Parameter == slides[0].Id);
            await window.Shell.PageHost.PendingTransition.WaitAsync(token);
            await MotionUntilAsync(() => window.Shell.PageHost.CurrentPage is DetailPage { ViewModel.HasContent: true }, token);
            await transitions.PendingBackdrop.WaitAsync(token);
            MotionCheck(report, "HeroDetailReusesActualSurface", ReferenceEquals(firstSurface, backdrop.DisplayedSurface) &&
                backdrop.FetchStartedCount == beforeDetailFetches && backdrop.DecodeStartedCount == beforeDetailDecodes);
            var detailFetches = backdrop.FetchStartedCount;
            var detailDecodes = backdrop.DecodeStartedCount;
            navigation.GoBack();
            await window.Shell.PageHost.PendingTransition.WaitAsync(token);
            await SettleHeroAsync(hero, backdrop, token);
            MotionCheck(report, "HeroBackReusesSurfaceAndRecommendation", hero.DisplayedSlide?.Id == slides[0].Id &&
                ReferenceEquals(firstSurface, backdrop.DisplayedSurface) && backdrop.FetchStartedCount == detailFetches &&
                backdrop.DecodeStartedCount == detailDecodes);
            hero.SetSlides(slides);
            await SelectHeroAsync(hero, 1, token);
            images.Source.Hold(slides[2].Backdrop!);
            images.Source.Hold(slides[2].Logo!);
            await SelectHeroAsync(hero, 2, token);
            MotionCheck(report, "HeroLatestRequestStillKeepsOldCommit", hero.DisplayedSlide?.Id == slides[0].Id &&
                hero.RequestedSlide?.Id == slides[2].Id && backdrop.PresentedSurfaceCount <= 2 && backdrop.InFlightDecodeCount <= 1);
            images.Source.Release(slides[1].Backdrop!);
            images.Source.Release(slides[2].Backdrop!);
            await SettleHeroAsync(hero, backdrop, token);
            MotionCheck(report, "HeroLatestImageTextDotsCommitTogether", hero.DisplayedSlide?.Id == slides[2].Id &&
                backdrop.DisplayedSource == slides[2].Backdrop && HeroCueMatches(hero, 2) &&
                backdrop.PresentedSurfaceCount <= 2 && hero.PresentedInfoCount <= 2);
            images.Source.Release(slides[1].Logo!);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "SupersededLogoCannotFillNewSlide", DisplayedHeroPanel(hero).FindName("LogoImage").As<Image>().Source is null &&
                hero.DisplayedSlide?.Id == slides[2].Id);
            images.Source.Release(slides[2].Logo!);
            await MotionUntilAsync(() => DisplayedHeroPanel(hero).FindName("LogoImage").As<Image>().Source is not null, token);
            MotionCheck(report, "CurrentLateLogoDoesNotReplay", hero.PendingForeground.IsCompleted);

            var failedBackdrop = slides[1].Backdrop! with { Tag = "motion-failed" };
            var failedLogo = slides[1].Logo! with { Tag = "motion-failed-logo" };
            images.Source.Fail(failedBackdrop);
            images.Source.Fail(failedLogo);
            var altered = slides.ToArray();
            altered[1] = new HeroSlideViewModel(items[1] with { Images = [failedBackdrop, failedLogo] });
            hero.SetSlides(altered);
            await SelectHeroAsync(hero, 1, token);
            await SettleHeroAsync(hero, backdrop, token);
            MotionCheck(report, "FailedHeroUsesFallbackNotPreviousArtwork", hero.DisplayedSlide?.Id == slides[1].Id &&
                backdrop.DisplayedSurface is null && backdrop.PresentedSurfaceCount == 0 &&
                DisplayedHeroPanel(hero).FindName("TitleText").As<TextBlock>().Visibility == Visibility.Visible && HeroCueMatches(hero, 1));
            var leavingBackdrop = slides[0].Backdrop! with { Tag = "motion-leaving" };
            images.Source.Hold(leavingBackdrop);
            altered[0] = new HeroSlideViewModel(items[0] with { Images = [leavingBackdrop] });
            hero.SetSlides(altered);
            await SelectHeroAsync(hero, 0, token);
            await MotionUntilAsync(() => images.Source.Requested(leavingBackdrop), token);
            navigation.Navigate(Route.Settings);
            await window.Shell.PageHost.PendingTransition.WaitAsync(token);
            images.Source.Release(leavingBackdrop);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "LeavingHeroRejectsLateArtwork", backdrop.PresentedSurfaceCount == 0 && !hero.IsTimerRunning);
            hero.SetSlides(slides);
            navigation.Navigate(Route.Home);
            await window.Shell.PageHost.PendingTransition.WaitAsync(token);
            await SettleHeroAsync(hero, backdrop, token);
            await RunHeroTimerMotionAsync(window, home, input, report, token);
            hero.SetSlides([]);
            await hero.PendingTransition.WaitAsync(token);
            MotionCheck(report, "EmptyHeroStopsInputAndSurfaces", hero.DisplayedSlide is null && !hero.IsTimerRunning &&
                !hero.FindName("Root").As<Button>().IsHitTestVisible && backdrop.PresentedSurfaceCount == 0);
        }
        finally
        {
            navigation.Navigate(Route.Settings);
            await window.Shell.PageHost.PendingTransition.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            home?.Carousel.SetSlides(originalSlides);
        }
    }

    private static async Task SelectHeroAsync(HeroCarousel hero, int index, CancellationToken token)
    {
        InvokeMotionButton(hero.Dots.Children[index + 1].As<Button>());
        await AwaitNextRenderingAsync(token);
    }

    private static async Task SettleHeroAsync(HeroCarousel hero, HeroBackdropPresenter backdrop, CancellationToken token)
    {
        await hero.PendingTransition.WaitAsync(token);
        await hero.PendingForeground.WaitAsync(token);
        await backdrop.PendingTransition.WaitAsync(token);
        await AwaitNextRenderingAsync(token);
    }

    private static HeroInfoPanel DisplayedHeroPanel(HeroCarousel hero)
    {
        var first = hero.FindName("FirstInfo").As<HeroInfoPanel>();
        return AutomationProperties.GetAccessibilityView(first) == AccessibilityView.Content && first.Slide is not null
            ? first : hero.FindName("SecondInfo").As<HeroInfoPanel>();
    }

    private static bool HeroCueMatches(HeroCarousel hero, int index)
    {
        if (hero.DisplayedSlide is not { } slide || AutomationProperties.GetName(hero.FindName("Root").As<Button>()) != slide.Title ||
            DisplayedHeroPanel(hero).Slide?.Id != slide.Id) return false;
        for (var current = 0; current < hero.Count; current++)
        {
            var pill = hero.Dots.Children[current + 1].As<Button>().Content.As<Grid>();
            if (pill.Children[1].As<Border>().Opacity != (current == index ? 1 : 0)) return false;
        }
        return true;
    }

    private static async Task RunHeroTimerMotionAsync(MainWindow window, HomePage home, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Stage = "HeroFullResidence";
        var hero = home.Carousel;
        var neutral = window.Shell.FindName("MinimizeButton").As<Button>();
        neutral.Focus(FocusState.Programmatic);
        await input.MoveAsync(neutral, new Point(8, 8), token);
        hero.SetVisibleFraction(0.1);
        MotionCheck(report, "HeroVisibilityPausesTimer", !hero.IsTimerRunning);
        hero.SetVisibleFraction(1);
        await input.MoveAsync(hero, new Point(24, 24), token);
        await MotionUntilAsync(() => !hero.IsTimerRunning, token);
        MotionCheck(report, "HeroHoverPausesTimer", !hero.IsTimerRunning);
        await input.MoveAsync(neutral, new Point(8, 8), token);
        hero.Dots.Children[1].As<Button>().Focus(FocusState.Programmatic);
        await AwaitNextRenderingAsync(token);
        MotionCheck(report, "HeroTitlebarFocusPausesTimer", !hero.IsTimerRunning);
        neutral.Focus(FocusState.Programmatic);
        await AwaitNextRenderingAsync(token);
        if (!Motion.AnimationsEnabled)
        {
            var before = hero.DisplayedSlide?.Id;
            var current = Enumerable.Range(0, hero.Count).Single(index => HeroCueMatches(hero, index));
            await SelectHeroAsync(hero, (current + 1) % hero.Count, token);
            await hero.PendingTransition.WaitAsync(token);
            MotionCheck(report, "ReducedMotionStopsAutomaticHero", !hero.IsTimerRunning && hero.DisplayedSlide?.Id != before);
            return;
        }
        await MotionUntilAsync(() => hero.IsTimerRunning, token);
        home.SetCovered(true);
        var pausedId = hero.DisplayedSlide?.Id;
        await Task.Delay(TimeSpan.FromMilliseconds(7100), token);
        MotionCheck(report, "CoveredHeroDoesNotAdvanceAfterSevenSeconds", !hero.IsTimerRunning && hero.DisplayedSlide?.Id == pausedId);
        var resumed = Stopwatch.StartNew();
        home.SetCovered(false);
        report.Stage = "HeroResumesAfterCover";
        try
        {
            await MotionUntilAsync(() => hero.DisplayedSlide?.Id != pausedId, token, seconds: 9);
        }
        finally
        {
            report.Measurements["HeroResumeWindowActive"] = window.Services.GetRequiredService<WindowContext>().IsActive ? 1 : 0;
            report.Measurements["HeroResumeTimerRunning"] = hero.IsTimerRunning ? 1 : 0;
            report.Measurements["HeroResumeRequestPending"] = hero.IsRequestPending ? 1 : 0;
            report.Measurements["HeroResumeForegroundPending"] = hero.PendingForeground.IsCompleted ? 0 : 1;
            report.Measurements["HeroResumeBackdropPending"] = window.Shell.FindName("HeroBackdrop").As<HeroBackdropPresenter>().IsTransitioning ? 1 : 0;
            report.Measurements["HeroResumeParentActive"] = Motion.IsActive(hero) ? 1 : 0;
            report.Measurements["HeroResumeParentSuppressed"] = Motion.IsEntranceSuppressed(hero) ? 1 : 0;
            report.Measurements["HeroResumeRootFocused"] = hero.FindName("Root").As<Button>().FocusState != FocusState.Unfocused ? 1 : 0;
            report.Measurements["HeroResumeDotsFocused"] = hero.Dots.Children.OfType<Button>().Any(button => button.FocusState != FocusState.Unfocused) ? 1 : 0;
            report.Measurements["HeroResumeRequestedChanged"] = hero.RequestedSlide?.Id != pausedId ? 1 : 0;
        }
        MotionCheck(report, "HeroResumeReceivesFullSevenSeconds", resumed.Elapsed >= TimeSpan.FromSeconds(7));
        await hero.PendingTransition.WaitAsync(token);
    }

    /// <summary>A scoped diagnostic binding; the application's loader/cache is restored unchanged.</summary>
    private sealed class MotionImageScope : IDisposable
    {
        private readonly ImageLoader? previous = ImageLoader.Current;
        internal MotionImageScope(IImageService images)
        {
            Source = new ControlledMotionImages(images);
            Loader = new ImageLoader(Source);
        }
        internal ControlledMotionImages Source { get; }
        internal ImageLoader Loader { get; }
        public void Dispose()
        {
            Loader.Dispose();
            Source.ReleaseAll();
            ImageLoader.Current = previous;
        }
    }

    private sealed class ControlledMotionImages(IImageService inner) : IImageService
    {
        private readonly Dictionary<ImageRef, TaskCompletionSource> gates = [];
        private readonly HashSet<ImageRef> failures = [];
        private readonly HashSet<ImageRef> requested = [];
        internal void Hold(ImageRef image) => gates[image] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Fail(ImageRef image) => failures.Add(image);
        internal bool Requested(ImageRef image) => requested.Contains(image);
        internal void Release(ImageRef image) { if (gates.TryGetValue(image, out var gate)) gate.TrySetResult(); }
        internal void ReleaseAll() { foreach (var gate in gates.Values) gate.TrySetResult(); }
        public async Task<ReadOnlyMemory<byte>> FetchAsync(ImageRef image, int pixelWidth, ImagePriority priority = ImagePriority.Visible,
            CancellationToken cancellationToken = default)
        {
            requested.Add(image);
            if (gates.TryGetValue(image, out var gate)) await gate.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (failures.Contains(image))
                throw new AppException(new AppError(AppErrorKind.Contract, ErrorCodes.ImageNotFound, "诊断图片不存在。", false));
            // Synthetic logos use actual decodable BMP bytes from the same local demo artwork.
            return await inner.FetchAsync(image.Kind == ImageKind.Logo ? image with { Kind = ImageKind.Backdrop } : image,
                pixelWidth, priority, cancellationToken);
        }
    }
}
