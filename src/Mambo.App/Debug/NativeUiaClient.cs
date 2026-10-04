using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Mambo.App.Debug;

/// <summary>只在已核验的父进程子树查询模式；不读取 Value/Text，不注入按键。</summary>
internal static unsafe partial class NativeUiaClient
{
    private static readonly Guid AutomationClass = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid AutomationInterface = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    private const int ValuePatternId = 10002;
    private const int TextPatternId = 10014;
    private const int AutomationIdPropertyId = 30011;
    private const int NamePropertyId = 30005;
    private const int ProcessIdPropertyId = 30002;
    private const int ControlTypePropertyId = 30003;
    private const int EditControlTypeId = 50004;
    private const int PatternNotSupported = unchecked((int)0x80040204);

    internal static NativeUiaResult Query(NativeUiaRequest request)
    {
        // 元数据依据：本机 Windows SDK UIAutomationClient.h 的 IUIAutomation /
        // IUIAutomationElement ABI。所有指针在同一 MTA 创建、使用并释放。
        var initialized = CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        if (initialized < 0) return new("NativeUiaApartmentFailed", []);
        nint automation = 0, windowElement = 0, element = 0;
        var conditions = new List<nint>(8);
        var keys = new List<nint>(2);
        var rawControlType = 0;
        var windowProcessMatched = false;
        var controlProcessMatched = false;
        AccessibilityBoundsReport? actualBounds = null;
        var lookupMode = "";
        NativeUiaResult Result(string status, string[]? patterns = null) => new(status, patterns ?? [],
            rawControlType, windowProcessMatched, controlProcessMatched, actualBounds, lookupMode);
        try
        {
            if (request.WindowHandle == 0 || request.Width <= 0 || request.Height <= 0)
                return Result("NativeUiaWindowNotReady");
            if (request.AutomationId.Length == 0 && request.Name.Length == 0)
                return Result("NativeUiaControlUnidentified");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in AutomationClass, 0, 1,
                in AutomationInterface, out automation)); // CLSCTX_INPROC_SERVER
            if (automation == 0) return Result("NativeUiaClientMissing");
            var automationTable = *(nint**)automation;
            var fromHandle = (delegate* unmanaged[MemberFunction]<nint, nint, nint*, int>)automationTable[6];
            Marshal.ThrowExceptionForHR(fromHandle(automation, (nint)request.WindowHandle, &windowElement));
            windowProcessMatched = windowElement != 0 && CurrentInt(windowElement, 20) == request.ParentProcessId;
            if (!windowProcessMatched) return Result("NativeUiaWindowScopeMismatch");

            // 不调用 desktop GetRootElement/ElementFromPoint/GetFocusedElement，不扫描其他应用。
            // 同名 Group/容器也能匹配 Name；必须先在条件中约束 Edit 与 owned PID。
            var process = CreateCondition(automation, ProcessIdPropertyId,
                new NativeVariant { Type = 3, Int32 = request.ParentProcessId }, conditions); // VT_I4
            var edit = CreateCondition(automation, ControlTypePropertyId,
                new NativeVariant { Type = 3, Int32 = EditControlTypeId }, conditions);
            var ownedEdit = AndCondition(automation, process, edit, conditions);
            var identity = CreateIdentityCondition(automation,
                request.AutomationId.Length > 0 ? AutomationIdPropertyId : NamePropertyId,
                request.AutomationId.Length > 0 ? request.AutomationId : request.Name, conditions, keys);
            var condition = AndCondition(automation, ownedEdit, identity, conditions);
            lookupMode = request.AutomationId.Length > 0 ? "AutomationIdAndEditAndOwnedProcess" : "NameAndEditAndOwnedProcess";
            var find = (delegate* unmanaged[MemberFunction]<nint, int, nint, nint*, int>)(*(nint**)windowElement)[5];
            Marshal.ThrowExceptionForHR(find(windowElement, 4, condition, &element)); // TreeScope_Descendants
            // 原生 rich text 子 provider 可能不继承 XAML Name 对应的 AutomationId；
            // 回退仍同时匹配可读 Name、Edit、PID，后面必须复核同一布局边界。
            if (element == 0 && request.AutomationId.Length > 0 && request.Name.Length > 0)
            {
                identity = CreateIdentityCondition(automation, NamePropertyId, request.Name, conditions, keys);
                condition = AndCondition(automation, ownedEdit, identity, conditions);
                lookupMode = "NameAndEditAndOwnedProcess";
                Marshal.ThrowExceptionForHR(find(windowElement, 4, condition, &element));
            }
            if (element == 0) return Result("NativeUiaControlMissing");
            controlProcessMatched = CurrentInt(element, 20) == request.ParentProcessId;
            rawControlType = CurrentInt(element, 21);
            if (!controlProcessMatched || rawControlType != EditControlTypeId)
                return Result("NativeUiaControlScopeMismatch");

