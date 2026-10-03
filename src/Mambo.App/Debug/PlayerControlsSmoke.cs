using System.Diagnostics;
using Mambo.App.Shell;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mambo.App.Debug;

/// <summary>使用真实播放层的共享事件分派及按钮 AutomationPeer；仅允许本地假服务。</summary>
internal static class PlayerControlsSmoke
{
    public static async Task<PlayerControlsReport> RunAsync(MainWindow window, IPlaybackService playback, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(playback);
        var report = new PlayerControlsReport();
        var watch = Stopwatch.StartNew();
        var navigation = window.Services.GetRequiredService<Navigator>();
        var presentation = window.Services.GetRequiredService<WindowContext>();
        var originalMaximized = presentation.IsMaximized;
        var mayCleanup = false;
        try
        {
            if (playback is not FakePlaybackService)
            {
                report.Status = "Unsupported";
                report.Reason = "控件烟测只允许假播放服务。";
                return report;
            }
            mayCleanup = true;
            Mark(report, "StartsWithoutPlayback", playback.Current is null && window.Shell.ActivePlayer is null);
            // 建立真实前进历史，才能验证播放期间前进确实被锁住。
            var originalRoute = navigation.Current.Route;
            navigation.Navigate(originalRoute.Kind == PageKind.Settings ? Route.Home : Route.Settings);
            Mark(report, "PrepareForwardHistory", navigation.GoBack() && navigation.CanGoForward);
            await Task.Yield();
            var previousFocus = window.Shell.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
            report.Stage = "OpenSeries";
            var session = await playback.PlayAsync(new PlayRequest("demo-series-001"), token);
            report.SessionsOpened++;
            await WaitAsync(() => window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, session) && active.ViewModel.CanControl, token);
            var player = window.Shell.ActivePlayer!;
            var fake = (FakePlaybackSession)session;
            report.SeriesEpisodeCount = session.Snapshot.Entries.Length;
            Mark(report, "SeriesEpisodeQueue", report.SeriesEpisodeCount >= 2 && player.ViewModel.HasEpisodes);
            player.ShowControlsForSmoke();
            await CheckAsync(report, "InitialPlayerFocus", () => IsFocusedWithin(player), token);
            var browsingEntry = navigation.Current;
            navigation.Navigate(originalRoute.Kind == PageKind.Home ? Route.Settings : Route.Home);
            Mark(report, "NavigationLocked", navigation.ForwardBlocked && ReferenceEquals(navigation.Current, browsingEntry) && !navigation.GoForward());
            Mark(report, "CoveredPageDisabled", window.Shell.PageHost.CurrentPage is Control { IsEnabled: false });

            async Task KeyAsync(string name, VirtualKey key, Func<bool> state)
            {
                report.Stage = name;
                Mark(report, name + "Handled", await player.DispatchSmokeKeyAsync(key));
                await CheckAsync(report, name, state, token);
            }
            async Task PauseAsync(bool paused)
            {
                if (player.ViewModel.IsPaused != paused)
                    await player.DispatchSmokeKeyAsync(VirtualKey.Space);
                await WaitAsync(() => player.ViewModel.IsPaused == paused, token);
            }

            await PauseAsync(true);
            await KeyAsync("SpaceResumes", VirtualKey.Space, () => !player.ViewModel.IsPaused);
            await KeyAsync("SpacePauses", VirtualKey.Space, () => player.ViewModel.IsPaused);
            await InvokeButtonAsync(player, player.ViewModel.PauseAccessibleName, token);
            await CheckAsync(report, "PauseButtonResumes", () => !player.ViewModel.IsPaused, token);
            await InvokeButtonAsync(player, player.ViewModel.PauseAccessibleName, token);
            await CheckAsync(report, "PauseButtonPauses", () => player.ViewModel.IsPaused, token);

            player.DispatchSmokeTap();
            await Task.Delay(120, token);
            Mark(report, "SingleTapWaitsForDoubleTap", player.ViewModel.IsPaused);
            await CheckAsync(report, "SingleTapTogglesPause", () => !player.ViewModel.IsPaused, token);
            await PauseAsync(true);
            player.DispatchSmokeTap();
            await Task.Delay(50, token);
            player.DispatchSmokeTap(doubleTap: true);
            await CheckAsync(report, "DoubleTapEntersFullscreen", () => presentation.IsFullscreen, token);
            await Task.Delay(350, token);
            Mark(report, "DoubleTapCancelsSingleTap", player.ViewModel.IsPaused);
            await KeyAsync("F11LeavesFullscreen", VirtualKey.F11, () => !presentation.IsFullscreen);
            await KeyAsync("F11EntersFullscreen", VirtualKey.F11, () => presentation.IsFullscreen);
            await KeyAsync("F11TogglesFullscreen", VirtualKey.F11, () => !presentation.IsFullscreen);
            await InvokeButtonAsync(player, "切换全屏", token);
            await CheckAsync(report, "FullscreenButtonEnters", () => presentation.IsFullscreen, token);
            await InvokeButtonAsync(player, "切换全屏", token);
            await CheckAsync(report, "FullscreenButtonLeaves", () => !presentation.IsFullscreen, token);
            await InvokeButtonAsync(player, "最大化或还原", token);
            await CheckAsync(report, "MaximizeButton", () => presentation.IsMaximized != originalMaximized, token);
            await InvokeButtonAsync(player, "最大化或还原", token);
            await CheckAsync(report, "MaximizeButtonRestores", () => presentation.IsMaximized == originalMaximized, token);

            await player.DispatchSmokeVolumeAsync(50);
            await CheckAsync(report, "VolumeSlider", () => player.ViewModel.Volume == 50, token);
            await KeyAsync("ArrowUpVolume", VirtualKey.Up, () => player.ViewModel.Volume == 55);
            await KeyAsync("ArrowDownVolume", VirtualKey.Down, () => player.ViewModel.Volume == 50);
            await player.DispatchSmokeVolumeAsync(100);
            await WaitAsync(() => player.ViewModel.Volume == 100, token);
            await KeyAsync("VolumeUpperClamp", VirtualKey.Up, () => player.ViewModel.Volume == 100);
            await player.DispatchSmokeVolumeAsync(0);
            await WaitAsync(() => player.ViewModel.Volume == 0, token);
            await KeyAsync("VolumeLowerClamp", VirtualKey.Down, () => player.ViewModel.Volume == 0);
            await KeyAsync("MutesWithM", VirtualKey.M, () => player.ViewModel.Snapshot.IsMuted);
            await KeyAsync("UnmutesWithM", VirtualKey.M, () => !player.ViewModel.Snapshot.IsMuted);
            await InvokeButtonAsync(player, player.ViewModel.MuteAccessibleName, token);
            await CheckAsync(report, "MuteButton", () => player.ViewModel.Snapshot.IsMuted, token);
            await InvokeButtonAsync(player, player.ViewModel.MuteAccessibleName, token);
            await CheckAsync(report, "MuteButtonRestores", () => !player.ViewModel.Snapshot.IsMuted, token);

            await player.DispatchSmokeSeekAsync(25);
            await WaitAsync(() => At(session, 25), token);
            player.BeginSmokeSeek(40);
            Mark(report, "SeekDragShowsPreview", player.SeekTipVisible && Math.Abs(player.DisplayedSeekSeconds - 40) < .01 && At(session, 25));
            await Task.Delay(300, token);
            Mark(report, "SeekDragDoesNotCommitEarly", player.SeekTipVisible && At(session, 25));
            await player.CommitSmokeSeekAsync();
            await CheckAsync(report, "SeekDragCommitsOnRelease", () => !player.SeekTipVisible && At(session, 40), token);
            var seekRange = new SliderAutomationPeer((Slider)player.FindName("SeekSlider"));
            seekRange.SetValue(55);
            await CheckAsync(report, "SeekAutomationRangeValue", () => At(session, 55) && Math.Abs(player.DisplayedSeekSeconds - 55) < .01, token);
            seekRange.SetValue(40);
            await WaitAsync(() => At(session, 40), token);
            await KeyAsync("ArrowRightSeek", VirtualKey.Right, () => At(session, 45));
            await KeyAsync("ArrowLeftSeek", VirtualKey.Left, () => At(session, 40));
            await player.DispatchSmokeSeekAsync(1);
            await WaitAsync(() => At(session, 1), token);
            await KeyAsync("SeekLowerClamp", VirtualKey.Left, () => At(session, 0));
            var durationSeconds = TimeSpan.FromTicks(session.Snapshot.DurationTicks).TotalSeconds;
            await player.DispatchSmokeSeekAsync(durationSeconds - 1);
            await WaitAsync(() => At(session, durationSeconds - 1), token);
            await KeyAsync("SeekUpperClamp", VirtualKey.Right, () => At(session, durationSeconds));
            await player.DispatchSmokeSeekAsync(60);
            await WaitAsync(() => At(session, 60), token);
            var frameStart = session.Snapshot.PositionTicks;
            await KeyAsync("CommaFrameBackward", (VirtualKey)188, () => session.Snapshot.IsPaused && session.Snapshot.PositionTicks < frameStart);
            await KeyAsync("PeriodFrameForward", (VirtualKey)190, () => session.Snapshot.IsPaused && session.Snapshot.PositionTicks == frameStart);

            await player.DispatchSmokeRateAsync(1);
            await WaitAsync(() => player.ViewModel.Snapshot.PlaybackRate == 1, token);
            await KeyAsync("BracketSlower", (VirtualKey)219, () => player.ViewModel.Snapshot.PlaybackRate == .75);
            await KeyAsync("BracketFaster", (VirtualKey)221, () => player.ViewModel.Snapshot.PlaybackRate == 1);
            var rateMenu = player.ShowMenuForSmoke(tracks: false);
            await CheckAsync(report, "RateMenuOpened", () => player.HasOpenMenu, token);
            Mark(report, "RateMenuChoices", rateMenu.Items.OfType<ToggleMenuFlyoutItem>().Count() == 6);
            await player.DispatchSmokeRateAsync(1.5);
            await CheckAsync(report, "RateMenuSelectsAndCloses", () => player.ViewModel.Snapshot.PlaybackRate == 1.5 && !player.HasOpenMenu, token);
            await player.DispatchSmokeRateAsync(2);
            await WaitAsync(() => player.ViewModel.Snapshot.PlaybackRate == 2, token);
            await KeyAsync("RateUpperClamp", (VirtualKey)221, () => player.ViewModel.Snapshot.PlaybackRate == 2);
            await player.DispatchSmokeRateAsync(.5);
            await WaitAsync(() => player.ViewModel.Snapshot.PlaybackRate == .5, token);
            await KeyAsync("RateLowerClamp", (VirtualKey)219, () => player.ViewModel.Snapshot.PlaybackRate == .5);

            var subtitles = session.Snapshot.SubtitleTracks;
            var audio = session.Snapshot.AudioTracks;
            Mark(report, "FakeTrackChoices", subtitles.Length == 2 && audio.Length == 2);
            await KeyAsync("CNextSubtitle", VirtualKey.C, () => player.ViewModel.Snapshot.SelectedSubtitleTrackId == subtitles[1].Id);
            await KeyAsync("CDisablesSubtitles", VirtualKey.C, () => player.ViewModel.Snapshot.SelectedSubtitleTrackId is null);
            await KeyAsync("CCyclesSubtitles", VirtualKey.C, () => player.ViewModel.Snapshot.SelectedSubtitleTrackId == subtitles[0].Id);
            await KeyAsync("VNextAudio", VirtualKey.V, () => player.ViewModel.Snapshot.SelectedAudioTrackId == audio[1].Id);
            await KeyAsync("VCyclesAudio", VirtualKey.V, () => player.ViewModel.Snapshot.SelectedAudioTrackId == audio[0].Id);
            var trackMenu = player.ShowMenuForSmoke(tracks: true);
            await CheckAsync(report, "TrackMenuOpened", () => player.HasOpenMenu, token);
            Mark(report, "TrackMenuChoices", trackMenu.Items.OfType<ToggleMenuFlyoutItem>().Count() == 5);
            await player.DispatchSmokeTrackAsync(audio[1].Id, subtitle: false);
            await CheckAsync(report, "AudioMenuSelects", () => player.ViewModel.Snapshot.SelectedAudioTrackId == audio[1].Id && !player.HasOpenMenu, token);
            player.ShowMenuForSmoke(tracks: true);
            await WaitAsync(() => player.HasOpenMenu, token);
            await player.DispatchSmokeTrackAsync(subtitles[1].Id, subtitle: true);
            await CheckAsync(report, "SubtitleMenuSelects", () => player.ViewModel.Snapshot.SelectedSubtitleTrackId == subtitles[1].Id && !player.HasOpenMenu, token);
            player.ShowMenuForSmoke(tracks: true);
            await WaitAsync(() => player.HasOpenMenu, token);
            await player.DispatchSmokeTrackAsync(null, subtitle: true);
            await CheckAsync(report, "SubtitleMenuOff", () => player.ViewModel.Snapshot.SelectedSubtitleTrackId is null && !player.HasOpenMenu, token);

            player.ToggleEpisodesForSmoke();
            Mark(report, "EpisodeDrawerOpens", player.EpisodeDrawerOpen);
            await player.SetEpisodeGridForSmokeAsync(false);
            Mark(report, "EpisodeListLayout", !player.EpisodeGridVisible);
            await player.SetEpisodeGridForSmokeAsync(true);
            Mark(report, "EpisodeGridLayout", player.EpisodeGridVisible);
            Mark(report, "EpisodeLayoutPreferenceSaved", window.Services.GetRequiredService<ISettingsService>().Current.UseEpisodeGrid);
            var firstEntry = session.Snapshot.Entries[0];
            await player.DispatchSmokeEpisodeAsync(firstEntry.ItemId);
            await CheckAsync(report, "SelectEpisode", () => player.ViewModel.CanControl && player.ViewModel.Snapshot.CurrentEntryIndex == 0, token);
            var firstPosition = session.Snapshot.PositionTicks;
            await player.DispatchSmokeNavigationAsync(next: false);
            Mark(report, "FirstEpisodePreviousDisabled", session.Snapshot.CurrentEntryIndex == 0 && session.Snapshot.PositionTicks >= firstPosition);
            var lastEntry = session.Snapshot.Entries[^1];
            await player.DispatchSmokeEpisodeAsync(lastEntry.ItemId);
            await WaitAsync(() => player.ViewModel.CanControl && session.Snapshot.Entry?.ItemId == lastEntry.ItemId &&
                player.ViewModel.Snapshot.Entry?.ItemId == lastEntry.ItemId && !player.ViewModel.CanNext, token);
            await player.DispatchSmokeNavigationAsync(next: true);
            await CheckAsync(report, "LastEpisodeNextDisabled", () => session.Snapshot.Entry?.ItemId == lastEntry.ItemId &&
                player.ViewModel.Snapshot.Entry?.ItemId == lastEntry.ItemId && !player.ViewModel.CanNext, token);
            await player.DispatchSmokeEpisodeAsync(firstEntry.ItemId);
            await WaitAsync(() => player.ViewModel.CanControl && session.Snapshot.CurrentEntryIndex == 0 && player.ViewModel.Snapshot.CurrentEntryIndex == 0, token);
            await InvokeButtonAsync(player, "下一集", token);
            await CheckAsync(report, "NextEpisodeButton", () => player.ViewModel.CanControl && player.ViewModel.Snapshot.CurrentEntryIndex == 1, token);
            await InvokeButtonAsync(player, "上一集", token);
            await CheckAsync(report, "PreviousEpisodeButton", () => player.ViewModel.CanControl && player.ViewModel.Snapshot.CurrentEntryIndex == 0, token);
            player.ToggleEpisodesForSmoke();
            await PauseAsync(true);
            var nearEnd = TimeSpan.FromTicks(session.Snapshot.DurationTicks).TotalSeconds - 10;
            await player.DispatchSmokeSeekAsync(nearEnd);
            await CheckAsync(report, "UpNextAppears", () => player.ViewModel.ShowUpNext, token);
            await InvokeButtonAsync((DependencyObject)player.FindName("UpNext"), "取消", token, byContent: true);
            await CheckAsync(report, "UpNextCancelButton", () => !player.ViewModel.ShowUpNext, token);
            await player.DispatchSmokeNavigationAsync(next: true);
            await WaitAsync(() => player.ViewModel.CanControl && session.Snapshot.CurrentEntryIndex == 1 && player.ViewModel.Snapshot.CurrentEntryIndex == 1, token);
            await PauseAsync(true);
            nearEnd = TimeSpan.FromTicks(session.Snapshot.DurationTicks).TotalSeconds - 10;
            await player.DispatchSmokeSeekAsync(nearEnd);
            await WaitAsync(() => player.ViewModel.ShowUpNext, token);
            await InvokeButtonAsync((DependencyObject)player.FindName("UpNext"), "立即播放", token, byContent: true);
            await CheckAsync(report, "UpNextPlayButton", () => player.ViewModel.CanControl &&
                session.Snapshot.Phase == PlayerPhase.Playing && player.ViewModel.Snapshot.Phase == PlayerPhase.Playing &&
                session.Snapshot.CurrentEntryIndex == 2 && player.ViewModel.Snapshot.CurrentEntryIndex == 2, token);
            await PauseAsync(true);
            await player.DispatchSmokeSeekAsync(80);
            await WaitAsync(() => At(session, 80), token);

            await fake.SimulateBufferingAsync(true, token);
            await CheckAsync(report, "BufferingPanel", () => player.BufferingVisible, token);
            await fake.SimulateBufferingAsync(false, token);
            await CheckAsync(report, "BufferingPanelClears", () => !player.BufferingVisible, token);
            await fake.SimulateFailureAsync(cancellationToken: token);
            await CheckAsync(report, "FailurePanel", () => player.ErrorPanelVisible && player.ViewModel.IsFailed && !player.ViewModel.CanControl, token);
            var staleRetryAction = window.Services.GetRequiredService<ToastService>().Items.LastOrDefault(item => item.ActionText == "重试")?.Action;
            Mark(report, "FailureToastActionAvailable", staleRetryAction is not null);
            var failurePosition = session.Snapshot.PositionTicks;
            Mark(report, "FailureDisablesPlaybackKeys", !await player.DispatchSmokeKeyAsync(VirtualKey.Space) && session.Snapshot.PositionTicks == failurePosition);
            await player.DispatchSmokeRetryAsync();
            await CheckAsync(report, "FailureRetry", () => player.ViewModel.CanControl && !player.ErrorPanelVisible &&
                session.Snapshot.PositionTicks == failurePosition && session.Snapshot.Error is null, token);

            await ProbeSnapshotPanelsAsync(window, player, report, token);
            player.ShowControlsForSmoke();
            var pauseButton = FindButton(player, player.ViewModel.PauseAccessibleName, byContent: false);
            Mark(report, "FocusControl", pauseButton.Focus(FocusState.Keyboard));
            var moved = false;
            // Desktop WinUI 没有 UWP 的隐式根。使用整窗根，仍须证明播放层阻止焦点逃到浏览页。
            var focusOptions = new FindNextElementOptions { SearchRoot = window.Shell };
            foreach (var direction in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
                for (var index = 0; index < 24; index++)
                {
                    moved |= FocusManager.TryMoveFocus(direction, focusOptions);
                    await Task.Yield();
                    Mark(report, direction == FocusNavigationDirection.Next ? "TabFocusContained" : "ShiftTabFocusContained", IsFocusedWithin(player));
                }
            Mark(report, "TabFocusMoves", moved);
            var pausedBeforeIgnored = session.Snapshot.IsPaused;
            Mark(report, "ModifierKeysIgnored", !await player.DispatchSmokeKeyAsync(VirtualKey.Space, control: true) &&
                !await player.DispatchSmokeKeyAsync(VirtualKey.Space, alt: true) && session.Snapshot.IsPaused == pausedBeforeIgnored);
            Mark(report, "UnknownKeyIgnored", !await player.DispatchSmokeKeyAsync(VirtualKey.A));
            Mark(report, "AltRightIgnored", await player.DispatchSmokeKeyAsync(VirtualKey.Right, alt: true) && ReferenceEquals(playback.Current, session));
            await player.DispatchSmokeMouseButtonAsync(back: false);
            Mark(report, "MouseForwardIgnored", ReferenceEquals(playback.Current, session));

            await player.DispatchSmokeKeyAsync(VirtualKey.F11);
            await WaitAsync(() => presentation.IsFullscreen, token);
            player.ToggleEpisodesForSmoke();
            player.ShowMenuForSmoke(tracks: false);
            await WaitAsync(() => player.HasOpenMenu, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "EscapeClosesMenuFirst", () => !player.HasOpenMenu && player.EpisodeDrawerOpen && presentation.IsFullscreen && playback.Current is not null, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            Mark(report, "EscapeClosesDrawerSecond", !player.EpisodeDrawerOpen && presentation.IsFullscreen && playback.Current is not null);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "EscapeLeavesFullscreenThird", () => !presentation.IsFullscreen && playback.Current is not null, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "EscapeClosesPlaybackLast", () => playback.Current is null && window.Shell.ActivePlayer is null && !navigation.ForwardBlocked, token);
            Mark(report, "NavigationRestored", navigation.CanGoForward && ReferenceEquals(navigation.Current, browsingEntry));
            Mark(report, "FocusRestored", previousFocus is { IsLoaded: true, IsEnabled: true }
                ? ReferenceEquals(FocusManager.GetFocusedElement(window.Shell.XamlRoot), previousFocus)
                : !IsFocusedWithin(player));
            staleRetryAction!.Invoke();
            await Task.Yield();
            Mark(report, "ClosedPlayerToastRetryIgnored", playback.Current is null && window.Shell.ActivePlayer is null);

            report.Stage = "EpisodeLayoutExternalCloseRace";
            var layoutSession = await playback.PreviewAsync(token);
            report.SessionsOpened++;
            await WaitAsync(() => window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, layoutSession) && active.ViewModel.CanControl, token);
            var layoutOwner = window.Shell.ActivePlayer!;
            var layoutSettings = window.Services.GetRequiredService<ISettingsService>();
            var fakeDelayMs = window.Services.GetRequiredService<FakeOptions>().Delay.TotalMilliseconds;
            var queuedWrites = Enumerable.Range(0, Math.Clamp((int)Math.Ceiling(200 / Math.Max(1, fakeDelayMs)), 2, 24))
                .Select(_ => layoutSettings.UpdateAsync(settings => settings, token)).ToArray();
            layoutOwner.SetEpisodeGridForSmoke(false);
            Mark(report, "EpisodeLayoutWritePendingAtExternalClose", !layoutOwner.PendingPreferenceSave.IsCompleted);
            await layoutSession.CloseAsync(cancellationToken: token);
            var layoutReplacement = await playback.PreviewAsync(token);
            report.SessionsOpened++;
            await WaitAsync(() => window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, layoutReplacement) && active.ViewModel.CanControl, token);
            var nextLayoutOwner = window.Shell.ActivePlayer!;
            await Task.WhenAll(queuedWrites);
            Mark(report, "EpisodeLayoutExternalCloseReopen", !layoutSettings.Current.UseEpisodeGrid && !nextLayoutOwner.EpisodeGridVisible);
            await nextLayoutOwner.SetEpisodeGridForSmokeAsync(true);
            Mark(report, "EpisodeLayoutRaceRestoresGrid", layoutSettings.Current.UseEpisodeGrid && nextLayoutOwner.EpisodeGridVisible);
            await layoutReplacement.CloseAsync(cancellationToken: token);
            await WaitAsync(() => playback.Current is null && window.Shell.ActivePlayer is null && !navigation.ForwardBlocked, token);

