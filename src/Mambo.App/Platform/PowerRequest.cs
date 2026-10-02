using System.Runtime.InteropServices;

namespace Mambo.App.Platform;

/// <summary>必须在同一个 UI 线程更新和释放线程执行状态。</summary>
internal sealed partial class PowerRequest : IDisposable
{
    private bool active;
    public void SetPlaying(bool playing)
    {
        if (active == playing) return;
        var flags = playing ? 0x80000003u : 0x80000000u;
        if (SetThreadExecutionState(flags) == 0) return;
        active = playing;
    }
    public void Dispose() => SetPlaying(false);
    [LibraryImport("kernel32")]
    private static partial uint SetThreadExecutionState(uint flags);
}
