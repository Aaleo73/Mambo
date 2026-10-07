using System.Diagnostics;
using System.Text.Json;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT;

namespace Mambo.App.Debug;

/// <summary>使用真实播放层的共享事件分派及按钮 AutomationPeer；仅允许本地假服务。</summary>
internal static class PlayerControlsSmoke
{
    internal const string Argument = "--player-controls-smoke";

    /// <summary>只运行播放控件，不启动完整 UiLab 的导航、性能或外观旧阶段。</summary>
    internal static async Task RunStandaloneAsync(MainWindow window, string reportPath)
    {
        var report = new PlayerControlsReport { Stage = "WaitForShell" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var services = window.Services;
            var playback = services.GetRequiredService<IPlaybackService>();
            if (playback is not FakePlaybackService || services.GetRequiredService<ISettingsService>() is not FakeSettingsService ||
                services.GetRequiredService<ISessionService>() is not FakeSessionService || services.GetRequiredService<ILibraryService>() is not FakeLibraryService ||
                services.GetRequiredService<IImageService>() is not FakeImageService || services.GetRequiredService<ILibraryPreferences>() is not FakeLibraryPreferences)
                throw new InvalidOperationException("RealBackendRejected");
            await WaitAsync(() => window.Shell.IsLoaded && window.Shell.ActualWidth > 0, deadline.Token);
            // 完整 RunAsync 依赖 UiLab 的前台占用与动画预热，独立入口只验本轮字幕功能。
            report = await RunSubtitleOnlyAsync(window, playback, deadline.Token);
        }
        catch (Exception error)
        {
            report.Passed = false;
            report.Status = "Failed";
            report.Reason = "独立控件诊断中止：" + error.GetType().Name;
        }
        finally
        {
            Environment.ExitCode = report.Passed ? 0 : 1;
            try
            {
                var path = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, UiLabJsonContext.Default.PlayerControlsReport));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { Environment.ExitCode = 1; }
            window.Close();
        }
    }

    /// <summary>本轮字幕功能的独立范围；旧播放器/Fold 断言仍完整保留在 RunAsync。</summary>
    private static async Task<PlayerControlsReport> RunSubtitleOnlyAsync(MainWindow window, IPlaybackService playback, CancellationToken token)
    {
        var report = new PlayerControlsReport
        {
            Scope = "Subtitle controls only; in-memory fake session and real XAML shared event paths; no physical input or rendering claim",
        };
        var watch = Stopwatch.StartNew();
        var presentation = window.Services.GetRequiredService<WindowContext>();
        try
        {
            Mark(report, "FakeServiceOnly", playback is FakePlaybackService);
            Mark(report, "StartsWithoutPlayback", playback.Current is null && window.Shell.ActivePlayer is null);
            var session = await playback.PreviewAsync(token);
            report.SessionsOpened++;
            await WaitAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, session) && active.ViewModel.CanControl, token);
            var player = window.Shell.ActivePlayer!;
            if (!session.Snapshot.IsPaused) await session.TogglePauseAsync(token);
            await WaitAsync(() => player.ViewModel.IsPaused, token);
            Mark(report, "DemoRejectsRealFileImport", session.Snapshot.EngineKind == EngineKind.Demo &&
                !session.Snapshot.CanImportSubtitles && session.BeginSubtitleImport() is null);
            report.SeriesEpisodeCount = session.Snapshot.Entries.Length;
            var subtitles = session.Snapshot.SubtitleTracks;
            var audio = session.Snapshot.AudioTracks;
            Mark(report, "FakeTrackChoices", subtitles.Length == 2 && audio.Length == 2);
            player.ShowControlsForSmoke();
            var panel = player.ShowMenuForSmoke(tracks: true);
            await CheckAsync(report, "TrackMenuOpened", () => player.HasOpenMenu && panel.ChoiceCount == 5, token);
            await player.DispatchSmokeTrackAsync(audio[1].Id, subtitle: false);
            await CheckAsync(report, "AudioMenuSelects", () => player.ViewModel.Snapshot.SelectedAudioTrackId == audio[1].Id && player.HasOpenMenu, token);
            await player.DispatchSmokeTrackAsync(subtitles[1].Id, subtitle: true);
            await CheckAsync(report, "SubtitleMenuSelects", () => player.ViewModel.Snapshot.SelectedSubtitleTrackId == subtitles[1].Id && player.HasOpenMenu, token);
            await ProbeSubtitleControlsAsync(player, panel, report, token);
            await player.DispatchSmokeTrackAsync(null, subtitle: true);
            await CheckAsync(report, "NoSubtitleAllowsStyleButDisablesDelay", () => panel.SubtitleControls.CanEditStyle &&
                !panel.SubtitleControls.CanAdjustDelay && player.HasOpenMenu, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "TracksEscapeOnlyClosesComponent", () => !player.HasOpenMenu && player.ViewModel.CanControl, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.C);
            await CheckAsync(report, "CStillSelectsSubtitleAfterEditing", () => player.ViewModel.Snapshot.SelectedSubtitleTrackId == subtitles[0].Id, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.V);
            await CheckAsync(report, "VStillSelectsAudioAfterEditing", () => player.ViewModel.Snapshot.SelectedAudioTrackId == audio[0].Id, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.F11);
            await WaitAsync(() => presentation.IsFullscreen, token);
            player.ShowControlsForSmoke();
            panel = player.ShowMenuForSmoke(tracks: true);
            await WaitAsync(() => player.HasOpenMenu && panel.SubtitleControls.CanEditStyle, token);
            panel.FindName("SubtitleDelayInput").As<TextBox>().Text = "0.5";
            await CheckAsync(report, "FullscreenSubtitleControlsApply", () => player.ViewModel.Snapshot.SubtitleDelaySeconds == .5 && player.HasOpenMenu, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "FullscreenEscapeClosesSubtitleComponentFirst", () => !player.HasOpenMenu && presentation.IsFullscreen, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "FullscreenEscapeLeavesVideoPlaying", () => !presentation.IsFullscreen && playback.Current is not null, token);
            report.Passed = true;
            report.Status = "Passed";
            report.Stage = "Complete";
        }
        catch (SmokeCheckException error)
        {
            report.Status = "Failed";
            report.Reason = "字幕控件检查未通过：" + error.Check;
        }
        catch (Exception error)
        {
            report.Status = "Failed";
            report.Reason = "字幕控件在 " + report.Stage + " 阶段中止：" + error.GetType().Name;
        }
        finally
        {
            try
            {
                presentation.ExitFullscreen();
                if (playback.Current is { } remaining) await remaining.CloseAsync(cancellationToken: CancellationToken.None);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await WaitAsync(() => playback.Current is null && window.Shell.ActivePlayer is null && !window.Shell.IsTransitioning, cleanup.Token);
                report.SessionClosed = true;
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
            await WaitAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, session) && active.ViewModel.CanControl && active.VideoSurface.IsDemoAttached, token);
            var player = window.Shell.ActivePlayer!;
            var fake = (FakePlaybackSession)session;
            report.SeriesEpisodeCount = session.Snapshot.Entries.Length;
            Mark(report, "SeriesEpisodeQueue", report.SeriesEpisodeCount >= 2 && player.ViewModel.HasEpisodes);
            player.ShowControlsForSmoke();
            await CheckAsync(report, "InitialPlayerFocus", () => IsFocusedWithin(player), token);
            var browsingEntry = navigation.Current;
            navigation.Navigate(originalRoute.Kind == PageKind.Home ? Route.Settings : Route.Home);
            Mark(report, "NavigationLocked", navigation.ForwardBlocked && ReferenceEquals(navigation.Current, browsingEntry) && !navigation.GoForward());
            Mark(report, "CoveredBrowseCannotReceiveFocus",
                FocusManager.FindFirstFocusableElement(window.Shell.FindName("BrowseFace").As<FrameworkElement>()) is null);

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
            player.Focus(FocusState.Programmatic);
            await Task.Delay(3200, token);
            await CheckAsync(report, "IdleChromeHides", () => !player.ControlsVisible, token);
            var volumeBeforeHint = player.ViewModel.Volume;
            await player.DispatchSmokeKeyAsync(volumeBeforeHint > 0 ? VirtualKey.Down : VirtualKey.Up);
            await CheckAsync(report, "KeyboardHintWithoutChrome", () => player.KeyHintVisible && !player.ControlsVisible &&
                player.ViewModel.Volume != volumeBeforeHint, token);
            await CheckAsync(report, "KeyboardHintExpires", () => !player.KeyHintVisible && !player.ControlsVisible, token);
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
            var hoverPosition = session.Snapshot.PositionTicks;
            player.DispatchSmokeSeekHover(.5);
            Mark(report, "SeekHoverPreviewsWithoutSeeking", player.SeekTipVisible &&
                Math.Abs(player.SeekTipSeconds - TimeSpan.FromTicks(session.Snapshot.DurationTicks).TotalSeconds / 2) < .01 &&
                session.Snapshot.PositionTicks == hoverPosition && Math.Abs(player.DisplayedSeekSeconds - 25) < .01);
            player.DispatchSmokeSeekHover(.9);
            player.DispatchSmokeSeekHover(.5);
            player.DispatchSmokeSeekHover(.5);
            var seekHost = player.FindName("SeekHost").As<Grid>();
            var seekTip = player.FindName("SeekTip").As<Border>();
            await CheckAsync(report, "SeekHoverTracksPointerAcrossMoves", () =>
            {
                var left = seekTip.TransformToVisual(seekHost).TransformPoint(new(0, 0)).X;
                return seekTip.ActualWidth > 0 && left <= seekHost.ActualWidth / 2 &&
                    left + seekTip.ActualWidth >= seekHost.ActualWidth / 2;
            }, token);
            player.DispatchSmokeSeekHover(1.5);
            Mark(report, "SeekHoverClampsAtEnd", Math.Abs(player.SeekTipSeconds -
                TimeSpan.FromTicks(session.Snapshot.DurationTicks).TotalSeconds) < .01 && session.Snapshot.PositionTicks == hoverPosition);
            player.DispatchSmokeSeekExit();
            Mark(report, "SeekHoverClearsOnExit", !player.SeekTipVisible && session.Snapshot.PositionTicks == hoverPosition);
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
            Mark(report, "RateMenuChoices", rateMenu.ChoiceCount == 6);
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
            Mark(report, "TrackMenuChoices", trackMenu.ChoiceCount == 5);
            await player.DispatchSmokeTrackAsync(audio[1].Id, subtitle: false);
            await CheckAsync(report, "AudioMenuSelects", () => player.ViewModel.Snapshot.SelectedAudioTrackId == audio[1].Id && player.HasOpenMenu, token);
            player.ShowMenuForSmoke(tracks: true);
            await WaitAsync(() => player.HasOpenMenu, token);
            await player.DispatchSmokeTrackAsync(subtitles[1].Id, subtitle: true);
            await CheckAsync(report, "SubtitleMenuSelects", () => player.ViewModel.Snapshot.SelectedSubtitleTrackId == subtitles[1].Id && player.HasOpenMenu, token);
            await ProbeSubtitleControlsAsync(player, trackMenu, report, token);
            player.ShowMenuForSmoke(tracks: true);
            await WaitAsync(() => player.HasOpenMenu, token);
            await player.DispatchSmokeTrackAsync(null, subtitle: true);
            await CheckAsync(report, "SubtitleMenuOff", () => player.ViewModel.Snapshot.SelectedSubtitleTrackId is null && player.HasOpenMenu, token);
            Mark(report, "NoSubtitleAllowsStyleButDisablesDelay", trackMenu.SubtitleControls.CanEditStyle && !trackMenu.SubtitleControls.CanAdjustDelay);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "TracksEscapeOnlyClosesComponent", () => !player.HasOpenMenu && player.ViewModel.CanControl, token);

            var preferences = window.Services.GetRequiredService<ISettingsService>();
            Mark(report, "EpisodePanelExpanded", player.EpisodePanelVisible);
            var expandedViewportWidth = player.ViewportElement.ActualWidth;
            player.ToggleEpisodesForSmoke();
            await player.FlushPreferencesAsync();
            await CheckAsync(report, "EpisodePanelCollapseExpandsVideo", () => !player.EpisodePanelVisible &&
                preferences.Current.EpisodePanelCollapsed && player.ViewportElement.ActualWidth > expandedViewportWidth + 180, token);
            player.ToggleEpisodesForSmoke();
            await player.FlushPreferencesAsync();
            await CheckAsync(report, "EpisodePanelRestoresVideo", () => player.EpisodePanelVisible &&
                !preferences.Current.EpisodePanelCollapsed && Math.Abs(player.ViewportElement.ActualWidth - expandedViewportWidth) < 1, token);
            // 标题栏上的开关走真实点击路径：带淡变，所以用轮询等退场结束。
            var episodesToggle = window.Shell.EpisodesToggle;
            Mark(report, "EpisodesToggleShown", episodesToggle.Visibility == Visibility.Visible &&
                AutomationProperties.GetName(episodesToggle) == "收起选集");
            new ButtonAutomationPeer(episodesToggle).Invoke();
            await player.FlushPreferencesAsync();
            await CheckAsync(report, "EpisodesToggleCollapses", () => !player.EpisodePanelVisible && preferences.Current.EpisodePanelCollapsed &&
                AutomationProperties.GetName(episodesToggle) == "展开选集" && player.ViewportElement.ActualWidth > expandedViewportWidth + 180, token);
            new ButtonAutomationPeer(episodesToggle).Invoke();
            await player.FlushPreferencesAsync();
            await CheckAsync(report, "EpisodesToggleExpands", () => player.EpisodePanelVisible && !preferences.Current.EpisodePanelCollapsed &&
                AutomationProperties.GetName(episodesToggle) == "收起选集" && Math.Abs(player.ViewportElement.ActualWidth - expandedViewportWidth) < 1, token);
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
            Mark(report, "FullscreenHidesEpisodePanel", !player.EpisodePanelVisible);
            player.ShowMenuForSmoke(tracks: false);
            await WaitAsync(() => player.HasOpenMenu, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "EscapeClosesMenuFirst", () => !player.HasOpenMenu && !player.EpisodePanelVisible && presentation.IsFullscreen && playback.Current is not null, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "EscapeLeavesFullscreenSecond", () => !presentation.IsFullscreen && player.EpisodePanelVisible && playback.Current is not null, token);
            await player.DispatchSmokeKeyAsync(VirtualKey.Escape);
            await CheckAsync(report, "EscapeClosesPlaybackLast", () => playback.Current is null && window.Shell.ActivePlayer is null &&
                !navigation.ForwardBlocked && !window.Shell.IsTransitioning, token);
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
            await WaitAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, layoutSession) && active.ViewModel.CanControl, token);
            var layoutOwner = window.Shell.ActivePlayer!;
            var layoutSettings = window.Services.GetRequiredService<ISettingsService>();
            var fakeDelayMs = window.Services.GetRequiredService<FakeOptions>().Delay.TotalMilliseconds;
            var queuedWrites = Enumerable.Range(0, Math.Clamp((int)Math.Ceiling(200 / Math.Max(1, fakeDelayMs)), 2, 24))
                .Select(_ => layoutSettings.UpdateAsync(settings => settings, token)).ToArray();
            layoutOwner.SetEpisodeGridForSmoke(false);
            layoutOwner.ToggleEpisodesForSmoke();
            Mark(report, "EpisodeLayoutWritePendingAtExternalClose", !layoutOwner.PendingPreferenceSave.IsCompleted);
            await layoutSession.CloseAsync(cancellationToken: token);
            var layoutReplacement = await playback.PreviewAsync(token);
            report.SessionsOpened++;
            await WaitAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true } active &&
                ReferenceEquals(active.Session, layoutReplacement) && active.ViewModel.CanControl, token);
            var nextLayoutOwner = window.Shell.ActivePlayer!;
            await Task.WhenAll(queuedWrites);
            Mark(report, "EpisodeLayoutExternalCloseReopen", !layoutSettings.Current.UseEpisodeGrid && !nextLayoutOwner.EpisodeGridVisible);
            Mark(report, "EpisodePanelExternalCloseReopen", layoutSettings.Current.EpisodePanelCollapsed && !nextLayoutOwner.EpisodePanelVisible);
            nextLayoutOwner.ToggleEpisodesForSmoke();
            await nextLayoutOwner.FlushPreferencesAsync();
            Mark(report, "EpisodePanelRaceRestoresExpanded", !layoutSettings.Current.EpisodePanelCollapsed && nextLayoutOwner.EpisodePanelVisible);
            await nextLayoutOwner.SetEpisodeGridForSmokeAsync(true);
            Mark(report, "EpisodeLayoutRaceRestoresGrid", layoutSettings.Current.UseEpisodeGrid && nextLayoutOwner.EpisodeGridVisible);
            await layoutReplacement.CloseAsync(cancellationToken: token);
            await WaitAsync(() => playback.Current is null && window.Shell.ActivePlayer is null && !navigation.ForwardBlocked && !window.Shell.IsTransitioning, token);

            await ProbeFoldInterruptionsAsync(window, playback, navigation, report, token);

            foreach (var close in new[] { "AltLeftCloses", "MouseBackCloses", "NavigatorBackCloses", "CloseButtonCloses" })
            {
                report.Stage = close;
                var auxiliary = await playback.PreviewAsync(token);
                report.SessionsOpened++;
                await WaitAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true } active &&
                    ReferenceEquals(active.Session, auxiliary) && active.ViewModel.CanControl, token);
                var active = window.Shell.ActivePlayer!;
                Mark(report, "EpisodeLayoutRememberedByNewOverlay", active.EpisodeGridVisible);
                if (close == "AltLeftCloses") await active.DispatchSmokeKeyAsync(VirtualKey.Left, alt: true);
                else if (close == "MouseBackCloses") await active.DispatchSmokeMouseButtonAsync(back: true);
                else if (close == "NavigatorBackCloses") Mark(report, "NavigatorBackIntercepted", navigation.GoBack());
                else
                {
                    await active.DispatchSmokeKeyAsync(VirtualKey.F11);
                    await WaitAsync(() => presentation.IsFullscreen, token);
                    await InvokeButtonAsync(active, "关闭播放", token);
                }
                await CheckAsync(report, close, () => playback.Current is null && window.Shell.ActivePlayer is null &&
                    !navigation.ForwardBlocked && !window.Shell.IsTransitioning, token);
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

    private static async Task ProbeFoldInterruptionsAsync(MainWindow window, IPlaybackService playback, Navigator navigation,
        PlayerControlsReport report, CancellationToken token)
    {
        var shell = window.Shell;
        foreach (var threshold in new[] { .15, .49, .51, .85 })
        {
            var label = "Fold" + ((int)(threshold * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            report.Stage = label;
            var interrupted = await playback.PreviewAsync(token);
            report.SessionsOpened++;
            await WaitAsync(() => shell.ActivePlayer is { IsLoaded: true } active && ReferenceEquals(active.Session, interrupted), token);
            if (Motion.AnimationsEnabled)
            {
                var deadline = Stopwatch.StartNew();
                // 给 SessionEnded 留出一帧；半程前用例必须真的发生在换面前。
                var observedThreshold = threshold == .49 ? .45 : threshold;
                while (shell.FoldProgress < observedThreshold)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException();
                    await UiLabSmoke.AwaitNextRenderingAsync(token);
                }
            }
            else await shell.PendingPresentation;
            var old = shell.ActivePlayer!;
            var surface = old.VideoSurface;
            var sample = new FoldInterruptionSample { RequestedProgress = threshold, CloseProgress = shell.FoldProgress };
            report.FoldInterruptions.Add(sample);
            if (Motion.AnimationsEnabled)
            {
                Mark(report, label + "InterruptsLiveOpening", shell.IsTransitioning && sample.CloseProgress is > 0 and < 1 &&
                    !surface.IsDemoAttached);
                Mark(report, label + "OpeningFaceRemainsReadable", ReadableFoldControls(old) &&
                    !old.IsClockRunning && !old.StateAnimationsRunning);
            }

            var releaseObserved = false;
            var retainedVisual = false;
            var readableClosingFace = false;
            void OnEnded(object? sender, PlaybackSessionEventArgs args)
            {
                if (!ReferenceEquals(args.Session, interrupted)) return;
                // Shell 的处理器先执行；这里观察的是其首个 await 前已经完成的解绑和冻结。
                releaseObserved = shell.ActivePlayer is null && old.IsPresentationFrozen &&
                    !old.HasAttachedSurface && !old.HasVideoSurface && !old.IsClockRunning &&
                    !old.IsSingleClickPending && !old.StateAnimationsRunning &&
                    !surface.IsDemoAttached && VisualTreeHelper.GetParent(surface) is null;
                retainedVisual = Motion.AnimationsEnabled
                    ? ReferenceEquals(shell.RetiringPlayer, old) && old.Content is not null && !old.IsHitTestVisible
                    : shell.RetiringPlayer is null;
                readableClosingFace = !Motion.AnimationsEnabled || ReadableFoldControls(old);
                sample.ReverseProgress = shell.FoldProgress;
            }
            playback.SessionEnded += OnEnded;
            try { await interrupted.CloseAsync(cancellationToken: token); }
            finally { playback.SessionEnded -= OnEnded; }
            Mark(report, label + "ReleasesSurfaceBeforeAwait", releaseObserved);
            Mark(report, label + "RetainsOnlyFrozenXaml", retainedVisual);
            Mark(report, label + "ClosingFaceRemainsReadable", readableClosingFace);
            if (Motion.AnimationsEnabled)
                Mark(report, label + "ReverseAvoidsEndpointJump",
                    sample.ReverseProgress is > 0 and < 1 && Math.Abs(sample.ReverseProgress - sample.CloseProgress) < .15);
            if (Motion.AnimationsEnabled && threshold == .49)
                Mark(report, label + "ReversesBeforeFacingChange", sample.ReverseProgress < .5 && !shell.IsPlayerFacing);
            if (Motion.AnimationsEnabled && threshold == .51)
                Mark(report, label + "ReversesAfterFacingChange", sample.ReverseProgress > .5 && shell.IsPlayerFacing);

            var replacement = await playback.PreviewAsync(token);
            report.SessionsOpened++;
            sample.ResumeProgress = shell.FoldProgress;
            await CheckAsync(report, label + "OnlyCurrentSessionAttaches", () => !shell.IsTransitioning &&
                shell.ActivePlayer is { IsLoaded: true } active && ReferenceEquals(active.Session, replacement) &&
                active.VideoSurface.IsDemoAttached && !surface.IsDemoAttached && shell.RetiringPlayer is null, token);
            Mark(report, label + "PlayerFaceOwnsTitle", shell.IsPlayerFacing &&
                shell.ViewModel.TitleText == shell.ActivePlayer!.ViewModel.ShellTitle);
            Mark(report, label + "FrozenCommandsIgnored", !await old.DispatchSmokeKeyAsync(VirtualKey.Space));
            if (threshold == .15)
            {
                var idlePlayer = shell.ActivePlayer!;
                await CheckAsync(report, "IdleBeforeFoldClose", () => !idlePlayer.ControlsVisible, token);
                var readableIdleClose = false;
                void OnIdleEnded(object? sender, PlaybackSessionEventArgs args)
                {
                    if (ReferenceEquals(args.Session, replacement))
                        readableIdleClose = idlePlayer.IsPresentationFrozen && !idlePlayer.HasVideoSurface &&
                            (!Motion.AnimationsEnabled || ReadableFoldControls(idlePlayer));
                }
                playback.SessionEnded += OnIdleEnded;
                try { await replacement.CloseAsync(cancellationToken: token); }
                finally { playback.SessionEnded -= OnIdleEnded; }
                Mark(report, "IdleCloseRetainsReadableXaml", readableIdleClose);
            }
            else
                await replacement.CloseAsync(cancellationToken: token);
            await CheckAsync(report, label + "RestoresBrowser", () => playback.Current is null &&
                shell.ActivePlayer is null && shell.RetiringPlayer is null && !shell.IsTransitioning && !shell.IsPlayerFacing &&
                !navigation.ForwardBlocked && shell.LastPlayerFocusRestoreSucceeded && shell.LastPlayerFocusRestoredWithinShell, token);
        }
    }

    private static bool ReadableFoldControls(PlayerOverlay player) =>
        player.ControlsVisible && !player.IsHitTestVisible &&
        player.FindName("Chrome") is FrameworkElement { Visibility: Visibility.Visible, Opacity: 1 } chrome &&
        chrome.ActualWidth > 0 && chrome.ActualHeight > 0 &&
        ElementCompositionPreview.GetElementVisual(chrome).Opacity >= .99f;

    private static async Task ProbeSnapshotPanelsAsync(MainWindow window, PlayerOverlay parent, PlayerControlsReport report, CancellationToken token)
    {
        var session = new SnapshotProbeSession();
        var overlay = new PlayerOverlay(session, window.Services.GetRequiredService<WindowContext>(),
            window.Services.GetRequiredService<ToastService>(), window.Services.GetRequiredService<ISettingsService>(),
            window.Services.GetRequiredService<IBulletChatService>());
        var host = (Grid)parent.FindName("Root");
        host.Children.Add(overlay);
        try
        {
            await WaitAsync(() => overlay.IsLoaded, token);
            await CheckAsync(report, "SlowOpeningPanel", () => overlay.OpeningPanelVisible && overlay.SlowOpeningVisible && !overlay.ViewModel.CanControl, token);
            Mark(report, "OpeningDisablesPlaybackKeys", !await overlay.DispatchSmokeKeyAsync(VirtualKey.Space));
        }
        finally
        {
            host.Children.Remove(overlay);
            overlay.Dispose();
            parent.Focus(FocusState.Programmatic);
        }
    }

    private static async Task ProbeSubtitleControlsAsync(PlayerOverlay player, PlayerChoicePanel panel, PlayerControlsReport report, CancellationToken token)
    {
        var editor = panel.SubtitleControls;
        var size = panel.FindName("SubtitleSizeInput").As<TextBox>();
        var outline = panel.FindName("SubtitleOutlineInput").As<TextBox>();
        var margin = panel.FindName("SubtitleMarginInput").As<TextBox>();
        var color = panel.FindName("SubtitleColorPicker").As<ColorPicker>();
        var delay = panel.FindName("SubtitleDelayInput").As<TextBox>();
        Mark(report, "SubtitleEditorsInExistingComponent", editor.IsVisible &&
            AutomationProperties.GetName(size) == "字幕字号" && AutomationProperties.GetName(outline) == "字幕描边粗细" &&
            AutomationProperties.GetName(margin) == "字幕底部距离" && AutomationProperties.GetName(color) == "字幕文字颜色");
        await CheckAsync(report, "AssStyleInitiallyPreserved", () => !editor.CanEditStyle && editor.CanOverrideAss && editor.StyleNote == "当前字幕使用自带样式", token);
        var toggle = panel.FindName("SubtitleOverrideToggle").As<CheckBox>();
        new ToggleButtonAutomationPeer(toggle).Toggle();
        await CheckAsync(report, "AssOverrideEnablesInlineStyle", () => player.ViewModel.Snapshot.SubtitleStyle.OverrideAssStyle && editor.CanEditStyle, token);
        size.Text = "48";
        outline.Text = "2.4";
        margin.Text = "50";
        color.Color = Windows.UI.Color.FromArgb(255, 255, 160, 64);
        await CheckAsync(report, "InlineStyleAppliesWithoutClosing", () => player.HasOpenMenu && player.ViewModel.Snapshot.SubtitleStyle is
            { FontSize: 48, OutlineSize: 2.4, BottomMargin: 50, TextColor: "#FFA040" }, token);
        delay.Text = "-0.3";
        await CheckAsync(report, "SubtitleEarlierDirection", () => player.ViewModel.Snapshot.SubtitleDelaySeconds == -.3, token);
        var originalPosition = player.ViewModel.Snapshot.PositionTicks;
        var originalTrack = player.ViewModel.Snapshot.SelectedSubtitleTrackId;
        delay.Focus(FocusState.Programmatic);
        Mark(report, "SubtitleTextOwnsPlaybackKeys", !await player.DispatchSmokeKeyAsync(VirtualKey.C) &&
            !await player.DispatchSmokeKeyAsync(VirtualKey.V) && !await player.DispatchSmokeKeyAsync(VirtualKey.Space) &&
            !await player.DispatchSmokeKeyAsync(VirtualKey.Left) && !await player.DispatchSmokeKeyAsync(VirtualKey.Right));
        Mark(report, "SubtitleTextDoesNotSeekOrSelect", player.ViewModel.Snapshot.PositionTicks == originalPosition &&
            player.ViewModel.Snapshot.SelectedSubtitleTrackId == originalTrack);
        delay.Text = "61";
        await Task.Delay(200, token);
        Mark(report, "InvalidSubtitleDelayStaysInline", editor.DelayError.Length > 0 && player.ViewModel.Snapshot.SubtitleDelaySeconds == -.3);
        delay.Text = "0.0";
        await CheckAsync(report, "SubtitleDelayReturnsToZero", () => player.ViewModel.Snapshot.SubtitleDelaySeconds == 0, token);
        await InvokeButtonAsync(panel, "恢复默认样式", token, byContent: true);
        await CheckAsync(report, "SubtitleResetOnlyRestoresStyle", () => player.ViewModel.Snapshot.SubtitleStyle == new SubtitleStyleSettings() &&
            player.ViewModel.Snapshot.SelectedSubtitleTrackId == originalTrack && player.HasOpenMenu, token);
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
    public List<FoldInterruptionSample> FoldInterruptions { get; set; } = [];
    public int SessionsOpened { get; set; }
    public bool SessionClosed { get; set; }
    public double ElapsedMilliseconds { get; set; }
}

internal sealed class FoldInterruptionSample
{
    public double RequestedProgress { get; set; }
    public double CloseProgress { get; set; }
    public double ReverseProgress { get; set; }
    public double ResumeProgress { get; set; }
}