            foreach (var close in new[] { "AltLeftCloses", "MouseBackCloses", "NavigatorBackCloses", "CloseButtonCloses" })
            {
                report.Stage = close;
                var auxiliary = await playback.PreviewAsync(token);
                report.SessionsOpened++;
                await WaitAsync(() => window.Shell.ActivePlayer is { IsLoaded: true } active &&
                    ReferenceEquals(active.Session, auxiliary) && active.ViewModel.CanControl, token);
                var active = window.Shell.ActivePlayer!;
                Mark(report, "EpisodeLayoutRememberedByNewOverlay", active.EpisodeGridVisible);
                if (close == "AltLeftCloses") await active.DispatchSmokeKeyAsync(VirtualKey.Left, alt: true);
                else if (close == "MouseBackCloses") await active.DispatchSmokeMouseButtonAsync(back: true);
                else if (close == "NavigatorBackCloses") Mark(report, "NavigatorBackIntercepted", navigation.GoBack());
                else await InvokeButtonAsync(active, "关闭播放", token);
                await CheckAsync(report, close, () => playback.Current is null && window.Shell.ActivePlayer is null && !navigation.ForwardBlocked, token);
            }
            report.Passed = true;
            report.Status = "Passed";
            report.Stage = "Complete";
        }
        catch (SmokeCheckException error)
        {
            report.Status = "Failed";
            report.Reason = "控件检查未通过：" + error.Check;
        }
        catch (Exception error)
        {
            report.Status = "Failed";
            report.Reason = "控件烟测在 " + report.Stage + " 阶段中止：" + error.GetType().Name;
        }
        finally
        {
            try
            {
                if (mayCleanup)
                {
                    presentation.ExitFullscreen();
                    if (playback.Current is { } remaining) await remaining.CloseAsync(cancellationToken: CancellationToken.None);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await WaitAsync(() => playback.Current is null && window.Shell.ActivePlayer is null && !navigation.ForwardBlocked, cleanup.Token);
                    if (presentation.IsMaximized != originalMaximized) presentation.ToggleMaximize();
                    report.SessionClosed = true;
                }
            }
            catch (Exception error)
            {
                report.Passed = false;
                report.Status = "Failed";
                report.Reason += " 清理失败：" + error.GetType().Name;
            }
            report.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
        return report;
    }

    private static async Task ProbeSnapshotPanelsAsync(MainWindow window, PlayerOverlay parent, PlayerControlsReport report, CancellationToken token)
    {
        var session = new SnapshotProbeSession();
        var overlay = new PlayerOverlay(session, window.Services.GetRequiredService<WindowContext>(),
            window.Services.GetRequiredService<ToastService>(), window.Services.GetRequiredService<ISettingsService>());
        var host = (Grid)parent.FindName("Root");
        host.Children.Add(overlay);
        try
        {
            await WaitAsync(() => overlay.IsLoaded, token);
            await CheckAsync(report, "SlowOpeningPanel", () => overlay.OpeningPanelVisible && overlay.SlowOpeningVisible && !overlay.ViewModel.CanControl, token);
            Mark(report, "OpeningDisablesPlaybackKeys", !await overlay.DispatchSmokeKeyAsync(VirtualKey.Space));
            session.SetSnapshot(session.Snapshot with { Phase = PlayerPhase.Playing, IsSlowOpening = false, EngineKind = EngineKind.External });
            await CheckAsync(report, "ExternalPanel", () => overlay.ExternalPanelVisible && !overlay.OpeningPanelVisible && overlay.ControlsVisible && !overlay.VideoSurface.IsDemoAttached, token);
            await InvokeButtonAsync(overlay, "停止播放", token, byContent: true);
            await CheckAsync(report, "ExternalStopButton", () => session.Snapshot.Phase == PlayerPhase.Closed && session.CloseCount == 1, token);
        }
        finally
        {
            host.Children.Remove(overlay);
            overlay.Dispose();
            parent.Focus(FocusState.Programmatic);
        }
    }

    private static async Task InvokeButtonAsync(DependencyObject root, string label, CancellationToken token, bool byContent = false)
    {
        if (root is PlayerOverlay player) player.ShowControlsForSmoke();
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        var button = FindButton(root, label, byContent);
        try { await WaitAsync(() => button.IsEnabled, token); }
        catch (TimeoutException) { throw new SmokeCheckException("DisabledButton"); }
        new ButtonAutomationPeer(button).Invoke();
        await Task.Yield();
    }

    private static Button FindButton(DependencyObject root, string label, bool byContent)
    {
        Button? Find(DependencyObject node)
        {
            if (node is Button button && (byContent ? button.Content is string text && text == label : AutomationProperties.GetName(button) == label)) return button;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                if (Find(VisualTreeHelper.GetChild(node, index)) is { } result) return result;
            return null;
        }
        return Find(root) ?? throw new SmokeCheckException("MissingButton");
    }

    private static bool IsFocusedWithin(PlayerOverlay player)
    {
        if (player.XamlRoot is null) return false;
        for (var element = FocusManager.GetFocusedElement(player.XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, player)) return true;
        return false;
    }

    private static bool At(IPlaybackSession session, double seconds) =>
        Math.Abs(session.Snapshot.PositionTicks - (long)(seconds * TimeSpan.TicksPerSecond)) < TimeSpan.TicksPerMillisecond * 10;

    private static async Task CheckAsync(PlayerControlsReport report, string name, Func<bool> ready, CancellationToken token)
    {
        report.Stage = name;
        try { await WaitAsync(ready, token); Mark(report, name, true); }
        catch (TimeoutException) { Mark(report, name, false); }
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException();
            await Task.Delay(10, token);
        }
    }

    private static void Mark(PlayerControlsReport report, string name, bool passed)
    {
        report.Stage = name;
        report.Checks[name] = passed;
        if (!passed) throw new SmokeCheckException(name);
    }

    private sealed class SmokeCheckException(string check) : Exception
    {
        public string Check { get; } = check;
    }

    /// <summary>只控制 Debug 投影状态，不替代服务或新增生产契约。</summary>
    private sealed class SnapshotProbeSession : IPlaybackSession
    {
        public SessionSnapshot Snapshot { get; private set; } = new()
        {
            Phase = PlayerPhase.Opening, EngineKind = EngineKind.Demo, IsSlowOpening = true,
            Entry = new PlaybackEntry("ui-probe", "界面状态演示"), DurationTicks = TimeSpan.FromMinutes(1).Ticks,
            DemoColorArgb = 0xFF182536,
        };
        public int CloseCount { get; private set; }
        public event EventHandler? SnapshotChanged;
        public void SetSnapshot(SessionSnapshot value) { Snapshot = value; SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        public Task CloseAsync(CancellationToken cancellationToken = default) => CloseAsync(PlaybackEndReason.UserClosed, cancellationToken);
        public Task CloseAsync(PlaybackEndReason reason, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Snapshot.Phase != PlayerPhase.Closed) { CloseCount++; SetSnapshot(Snapshot with { Phase = PlayerPhase.Closed }); }
            return Task.CompletedTask;
        }
        public Task TogglePauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectEntryAsync(string itemId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepFrameAsync(FrameStepDirection direction, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

internal sealed class PlayerControlsReport
{
    public bool Passed { get; set; }
    public string Status { get; set; } = "Pending";
    public string Stage { get; set; } = "ValidateService";
    public string Reason { get; set; } = "";
    public string Scope { get; set; } = "Local fake service; real XAML shared event paths and button AutomationPeer; no physical input device claim";
    public Dictionary<string, bool> Checks { get; set; } = [];
    public int SeriesEpisodeCount { get; set; }
    public int SessionsOpened { get; set; }
    public bool SessionClosed { get; set; }
    public double ElapsedMilliseconds { get; set; }
}
