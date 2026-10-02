using Mambo.Core.Contracts;

namespace Mambo.App.Shell;

/// <summary>
/// 统一的播放入口：启动中再次点击无效，同一项目正在播放时也无效；
/// 已有内容在播放时先确认替换。播放页本身在 P5 实现。
/// </summary>
public sealed class PlaybackLauncher(IPlaybackService playback, DialogService dialogs, ToastService toasts)
{
    public async Task PlayAsync(string itemId, long? startTicks = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        if (playback.IsStarting || dialogs.IsOpen) return;
        if (playback.Current?.Snapshot.Entry?.ItemId == itemId) return;
        var replace = false;
        if (playback.Current is not null)
        {
            if (!await dialogs.ConfirmAsync(new ConfirmRequest("切换播放？", "当前播放将结束并保存进度。", "切换", danger: true))) return;
            replace = true;
        }
        try
        {
            await playback.PlayAsync(new PlayRequest(itemId, startTicks, replace));
        }
        catch (AppException ex) when (ex.Error.Code is ErrorCodes.PlaybackBusy)
        {
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, "播放失败：" + ex.Error.Message, "重试", () => _ = PlayAsync(itemId, startTicks));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
