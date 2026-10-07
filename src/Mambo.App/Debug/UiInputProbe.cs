using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;

namespace Mambo.App.Debug;

/// <summary>
/// Real, UI-thread-only input for the supplied lab window. Callers must serialize operations
/// and dispose the probe in a finally/using scope, including when cancelling a drag.
/// </summary>
internal sealed partial class UiInputProbe : IDisposable
{
    private const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004;
    private const uint VirtualDesktop = 0x4000, Absolute = 0x8000;
    private readonly MainWindow window;
    private readonly nint hwnd;
    private NativePoint originalCursor, injectedCursor;
    private uint injectedTime;
    private bool acquired, moved, leftDown, disposed;

    /// <summary>Who held the foreground or covered the target when the last input was refused.</summary>
    internal string LastInterference { get; private set; } = "";

    internal UiInputProbe(MainWindow window)
    {
        this.window = window;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    internal bool IsForeground => !disposed && OwnsWindow() && GetForegroundWindow() == hwnd;

    /// <summary>
    /// Null while this window is foreground. Otherwise only the owning image name and window
    /// class of whatever holds the foreground; never its title or content.
    /// </summary>
    internal string? DescribeForegroundHolder()
    {
        var foreground = GetForegroundWindow();
        return foreground == hwnd ? null : DescribeWindow(foreground);
    }

    private static unsafe string DescribeWindow(nint target)
    {
        if (target == 0) return "none";
        var owner = "unknown";
        if (GetWindowThreadProcessId(target, out var process) != 0)
        {
            if (process == Environment.ProcessId) owner = "self";
            else
            {
                try
                {
                    using var other = Process.GetProcessById((int)process);
                    owner = other.ProcessName;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception) { }
            }
        }
        var name = stackalloc char[128];
        var length = GetClassName(target, name, 128);
        return length > 0 ? $"{owner}/{new string(name, 0, length)}" : owner;
    }

    internal async Task AcquireAsync(CancellationToken token)
    {
        try
        {
            EnsureThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (!OwnsWindow()) throw Failure("UiInputWindowUnavailable");
            // The app manifest is PerMonitorV2. Refuse virtualized coordinates rather than
            // changing the process/thread's DPI mode or guessing a second scale factor.
            if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) != 2)
                throw Failure("UiInputDpiContextUnsupported");
            if (!acquired && GetCursorPos(out originalCursor) == 0)
                throw Failure("UiInputCursorUnavailable");
            window.Activate();
            await UiLabSmoke.AwaitNextRenderingAsync(token);
            if (!IsForeground)
            {
                // Activation can be denied while this owned window is behind another app.
                // Raise only its ordinary Z-order; never change topmost or foreground-lock policy.
                window.AppWindow.MoveInZOrderAtTop();
                await UiLabSmoke.AwaitNextRenderingAsync(token);
                if (!IsForeground)
                {
                    var target = (FrameworkElement)window.Shell.Sidebar.FindName("SearchBox");
                    SendPointer(ToScreen(target, new Point(target.ActualWidth / 2, target.ActualHeight / 2)),
                        LeftDown | LeftUp, token, activating: true);
                }
            }
            var activation = Stopwatch.GetTimestamp();
            do
            {
                await UiLabSmoke.AwaitNextRenderingAsync(token);
            } while (!IsForeground && Stopwatch.GetElapsedTime(activation) < TimeSpan.FromSeconds(2));
            if (!IsForeground)
            {
                LastInterference = DescribeForegroundHolder() ?? "";
                throw Failure("UiInputForegroundUnavailable");
            }
            acquired = true;
        }
        catch
        {
            ReleaseOwnedLeft();
            throw;
        }
    }

    internal async Task KeyAsync(VirtualKey key, CancellationToken token)
    {
        try
        {
            EnsureReady(token);
            if ((uint)key is 0 or > 255 || key is VirtualKey.LeftButton or VirtualKey.RightButton
                or VirtualKey.MiddleButton or VirtualKey.XButton1 or VirtualKey.XButton2)
                throw Failure("UiInputKeyUnsupported");
            // Do not complete or alter a user's existing chord/key press.
            if (IsDown((int)key) || IsDown(0x10) || IsDown(0x11) || IsDown(0x12)
                || IsDown(0x5B) || IsDown(0x5C))
                throw Failure("UiInputKeyAlreadyHeld");
            SendKeyPair((ushort)key);
            await UiLabSmoke.AwaitNextRenderingAsync(token);
            EnsureReady(token);
        }
        catch
        {
            ReleaseOwnedLeft();
            throw;
        }
    }

    internal Task MoveAsync(FrameworkElement relativeTo, Point point, CancellationToken token) =>
        PointerAsync(relativeTo, point, 0, token);

    internal Task ClickAsync(FrameworkElement relativeTo, Point point, CancellationToken token) =>
        PointerAsync(relativeTo, point, LeftDown | LeftUp, token);

    internal Task PressLeftAsync(FrameworkElement relativeTo, Point point, CancellationToken token) =>
        PointerAsync(relativeTo, point, LeftDown, token);

    internal async Task ReleaseLeftAsync(CancellationToken token)
    {
        try
        {
            EnsureReady(token);
            ReleaseOwnedLeft();
            await UiLabSmoke.AwaitNextRenderingAsync(token);
            EnsureReady(token);
        }
        catch
        {
            ReleaseOwnedLeft();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        EnsureThread();
        // Cleanup may release our held button after focus loss; it must never inject
        // a new press or re-activate a window.
        ReleaseOwnedLeft();
        try
        {
            if (acquired && moved && IsForeground && GetCursorPos(out var current) != 0
                && current.X == injectedCursor.X && current.Y == injectedCursor.Y
                && LastInputWasOurMove())
            {
                // Any intervening input (even our own keyboard input) conservatively
                // forfeits cursor restoration. No previous foreground window is restored.
                SendCursorRestore();
            }
        }
        finally { disposed = true; }
    }

    private async Task PointerAsync(FrameworkElement relativeTo, Point point, uint buttons,
        CancellationToken token)
    {
        try
        {
            EnsureReady(token);
            if (buttons != 0 && (leftDown || IsDown(0x01)))
                throw Failure("UiInputButtonAlreadyHeld");
            var screen = ToScreen(relativeTo, point);
            SendPointer(screen, buttons, token);
            await UiLabSmoke.AwaitNextRenderingAsync(token);
            EnsureReady(token);
        }
        catch
        {
            ReleaseOwnedLeft();
            throw;
        }
    }

    private NativePoint ToScreen(FrameworkElement element, Point point)
    {
        if (window.Content is not FrameworkElement root || !element.IsLoaded
            || element.XamlRoot is null || element.XamlRoot != root.XamlRoot
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw Failure("UiInputTargetUnavailable");
        var position = element.TransformToVisual(root).TransformPoint(point);
        var scale = root.XamlRoot.RasterizationScale;
        var x = position.X * scale;
        var y = position.Y * scale;
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || GetClientRect(hwnd, out var client) == 0
            || x < client.Left || y < client.Top || x >= client.Right || y >= client.Bottom)
            throw Failure("UiInputTargetOutsideWindow");
        var screen = new NativePoint { X = (int)Math.Floor(x), Y = (int)Math.Floor(y) };
        if (ClientToScreen(hwnd, ref screen) == 0) throw Failure("UiInputCoordinateConversionFailed");
        return screen;
    }

    private unsafe void SendPointer(NativePoint screen, uint buttons, CancellationToken token, bool activating = false)
    {
        var move = MouseAt(screen);
        var inputs = stackalloc NativeInput[3];
        inputs[0] = move;
        var count = 1u;
        if ((buttons & LeftDown) != 0) inputs[count++] = MouseButton(LeftDown, move.Data.Mouse.Time);
        if ((buttons & LeftUp) != 0) inputs[count++] = MouseButton(LeftUp, move.Data.Mouse.Time);
        if (activating)
        {
            EnsureThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (acquired || !OwnsWindow() || buttons != (LeftDown | LeftUp))
                throw Failure("UiInputActivationUnavailable");
            // Do not redirect a user's ongoing gesture or keystroke into this window.
            for (var key = 1; key <= 255; key++)
                if (IsDown(key)) throw Failure("UiInputKeyAlreadyHeld");
        }
        else EnsureReady(token);
        // A foreground window can still be covered by another app's topmost window.
        // Permit only this window or its own same-process popup surface under the point.
        var target = WindowFromPoint(screen);
        if (target == 0) throw Failure("UiInputTargetUnavailable");
        if (GetWindowThreadProcessId(target, out var process) == 0 || process != Environment.ProcessId)
        {
            LastInterference = DescribeWindow(target);
            throw Failure("UiInputTargetOccluded");
        }
        if (GetAncestor(target, 3) != hwnd) throw Failure("UiInputTargetScopeMismatch");
        var inserted = SendInput(count, inputs, sizeof(NativeInput));
        if (inserted >= 1)
        {
            moved = true;
            injectedCursor = screen;
            injectedTime = move.Data.Mouse.Time;
        }
        if (buttons != 0 && inserted >= 2) leftDown = inserted < 3;
        if (inserted != count) throw Failure("UiInputInsertionFailed");
    }

    private unsafe void SendKeyPair(ushort key)
    {
        var scan = MapVirtualKey(key, 4);
        var down = new NativeInput
        {
            Type = 1,
            Data = new NativeInputUnion
            {
                Keyboard = new NativeKeyboardInput
                {
                    VirtualKey = key,
                    ScanCode = (ushort)(scan & 0xFF),
                    Flags = (scan & 0xFF00) == 0xE000 ? 1u : 0u,
                },
            },
        };
        var inputs = stackalloc NativeInput[2];
        inputs[0] = down;
        inputs[1] = down;
        inputs[1].Data.Keyboard.Flags |= 2;
        if (!IsForeground)
        {
            LastInterference = DescribeForegroundHolder() ?? "";
            throw Failure("UiInputForegroundUnavailable");
        }
        var inserted = SendInput(2, inputs, sizeof(NativeInput));
        if (inserted == 1)
        {
            // As with mouse cleanup, only release a press that this probe inserted.
            if (SendInput(1, inputs + 1, sizeof(NativeInput)) != 1
                && SendInput(1, inputs + 1, sizeof(NativeInput)) != 1)
                throw Failure("UiInputKeyReleaseFailed");
        }
        if (inserted != 2) throw Failure("UiInputInsertionFailed");
    }

    private unsafe void ReleaseOwnedLeft()
    {
        if (!leftDown) return;
        var release = MouseButton(LeftUp, 0);
        if (SendInput(1, &release, sizeof(NativeInput)) != 1
            && SendInput(1, &release, sizeof(NativeInput)) != 1)
            throw Failure("UiInputButtonReleaseFailed");
        leftDown = false;
    }

    private unsafe void SendCursorRestore()
    {
        var restore = MouseAt(originalCursor);
        if (!IsForeground || GetCursorPos(out var current) == 0
            || current.X != injectedCursor.X || current.Y != injectedCursor.Y || !LastInputWasOurMove()) return;
        if (SendInput(1, &restore, sizeof(NativeInput)) != 1)
            throw Failure("UiInputCursorRestoreFailed");
    }

    private static NativeInput MouseAt(NativePoint screen)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        var x = (long)screen.X - left;
        var y = (long)screen.Y - top;
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height)
            throw Failure("UiInputDesktopPointUnavailable");
        var input = MouseButton(MouseMove | VirtualDesktop | Absolute, GetTickCount());
        // Address each physical pixel's center in the virtual desktop's 0..65535 space,
        // including monitors left/above the primary. No primary-screen normalization.
        input.Data.Mouse.X = (int)Math.Min(65535, (2 * x + 1) * 65536 / (2L * width));
        input.Data.Mouse.Y = (int)Math.Min(65535, (2 * y + 1) * 65536 / (2L * height));
        return input;
    }

    private static NativeInput MouseButton(uint flags, uint time) => new()
    {
        Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = flags, Time = time } },
    };

    private bool LastInputWasOurMove()
    {
        var info = new NativeLastInputInfo { Size = (uint)Marshal.SizeOf<NativeLastInputInfo>() };
        return GetLastInputInfo(ref info) != 0 && info.Time == injectedTime;
    }

    private bool OwnsWindow() => hwnd != 0 && IsWindow(hwnd) != 0
        && GetWindowThreadProcessId(hwnd, out var process) != 0 && process == Environment.ProcessId;

    private void EnsureReady(CancellationToken token)
    {
        EnsureThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (acquired && IsForeground) return;
        LastInterference = DescribeForegroundHolder() ?? "";
        throw Failure("UiInputForegroundUnavailable");
    }

    private void EnsureThread()
    {
        if (!window.DispatcherQueue.HasThreadAccess) throw Failure("UiInputRequiresUiThread");
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static InputFailure Failure(string code) => new(code);

    internal sealed class InputFailure(string code) : InvalidOperationException(code);

    // Native INPUT is 40 bytes on x64 / 28 on x86: the union's pointer-sized
    // mouse member supplies native alignment. Do not force a packed or 32-bit union.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public NativeInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
        [FieldOffset(0)] public NativeHardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHardwareInput { public uint Message; public ushort Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLastInputInfo { public uint Size, Time; }

    [LibraryImport("user32.dll")]
    private static unsafe partial uint SendInput(uint count, NativeInput* inputs, int size);
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial int IsWindow(nint window);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("user32.dll")]
    private static partial int GetCursorPos(out NativePoint point);
    [LibraryImport("user32.dll")]
    private static partial int ClientToScreen(nint window, ref NativePoint point);
    [LibraryImport("user32.dll")]
    private static partial int GetClientRect(nint window, out NativeRect rect);
    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);
    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint window, char* name, int capacity);
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint type);
    [LibraryImport("user32.dll")]
    private static partial int GetLastInputInfo(ref NativeLastInputInfo info);
    [LibraryImport("user32.dll")]
    private static partial nint GetThreadDpiAwarenessContext();
    [LibraryImport("user32.dll")]
    private static partial int GetAwarenessFromDpiAwarenessContext(nint context);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetTickCount();
}
