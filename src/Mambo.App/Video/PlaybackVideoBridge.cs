using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Mambo.Player.LibMpv;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Video;

/// <summary>引擎与面板的后端桥接；销毁引擎前异步等待 UI 解绑。</summary>
internal sealed class PlaybackVideoBridge : IDisposable
{
    private readonly VideoSurface surface;
    private readonly PlaybackSession session;
    private readonly DispatcherQueue queue;
    private readonly DispatcherQueueTimer retry;
    private LibMpvEngine? engine;
    private HdrController? hdr;
    private int bindingAttempts;
    private bool disposed;

    public PlaybackVideoBridge(VideoSurface surface, PlaybackSession session)
    {
        this.surface = surface; this.session = session; queue = surface.DispatcherQueue;
        retry = queue.CreateTimer(); retry.Interval = TimeSpan.FromMilliseconds(50);
        retry.Tick += OnRetry;
        session.EngineChanged += EngineChanged;
        session.Detaching += DetachAsync;
        surface.PixelSizeRequested += SizeRequested;
        if (session.Settings is { } settings) settings.Changed += SettingsChanged;
        EngineChanged(session.Engine);
    }
    private void OnRetry(DispatcherQueueTimer sender, object args) => TryBind();
    private void EngineChanged(IPlayerEngine? value)
    {
        if (!queue.HasThreadAccess) { queue.TryEnqueue(() => EngineChanged(value)); return; }
        if (disposed || !ReferenceEquals(session.Engine, value)) return;
        if (engine is { } previous) previous.SwapChainChanged -= SwapChainChanged;
        hdr?.Detach();
        engine = value as LibMpvEngine;
        bindingAttempts = 0;
        if (engine is null) { surface.ClearNativeSwapChain(); return; }
        engine.SwapChainChanged += SwapChainChanged;
        if (surface.XamlRoot?.ContentIslandEnvironment.AppWindowId is { Value: not 0 } windowId)
            hdr ??= new HdrController(windowId, queue);
        hdr?.Attach(engine.Core);
        SettingsChanged(this, EventArgs.Empty);
        var size = surface.PixelSize;
        engine.SetCompositionSize(size.Width, size.Height);
        TryBind(); retry.Start();
    }
    private void SwapChainChanged(MpvSwapChain reference)
    {
        // reference 由引擎持有；队列只使用当前引擎的最新指针，不保存旧引用。
        queue.TryEnqueue(() => { if (!disposed) { bindingAttempts = 0; TryBind(); retry.Start(); } });
    }
    private void TryBind()
    {
        if (disposed || engine is null) { retry.Stop(); return; }
        var reference = engine.CurrentSwapChain;
        if (reference is null || reference.IsInvalid) return;
        try
        {
            surface.Attach(reference.Address);
            retry.Stop();
        }
        catch (Exception) when (++bindingAttempts <= 60) { /* WinUI 面板可晚于交换链初始化完成。 */ }
        catch (Exception) { retry.Stop(); surface.ReportDiagnosticError("视频画面绑定失败，请重新打开播放。"); }
    }
    private void SizeRequested(int width, int height)
    {
        if (!disposed) engine?.SetCompositionSize(width, height);
    }
    private void SettingsChanged(object? sender, EventArgs args)
    {
        if (disposed || engine is null) return;
        var settings = session.Settings?.Current;
        if (settings is null) return;
        hdr?.SetMode((HdrMode)(int)settings.HdrMode);
        engine.Core.SetProperty("hwdec", settings.HardwareDecoding == HardwareDecodingMode.Off ? "no" : "d3d11va");
    }
    private Task DetachAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (queue.HasThreadAccess) DetachOnUi();
        else if (!queue.TryEnqueue(() =>
            DetachOnUi())) completion.TrySetException(new AppException(new(AppErrorKind.Player, ErrorCodes.PlaybackFailed, "界面线程已结束，无法解绑视频画面。", false)));
        return completion.Task;

        void DetachOnUi()
        {
            try
            {
                // RetryAsync 会复用会话；这里只释放旧引擎，继续订阅新引擎通知。
                ReleaseEngine();
                surface.ClearNativeSwapChain();
                completion.TrySetResult();
            }
            catch (Exception) { completion.TrySetException(new AppException(new(AppErrorKind.Player, ErrorCodes.PlaybackFailed, "视频画面解绑失败。", false))); }
        }
    }
    private void ReleaseEngine()
    {
        retry.Stop();
        if (engine is { } previous) previous.SwapChainChanged -= SwapChainChanged;
        hdr?.Detach();
        engine = null;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        retry.Stop(); session.EngineChanged -= EngineChanged; session.Detaching -= DetachAsync;
        retry.Tick -= OnRetry;
        surface.PixelSizeRequested -= SizeRequested;
        if (session.Settings is { } settings) settings.Changed -= SettingsChanged;
        ReleaseEngine();
        hdr?.Dispose(); hdr = null;
    }
}
