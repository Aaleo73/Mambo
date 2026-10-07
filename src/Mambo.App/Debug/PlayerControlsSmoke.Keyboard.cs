using Mambo.App.Shell;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class PlayerControlsSmoke
{
    private static async Task ProbeKeyboardInputAsync(MainWindow window, PlayerOverlay player,
        IPlaybackSession session, PlayerControlsReport report, CancellationToken token)
    {
        using var input = new UiInputProbe(window);
        await input.AcquireAsync(token);
        player.ShowControlsForSmoke();
        await session.SeekAsync(TimeSpan.FromSeconds(40), token);
        await WaitAsync(() => player.ViewModel.DisplayPositionTicks == session.Snapshot.PositionTicks, token);

        // Do not refocus before the first key: opening playback must leave it ready for input.
        await SeekKeyAsync("OpeningRightSeeksFiveSeconds", VirtualKey.Right, 45);
        await SeekKeyAsync("OpeningLeftSeeksFiveSeconds", VirtualKey.Left, 40);
        Mark(report, "PlayerCanTakeKeyboardFocus", player.Focus(FocusState.Programmatic));
        await SeekKeyAsync("PlayerRightSeeksFiveSeconds", VirtualKey.Right, 45);
        await SeekKeyAsync("PlayerLeftSeeksFiveSeconds", VirtualKey.Left, 40);

        var slider = player.FindName("SeekSlider").As<Slider>();
        Mark(report, "SeekSliderCanTakeKeyboardFocus", slider.Focus(FocusState.Keyboard));
        await SeekKeyAsync("FocusedSeekSliderRightSeeksFiveSeconds", VirtualKey.Right, 45);
        await SeekKeyAsync("FocusedSeekSliderLeftSeeksFiveSeconds", VirtualKey.Left, 40);
        await input.KeyAsync(VirtualKey.Right, token);
        await input.KeyAsync(VirtualKey.Right, token);
        await SeekKeyAsync("RepeatedKeysAccumulateWithoutDoubleSeek", VirtualKey.Right, 55);

        await input.KeyAsync(VirtualKey.Space, token);
        await CheckAsync(report, "FocusedSeekSliderSpaceResumes", () => !session.Snapshot.IsPaused, token);
        await input.KeyAsync(VirtualKey.Space, token);
        await CheckAsync(report, "FocusedSeekSliderSpacePauses", () => session.Snapshot.IsPaused, token);
        await SetPositionAsync(1);
        await SeekKeyAsync("KeyboardSeekClampsAtStart", VirtualKey.Left, 0);
        var duration = TimeSpan.FromTicks(session.Snapshot.DurationTicks).TotalSeconds;
        await SetPositionAsync(duration - 1);
        await SeekKeyAsync("KeyboardSeekClampsAtEnd", VirtualKey.Right, duration);
        await SetPositionAsync(40);

        var pause = player.FindName("PauseButton").As<Button>();
        Mark(report, "ToolbarCanTakeKeyboardFocus", pause.Focus(FocusState.Keyboard));
        await SeekKeyAsync("ToolbarRightSeeksFiveSeconds", VirtualKey.Right, 45);
        await SeekKeyAsync("ToolbarLeftSeeksFiveSeconds", VirtualKey.Left, 40);

        var maximize = window.Shell.FindName("MaximizeButton").As<Button>();
        Mark(report, "TitlebarCanTakeKeyboardFocus", maximize.Focus(FocusState.Keyboard));
        await SeekKeyAsync("TitlebarRightSeeksFiveSeconds", VirtualKey.Right, 45);
        await SeekKeyAsync("TitlebarLeftSeeksFiveSeconds", VirtualKey.Left, 40);

        await session.SetVolumeAsync(50, token);
        await WaitAsync(() => player.ViewModel.Volume == 50, token);
        var mute = player.FindName("MuteButton").As<Button>();
        Mark(report, "MuteCanTakeKeyboardFocus", mute.Focus(FocusState.Keyboard));
        var volume = player.FindName("VolumeSlider").As<Slider>();
        await UiLabSmoke.AwaitNextRenderingAsync(token);
        await input.KeyAsync(VirtualKey.Tab, token);
        Mark(report, "TabMovesFromMuteToVolume", ReferenceEquals(FocusManager.GetFocusedElement(player.XamlRoot), volume));
        await input.KeyAsync(VirtualKey.Right, token);
        await CheckAsync(report, "VolumeRightChangesVolumeWithoutSeeking", () => session.Snapshot.Volume == 55 && At(session, 40), token);
        await input.KeyAsync(VirtualKey.Left, token);
        await CheckAsync(report, "VolumeLeftChangesVolumeWithoutSeeking", () => session.Snapshot.Volume == 50 && At(session, 40), token);

        var panel = player.ShowMenuForSmoke(tracks: true);
        await WaitAsync(() => player.HasOpenMenu && panel.SubtitleControls.CanAdjustDelay, token);
        var delay = panel.FindName("SubtitleDelayInput").As<TextBox>();
        Mark(report, "SubtitleEditorCanTakeKeyboardFocus", delay.Focus(FocusState.Keyboard));
        delay.Select(1, 0);
        await input.KeyAsync(VirtualKey.Right, token);
        await CheckAsync(report, "SubtitleRightMovesCaretWithoutSeeking", () => delay.SelectionStart == 2 && At(session, 40), token);
        await input.KeyAsync(VirtualKey.Left, token);
        await CheckAsync(report, "SubtitleLeftMovesCaretWithoutSeeking", () => delay.SelectionStart == 1 && At(session, 40), token);
        await input.KeyAsync(VirtualKey.Escape, token);
        await WaitAsync(() => !player.HasOpenMenu, token);
        await SeekKeyAsync("RightSeeksAfterClosingSubtitleEditor", VirtualKey.Right, 45);

        var presentation = window.Services.GetRequiredService<WindowContext>();
        await input.KeyAsync(VirtualKey.F11, token);
        await WaitAsync(() => presentation.IsFullscreen, token);
        player.ShowControlsForSmoke();
        Mark(report, "FullscreenSeekSliderCanTakeKeyboardFocus", slider.Focus(FocusState.Keyboard));
        await SeekKeyAsync("FullscreenSeekSliderLeftSeeksFiveSeconds", VirtualKey.Left, 40);
        await SeekKeyAsync("FullscreenSeekSliderRightSeeksFiveSeconds", VirtualKey.Right, 45);
        await input.KeyAsync(VirtualKey.F11, token);
        await WaitAsync(() => !presentation.IsFullscreen, token);

        player.Focus(FocusState.Programmatic);
        await SetPositionAsync(40);

        async Task SetPositionAsync(double seconds)
        {
            await player.DispatchSmokeSeekAsync(seconds);
            await WaitAsync(() => At(session, seconds) &&
                Math.Abs(player.ViewModel.PositionSeconds - seconds) < .01, token);
        }

        async Task SeekKeyAsync(string name, VirtualKey key, double expected)
        {
            report.Stage = name;
            await input.KeyAsync(key, token);
            await CheckAsync(report, name, () => At(session, expected), token);
        }
    }
}
