using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.App.BulletChat;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Hosting;
using Windows.System;
using Windows.UI;

namespace Mambo.App.Debug;

/// <summary>
/// 弹幕诊断：验证 Win2D 在当前构建（含 Native AOT）下可用，并用演示播放走一遍
/// 加载、滚动、暂停、跳转、开关、弹幕面板、倍速与轨道面板和关闭。只输出布尔值、计数、阶段和错误类型。
/// </summary>
internal static class BulletChatSmoke
{
    internal const string Argument = "--bullet-chat-smoke";

    internal static async Task RunAsync(MainWindow window, string reportPath)
    {
        var report = new BulletChatReport();
        // 刚发布的产物首次启动可能很慢（实测超过 90 秒），时限放宽，避免把慢启动误报成失败。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        IPlaybackSession? session = null;
        try
        {
            report.Stage = "等待外壳";
            await WaitAsync(() => window.Shell.IsLoaded && window.Shell.ActualWidth > 0, token);
            var services = window.Services;
            var settings = services.GetRequiredService<ISettingsService>();
            var bulletChat = services.GetRequiredService<IBulletChatService>();
            var hold = int.TryParse(Environment.GetEnvironmentVariable("MAMBO_BULLET_CHAT_HOLD_MS"), NumberStyles.None, CultureInfo.InvariantCulture, out var requested)
                && requested is > 0 and <= 20000 ? requested : 0;

            report.Stage = "离屏像素检查";
            CheckRasterizer(window, report);

            report.Stage = "演示播放";
            report.EnabledByDefault = settings.Current.BulletChat.Enabled;
            session = await services.GetRequiredService<IPlaybackService>().PreviewAsync(token);
            await WaitAsync(() => !window.Shell.IsTransitioning && window.Shell.ActivePlayer is { IsLoaded: true } && session.Snapshot.Phase == PlayerPhase.Playing, token);
            var player = window.Shell.ActivePlayer!;
            var layer = player.BulletChatLayer;

            report.Stage = "自动加载";
            await WaitAsync(() => bulletChat.Current.Status == BulletChatStatus.Loaded && bulletChat.Current.ItemId == session.Snapshot.Entry?.ItemId, token);
            report.CommentsLoaded = bulletChat.Current.Comments.Length;

            // 跳到高密度段，三种弹幕都会出现。
            report.Stage = "跳转重建";
            await session.SeekAsync(TimeSpan.FromSeconds(93), token);
            await WaitAsync(() => layer.IsRendering && layer.ActiveSprites >= 10 && Math.Abs(layer.ClockSeconds - 93) < 5, token);
            report.SeekRebuilt = true;
            report.SpritesAfterSeek = layer.ActiveSprites;
            report.BundledFontApplied = layer.UsesBundledFont;

            report.Stage = "时钟推进";
            await WaitAsync(() => layer.IsClockRunning, token);
            var before = layer.ClockSeconds;
            var spawned = layer.SpawnedTotal;
            await Task.Delay(1500, token);
            report.ClockAdvances = layer.ClockSeconds - before is > 1 and < 2.5;
            report.SpawnsWhilePlaying = layer.SpawnedTotal > spawned;

            if (hold > 0)
            {
                report.Stage = "停留供截图";
                Save(reportPath, report);
                await Task.Delay(hold, token);
            }

            report.Stage = "暂停与继续";
            await session.TogglePauseAsync(token);
            await WaitAsync(() => session.Snapshot.IsPaused && !layer.IsClockRunning, token);
            var paused = layer.ClockSeconds;
            await Task.Delay(400, token);
            report.PauseHolds = Math.Abs(layer.ClockSeconds - paused) < 0.001 && layer.ActiveSprites > 0;
            await session.TogglePauseAsync(token);
            await WaitAsync(() => !session.Snapshot.IsPaused && layer.IsClockRunning, token);
            await Task.Delay(400, token);
            // 继续时从暂停处接着走，没有被当成跳转重排。
            report.ResumeContinues = layer.ClockSeconds - paused is > 0.2 and < 1.5;

            report.Stage = "按键开关";
            report.KeyTurnsOff = await player.DispatchSmokeKeyAsync(VirtualKey.D);
            await WaitAsync(() => !settings.Current.BulletChat.Enabled && bulletChat.Current.Status == BulletChatStatus.Idle
                && !layer.IsRendering && layer.ActiveSprites == 0 && player.BulletChatButtonDimmed, token);
            report.KeyTurnsOff &= player.KeyHintText == "弹幕 关";
            report.KeyTurnsOn = await player.DispatchSmokeKeyAsync(VirtualKey.D);
            await WaitAsync(() => settings.Current.BulletChat.Enabled && bulletChat.Current.Status == BulletChatStatus.Loaded
                && layer.IsRendering && layer.ActiveSprites > 0 && !player.BulletChatButtonDimmed, token);
            report.KeyTurnsOn &= player.KeyHintText == "弹幕 开";

            report.Stage = "面板";
            player.ShowControlsForSmoke();
            player.ShowBulletChatPanelForSmoke();
            var panel = player.BulletChatPanelView;
            await WaitAsync(() => player.HasOpenMenu && panel.IsLoaded && panel.ViewModel is { StatusText.Length: > 0 }, token);
            var model = panel.ViewModel!;
            report.PanelShowsMatch = model.CanSearch && model.Enabled;
            model.OpacityPercent = 40;
            model.FontPercent = 130;
            await WaitAsync(() => Math.Abs(settings.Current.BulletChat.Opacity - 0.4) < 0.001 && Math.Abs(settings.Current.BulletChat.FontScale - 1.3) < 0.001, token);
            report.PanelSavesStyle = true;
            model.OpenSearch();
            await WaitAsync(() => model.IsSearchView && !model.IsBusy && model.Rows.Count > 0, token);
            await model.OpenAsync(model.Rows[0]);
            await WaitAsync(() => !model.IsBusy && model.HasHeading && model.Rows.Count > 0, token);
            if (hold > 0)
            {
                report.Stage = "面板停留供截图";
                Save(reportPath, report);
                await Task.Delay(hold, token);
            }
            await model.OpenAsync(model.Rows[2]);
            await WaitAsync(() => !player.HasOpenMenu && bulletChat.Current is { Status: BulletChatStatus.Loaded, Episode.Id: var id } && id.EndsWith("-3", StringComparison.Ordinal), token);
            report.PanelManualSelect = true;

            report.Stage = "倍速与轨道面板";
            player.ShowControlsForSmoke();
            var ratePanel = player.ShowMenuForSmoke(tracks: false);
            await WaitAsync(() => player.HasOpenMenu && ratePanel.IsLoaded && ratePanel.ChoiceCount > 0, token);
            report.RatePanelChoices = ratePanel.ChoiceCount;
            if (hold > 0)
            {
                report.Stage = "倍速面板停留供截图";
                Save(reportPath, report);
                await Task.Delay(hold, token);
            }
            report.RatePanelSelects = ratePanel.SelectedLabels == "正常" && ratePanel.ChooseForSmoke("1.5×");
            await WaitAsync(() => Math.Abs(session.Snapshot.PlaybackRate - 1.5) < .001 && !player.HasOpenMenu && ratePanel.ChoiceCount == 0, token);
            player.ShowControlsForSmoke();
            var tracksPanel = player.ShowMenuForSmoke(tracks: true);
            await WaitAsync(() => player.HasOpenMenu && tracksPanel.IsLoaded && tracksPanel.ChoiceCount > 0, token);
            report.TracksPanelChoices = tracksPanel.ChoiceCount;
            if (hold > 0)
            {
                report.Stage = "轨道面板停留供截图";
                Save(reportPath, report);
                await Task.Delay(hold, token);
            }
            report.TracksPanelSelects = session.Snapshot.SelectedSubtitleTrackId is not null && tracksPanel.ChooseForSmoke("关闭字幕");
            await WaitAsync(() => session.Snapshot.SelectedSubtitleTrackId is null && !player.HasOpenMenu, token);

            report.Stage = "关闭";
            await session.CloseAsync(token);
            await WaitAsync(() => window.Shell.ActivePlayer is null && window.Shell.RetiringPlayer is null && !window.Shell.IsTransitioning, token);
            report.ClosedCleanly = !layer.IsRendering && layer.ActiveSprites == 0 && bulletChat.Current.Status == BulletChatStatus.Idle;
            session = null;

            report.Passed = report.DeviceCreated && report.FillPixels > 50 && report.OutlinePixels > 50 && report.ClearPixels > 50
                && report.EmojiColorPixels > 50 && report.RatePanelChoices == 6 && report.RatePanelSelects
                && report.TracksPanelChoices >= 1 && report.TracksPanelSelects
                && report.EnabledByDefault && report.CommentsLoaded > 100 && report.SeekRebuilt && report.BundledFontApplied
                && report.ClockAdvances && report.SpawnsWhilePlaying && report.PauseHolds && report.ResumeContinues
                && report.KeyTurnsOff && report.KeyTurnsOn && report.PanelShowsMatch && report.PanelSavesStyle && report.PanelManualSelect && report.ClosedCleanly;
            report.Stage = report.Passed ? "完成" : "检查未通过";
        }
        catch (Exception error)
        {
            report.ErrorKind = error.GetType().Name;
            report.HResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
        }
        finally
        {
            if (!report.Passed) Environment.ExitCode = 1;
            Save(reportPath, report);
            try
            {
                if (session is { Snapshot.Phase: not PlayerPhase.Closed })
                    await session.CloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                await window.CloseForSmokeAsync().WaitAsync(TimeSpan.FromSeconds(12), CancellationToken.None);
                // 只在显式退出流程完成后才直接关闭，不依赖 AppWindow.Closing。
                if (window.HasCompletedShutdownForSmoke) window.Close();
            }
            catch (Exception error) when (error is TimeoutException or AppException) { Environment.ExitCode = 1; }
        }
    }

