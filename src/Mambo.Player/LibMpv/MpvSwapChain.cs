using Microsoft.Win32.SafeHandles;

namespace Mambo.Player.LibMpv;

/// <summary>
/// display-swapchain 返回借用指针；在事件离开 native 线程前显式 AddRef，
/// 保证排队到 UI 线程期间仍有效。SwapChainPanel 绑定后持有自己的引用。
/// </summary>
public sealed class MpvSwapChain : SafeHandleZeroOrMinusOneIsInvalid
{
    internal unsafe MpvSwapChain(nint address) : base(true)
    {
        if (address != 0)
        {
            var addRef = (delegate* unmanaged[MemberFunction]<nint, uint>)(*(nint**)address)[1];
            addRef(address);
        }
        SetHandle(address);
    }
    public nint Address
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            return DangerousGetHandle();
        }
    }
    protected override unsafe bool ReleaseHandle()
    {
        var release = (delegate* unmanaged[MemberFunction]<nint, uint>)(*(nint**)handle)[2];
        release(handle);
        return true;
    }
}
