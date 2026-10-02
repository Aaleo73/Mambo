using System.Globalization;
using Mambo.Player.LibMpv;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Video;

public enum HdrMode { Auto, Always, Off }

internal sealed class HdrController : IDisposable
{
    private readonly DisplayInformation display;
    private readonly DispatcherQueue queue;
    private MpvCore? player;
    private HdrMode mode;
    private bool disposed;
    public string Description { get; private set; } = "";
    public event Action? Changed;

    public HdrController(WindowId windowId, DispatcherQueue queue)
    {
        this.queue = queue;
        display = DisplayInformation.CreateForWindowId(windowId);
        display.AdvancedColorInfoChanged += ColorChanged;
        Apply();
    }

    public void SetMode(HdrMode value) { mode = value; Apply(); }
    public void Attach(MpvCore core) { player = core; Apply(); }
    public void Detach() { player = null; }
    private void ColorChanged(DisplayInformation sender, object args) =>
        queue.TryEnqueue(() => { if (!disposed) Apply(); });

    private void Apply()
    {
        var info = display.GetAdvancedColorInfo();
        var active = mode == HdrMode.Always ||
            (mode == HdrMode.Auto && info.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange);
        var peak = info.MaxLuminanceInNits > 0 ? info.MaxLuminanceInNits : 1000;
        var white = info.SdrWhiteLevelInNits > 0 ? info.SdrWhiteLevelInNits : 203;
        var contrast = info.MinLuminanceInNits > 0
            ? (peak / info.MinLuminanceInNits).ToString(CultureInfo.InvariantCulture) : "inf";
        player?.SetProperty("target-colorspace-hint", active ? "yes" : "no");
        player?.SetProperty("target-colorspace-hint-mode", "target");
        player?.SetProperty("target-trc", active ? "pq" : "auto");
        player?.SetProperty("target-prim", active ? "bt.2020" : "auto");
        player?.SetProperty("target-peak", active ? peak.ToString(CultureInfo.InvariantCulture) : "auto");
        player?.SetProperty("target-contrast", active ? contrast : "auto");
        player?.SetProperty("hdr-reference-white", active ? white.ToString(CultureInfo.InvariantCulture) : "auto");
        var supported = info.IsAdvancedColorKindAvailable(DisplayAdvancedColorKind.HighDynamicRange);
        Description = $"显示器：{info.CurrentAdvancedColorKind}；支持 HDR：{(supported ? "是" : "否")}；输出：{(active ? "HDR" : "SDR")}；峰值 {peak:F0} nit；SDR 白 {white:F0} nit";
        Changed?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        player = null;
        display.AdvancedColorInfoChanged -= ColorChanged;
        display.Dispose();
    }
}