    // 不经过合成器，直接读回像素：描边、填充和透明区域都必须存在。
    private static void CheckRasterizer(MainWindow window, BulletChatReport report)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(window.Shell).Compositor;
        using var rasterizer = new BulletChatTextRasterizer(compositor);
        report.DeviceCreated = true;
        using var text = rasterizer.Measure("弹幕测试 Bullet Chat 0123", 25);
        using var target = new CanvasRenderTarget(rasterizer.Device, text.Size.X, text.Size.Y, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            rasterizer.Paint(session, text, 0xFFFFFF);
        }
        var pixels = target.GetPixelBytes();
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            byte blue = pixels[index], green = pixels[index + 1], red = pixels[index + 2], alpha = pixels[index + 3];
            if (alpha == 0) report.ClearPixels++;
            else if (alpha > 230 && blue > 230 && green > 230 && red > 230) report.FillPixels++;
            else if (alpha > 150 && blue < 50 && green < 50 && red < 50) report.OutlinePixels++;
        }

        // 表情用白色去画：出现有彩度的像素，才说明用上了彩色字形而不是单色轮廓。
        using var emoji = rasterizer.Measure("\U0001F600\U0001F389", 32);
        using var emojiTarget = new CanvasRenderTarget(rasterizer.Device, emoji.Size.X, emoji.Size.Y, 96);
        using (var session = emojiTarget.CreateDrawingSession())
        {
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            rasterizer.Paint(session, emoji, 0xFFFFFF);
        }
        var emojiPixels = emojiTarget.GetPixelBytes();
        for (var index = 0; index + 3 < emojiPixels.Length; index += 4)
        {
            int blue = emojiPixels[index], green = emojiPixels[index + 1], red = emojiPixels[index + 2];
            if (emojiPixels[index + 3] > 200 && Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 60) report.EmojiColorPixels++;
        }
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(25, token);
    }

    private static void Save(string path, BulletChatReport report)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, BulletChatSmokeJsonContext.Default.BulletChatReport));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class BulletChatReport
{
    public bool Passed { get; set; }
    public string Stage { get; set; } = "开始";
    public bool DeviceCreated { get; set; }
    public int FillPixels { get; set; }
    public int OutlinePixels { get; set; }
    public int ClearPixels { get; set; }
    public int EmojiColorPixels { get; set; }
    public int RatePanelChoices { get; set; }
    public bool RatePanelSelects { get; set; }
    public int TracksPanelChoices { get; set; }
    public bool TracksPanelSelects { get; set; }
    public bool EnabledByDefault { get; set; }
    public int CommentsLoaded { get; set; }
    public bool SeekRebuilt { get; set; }
    public int SpritesAfterSeek { get; set; }
    public bool BundledFontApplied { get; set; }
    public bool ClockAdvances { get; set; }
    public bool SpawnsWhilePlaying { get; set; }
    public bool PauseHolds { get; set; }
    public bool ResumeContinues { get; set; }
    public bool KeyTurnsOff { get; set; }
    public bool KeyTurnsOn { get; set; }
    public bool PanelShowsMatch { get; set; }
    public bool PanelSavesStyle { get; set; }
    public bool PanelManualSelect { get; set; }
    public bool ClosedCleanly { get; set; }
    public string ErrorKind { get; set; } = "";
    public string HResult { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BulletChatReport))]
internal sealed partial class BulletChatSmokeJsonContext : JsonSerializerContext;
