using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.Views;
using Mambo.App.Views.Controls;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    // Exercise the same activation notifications as MainWindow without depending on another
    // application's foreground window. Do not navigate or settle the page between each pair.
    private static async Task RunWindowActivationAsync(MainWindow window, MotionReport report, CancellationToken token)
    {
        var context = window.Services.GetRequiredService<WindowContext>();
        var host = window.Shell.PageHost;
        var wasActive = context.IsActive;
        try
        {
            await MotionUntilAsync(() => host.CurrentPage is HomePage { HasFirstContent: true }, token);
            var home = host.CurrentPage.As<HomePage>();
            await home.PendingPresentation.WaitAsync(token);
            context.SetActive(true);
            host.SettleTransition(); // Establish the initial state, before exercising activation.
            await MotionUntilAsync(() => MotionDescendants<LandscapeCard>(home).Any(card => card.IsLoaded && card.ActualWidth > 0), token);
            var button = MotionDescendants<LandscapeCard>(home).First(card => card.IsLoaded && card.ActualWidth > 0)
                .FindName("Root").As<Button>();
            var accessibility = AutomationProperties.GetAccessibilityView(button);
            MotionCheck(report, "InitialContentIsInteractive", home.IsHitTestVisible && button.IsTabStop);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                context.SetActive(false);
                MotionCheck(report, $"InactiveContentIsIsolated{cycle}", !home.IsHitTestVisible && !button.IsTabStop);
                context.SetActive(true);
                MotionCheck(report, $"ReactivatedContentRestoresInput{cycle}", ReferenceEquals(host.CurrentPage, home) &&
                    home.IsHitTestVisible && button.IsTabStop && AutomationProperties.GetAccessibilityView(button) == accessibility);
            }

            var point = button.TransformToVisual(window.Shell).TransformPoint(new Point(12, 12));
            MotionCheck(report, "ReactivatedCardReceivesHitTests",
                VisualTreeHelper.FindElementsInHostCoordinates(point, window.Shell, false).Contains(button));

            var playback = window.Services.GetRequiredService<IPlaybackService>();
            var session = await playback.PreviewAsync(token);
            try
            {
                await MotionUntilAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true }, token);
                context.SetActive(false);
                context.SetActive(true);
                MotionCheck(report, "ReactivationKeepsPlayerBackgroundIsolated", !home.IsHitTestVisible && !button.IsTabStop &&
                    !Motion.IsActive(home) && window.Shell.CanHandle);
            }
            finally { await session.CloseAsync(token); }
            await window.Shell.PendingPresentation.WaitAsync(token);
            MotionCheck(report, "ClosingPlayerRestoresReactivatedContent", home.IsHitTestVisible && button.IsTabStop && !window.Shell.CanHandle);
            report.Passed = true;
            report.Stage = "Completed";
        }
        finally { context.SetActive(wasActive); }
    }

    private static async Task VerifyWindowActivationInputAsync(MainWindow window, UiInputProbe input, MotionReport report, CancellationToken token)
    {
        report.Passed = false;
        var context = window.Services.GetRequiredService<WindowContext>();
        var navigation = window.Services.GetRequiredService<Navigator>();
        var host = window.Shell.PageHost;
        var home = host.CurrentPage.As<HomePage>();
        var card = MotionDescendants<LandscapeCard>(home).First(item => item.IsLoaded && item.ActualWidth > 0);
        var button = card.FindName("Root").As<Button>();
        context.SetActive(false);
        context.SetActive(true);
        await input.MoveAsync(button, new Point(12, 12), token);
        await Task.Delay(1500, token);
        MotionCheck(report, "ContentHoverDoesNotShowGlobalShortcutTip",
            !VisualTreeHelper.GetOpenPopupsForXamlRoot(window.Shell.XamlRoot).Any(popup => popup.Child is not null &&
                MotionDescendants<ToolTip>(popup.Child).Any(tip => tip.Content is string text && text == "Ctrl+F")));
        await input.ClickAsync(button, new Point(12, 12), token);
        await MotionUntilAsync(() => navigation.Current.Route.Equals(Route.Detail(card.Item!.Id)), token);
        MotionCheck(report, "ReactivatedCardClickOpensDetail", host.CurrentPage is DetailPage);
        navigation.GoBack();
        await host.PendingTransition.WaitAsync(token);
        await input.KeyAsync(VirtualKey.Divide, token);
        var search = window.Shell.Sidebar.FindName("SearchBox").As<TextBox>();
        await MotionUntilAsync(() => ReferenceEquals(FocusManager.GetFocusedElement(window.Shell.XamlRoot), search), token);
        MotionCheck(report, "SearchShortcutStillFocusesSearchBox", search.FocusState == FocusState.Keyboard);
        report.Passed = true;
        report.Stage = "Completed";
    }
}