            NativeRect bounds = default;
            var readBounds = (delegate* unmanaged[MemberFunction]<nint, NativeRect*, int>)(*(nint**)element)[43];
            Marshal.ThrowExceptionForHR(readBounds(element, &bounds));
            actualBounds = new() { Left = bounds.Left, Top = bounds.Top, Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top };
            if (!MatchesBounds(request, bounds)) return Result("NativeUiaControlBoundsMismatch");

            var supported = new List<string>(2);
            if (SupportsPattern(element, ValuePatternId)) supported.Add(nameof(PatternInterface.Value));
            if (request.IncludeText && SupportsPattern(element, TextPatternId)) supported.Add(nameof(PatternInterface.Text));
            return Result("Checked", supported.ToArray());
        }
        catch (Exception)
        {
            // 不输出 COM 异常中的本机路径、输入值或 provider 诊断信息。
            return Result("NativeUiaFailed");
        }
        finally
        {
            if (element != 0) Marshal.Release(element);
            for (var index = conditions.Count - 1; index >= 0; index--) Marshal.Release(conditions[index]);
            if (windowElement != 0) Marshal.Release(windowElement);
            if (automation != 0) Marshal.Release(automation);
            foreach (var key in keys) Marshal.FreeBSTR(key);
            CoUninitialize();
        }
    }

    internal static (int X, int Y) ClientOrigin(nint window)
    {
        NativePoint origin = default;
        if (ClientToScreen(window, ref origin) == 0) throw new InvalidOperationException("无法转换窗口客户端坐标。");
        return (origin.X, origin.Y);
    }

    private static nint CreateIdentityCondition(nint automation, int property, string identity,
        List<nint> conditions, List<nint> keys)
    {
        var key = Marshal.StringToBSTR(identity);
        keys.Add(key);
        return CreateCondition(automation, property, new NativeVariant { Type = 8, Pointer = key }, conditions);
    }

    private static nint CreateCondition(nint automation, int property, NativeVariant value, List<nint> conditions)
    {
        nint condition = 0;
        var create = (delegate* unmanaged[MemberFunction]<nint, int, NativeVariant, nint*, int>)(*(nint**)automation)[23];
        var result = create(automation, property, value, &condition);
        if (condition != 0) conditions.Add(condition);
        Marshal.ThrowExceptionForHR(result);
        if (condition == 0) throw new InvalidOperationException("原生自动化条件未创建。");
        return condition;
    }

    private static nint AndCondition(nint automation, nint left, nint right, List<nint> conditions)
    {
        nint condition = 0;
        var create = (delegate* unmanaged[MemberFunction]<nint, nint, nint, nint*, int>)(*(nint**)automation)[25];
        var result = create(automation, left, right, &condition);
        if (condition != 0) conditions.Add(condition);
        Marshal.ThrowExceptionForHR(result);
        if (condition == 0) throw new InvalidOperationException("原生自动化组合条件未创建。");
        return condition;
    }

    private static int CurrentInt(nint element, int slot)
    {
        int value = 0;
        var read = (delegate* unmanaged[MemberFunction]<nint, int*, int>)(*(nint**)element)[slot];
        Marshal.ThrowExceptionForHR(read(element, &value));
        return value;
    }

    private static bool SupportsPattern(nint element, int patternId)
    {
        nint pattern = 0;
        try
        {
            var get = (delegate* unmanaged[MemberFunction]<nint, int, nint*, int>)(*(nint**)element)[16];
            var result = get(element, patternId, &pattern);
            if (result == PatternNotSupported) return false;
            Marshal.ThrowExceptionForHR(result);
            // 仅证明客户端可以取得模式；不调用 Value getter、DocumentRange 或 GetText。
            return pattern != 0;
        }
        finally { if (pattern != 0) Marshal.Release(pattern); }
    }

    private static bool MatchesBounds(NativeUiaRequest request, NativeRect actual) =>
        Math.Abs(request.Left - actual.Left) <= 2 && Math.Abs(request.Top - actual.Top) <= 2
        && Math.Abs(request.Left + request.Width - actual.Right) <= 2
        && Math.Abs(request.Top + request.Height - actual.Bottom) <= 2;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct NativeVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
        [FieldOffset(8)] public int Int32;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [LibraryImport("user32.dll")]
    private static partial int ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartment);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context,
        in Guid interfaceId, out nint instance);
}
