using System.Runtime.InteropServices;

namespace Mambo.App.Windowing;

/// <summary>
/// 自绘最大化按钮登记为非客户区 Maximize 区域后，点击以 HTMAXBUTTON 送达。
/// 吞掉这些消息，避免系统绘制经典按钮，由外壳自己切换最大化。
/// </summary>
internal sealed unsafe partial class NonClientHook : IDisposable
{
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmNcLButtonUp = 0x00A2;
    private const uint WmNcLButtonDblClk = 0x00A3;
    private const nuint HtMaxButton = 9;
    private const nuint SubclassId = 2;
    private readonly nint hwnd;
    private readonly GCHandle context;
    private bool disposed;

    public NonClientHook(nint hwnd)
    {
        this.hwnd = hwnd;
        context = GCHandle.Alloc(this);
        if (SetWindowSubclass(hwnd, &Procedure, SubclassId, (nuint)GCHandle.ToIntPtr(context)) == 0)
        {
            context.Free();
            throw new InvalidOperationException("无法挂接窗口标题栏。");
        }
    }

    public event Action<bool>? MaximizePressedChanged;
    public event Action? MaximizeClicked;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (wParam == HtMaxButton && message is WmNcLButtonDown or WmNcLButtonUp or WmNcLButtonDblClk)
        {
            var hook = (NonClientHook?)GCHandle.FromIntPtr((nint)data).Target;
            try
            {
                if (message == WmNcLButtonUp)
                {
                    hook?.MaximizePressedChanged?.Invoke(false);
                    hook?.MaximizeClicked?.Invoke();
                }
                else hook?.MaximizePressedChanged?.Invoke(true);
            }
            catch { /* 不能让托管异常跨越 Win32 回调边界。 */ }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (RemoveWindowSubclass(hwnd, &Procedure, SubclassId) != 0 || IsWindow(hwnd) == 0)
            context.Free();
    }

    [LibraryImport("comctl32")]
    private static partial int SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [LibraryImport("comctl32")]
    private static partial int RemoveWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> proc, nuint id);
    [LibraryImport("comctl32")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32")]
    private static partial int IsWindow(nint hwnd);
}
