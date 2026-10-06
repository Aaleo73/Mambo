using System.Runtime.InteropServices;

namespace Mambo.App.Windowing;

/// <summary>
/// 把嵌在 exe 里的应用图标交给窗口。标题栏是自绘的，不显示它；
/// 任务栏、Alt+Tab 和任务视图取的是窗口图标。
/// </summary>
internal static partial class WindowIcon
{
    private const uint WmSetIcon = 0x0080;
    private const nuint IconSmall = 0;
    private const nuint IconBig = 1;
    private const int SmCxIcon = 11;
    private const int SmCxSmIcon = 49;
    // 编译器给 ApplicationIcon 分配的资源号。
    private const nint ApplicationIconId = 32512;

    public static void Apply(nint hwnd)
    {
        var module = GetModuleHandleW(0);
        var dpi = GetDpiForWindow(hwnd);
        Set(hwnd, module, IconBig, GetSystemMetricsForDpi(SmCxIcon, dpi));
        Set(hwnd, module, IconSmall, GetSystemMetricsForDpi(SmCxSmIcon, dpi));
    }

    private static void Set(nint hwnd, nint module, nuint kind, int size)
    {
        // 两种尺寸各取一份，不用 LR_SHARED：共享缓存不区分尺寸。句柄随主窗口用到进程结束。
        var icon = LoadImageW(module, ApplicationIconId, 1, size, size, 0);
        if (icon != 0) SendMessageW(hwnd, WmSetIcon, kind, icon);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint name);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial nint LoadImageW(nint module, nint name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
}
