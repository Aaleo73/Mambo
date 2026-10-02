using System.Runtime.InteropServices;

namespace Mambo.App.Platform;

internal sealed unsafe partial class WindowResizeHook : IDisposable
{
    private readonly nint hwnd;
    private readonly GCHandle context;
    private readonly Action<bool> onResize;
    private bool disposed;
    public WindowResizeHook(nint hwnd, Action<bool> onResize)
    {
        this.hwnd = hwnd;
        this.onResize = onResize;
        context = GCHandle.Alloc(this);
        if (SetWindowSubclass(hwnd, &Procedure, 1, (nuint)GCHandle.ToIntPtr(context)) == 0)
        {
            context.Free();
            throw new InvalidOperationException("无法监听窗口缩放。");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        var hook = (WindowResizeHook?)GCHandle.FromIntPtr((nint)data).Target;
        try
        {
            if (message == 0x0231) hook?.onResize(true); // WM_ENTERSIZEMOVE
            if (message == 0x0232) hook?.onResize(false); // WM_EXITSIZEMOVE
        }
        catch { /* 不能让托管异常跨越 Win32 回调边界。 */ }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // 窗口销毁时系统已经移除 subclass；其它移除失败时保留 GCHandle，避免回调悬空。
        if (RemoveWindowSubclass(hwnd, &Procedure, 1) != 0 || IsWindow(hwnd) == 0)
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
