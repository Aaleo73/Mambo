using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Debug;

/// <summary>检查当前假数据页面的真实自动化树；不模拟或宣称已完成讲述人听觉验收。</summary>
internal static partial class AccessibilityProbe
{
    private static readonly PatternInterface[] Patterns =
    [PatternInterface.Invoke, PatternInterface.Toggle, PatternInterface.SelectionItem,
        PatternInterface.Value, PatternInterface.Text, PatternInterface.RangeValue, PatternInterface.ExpandCollapse, PatternInterface.Selection];

    internal static async Task<AccessibilityReport> RunAsync(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("无障碍探针必须在窗口 UI 线程运行。");
        // 在读取任何页面或 Name 前拒绝真实服务，报告只包含本地假页面的名称。
        if (window.Services.GetRequiredService<IPlaybackService>() is not FakePlaybackService
            || window.Services.GetRequiredService<ILibraryService>() is not FakeLibraryService
            || window.Services.GetRequiredService<ISessionService>() is not FakeSessionService
            || window.Services.GetRequiredService<ISettingsService>() is not FakeSettingsService
            || window.Services.GetRequiredService<ILibraryPreferences>() is not FakeLibraryPreferences
            || window.Services.GetRequiredService<IImageService>() is not FakeImageService)
            return new() { Status = "Unsupported", Scope = "RealBackendRejected" };
        if (!window.Shell.IsLoaded || window.Shell.XamlRoot is null)
            return new() { Status = "NotReady", Scope = "WindowNotLoaded" };

        var theme = ThemeSettings.CreateForWindowId(window.AppWindow.Id);
        var report = new AccessibilityReport
        {
            Scope = window.Shell.ActivePlayer is null ? "Shell" : "Player",
            SystemHighContrast = theme.HighContrast,
            HighContrastScheme = theme.HighContrast ? theme.HighContrastScheme : "",
        };
        FrameworkElement root = window.Shell.ActivePlayer is { } player ? player : window.Shell;
        var originalFocus = FocusManager.GetFocusedElement(window.Shell.XamlRoot) as Control;
        try
        {
            // 在焦点变更前固定当前布局；不展开折叠的抽屉、菜单或其他页面。
            var controls = VisibleControls(root).ToArray();
            foreach (var control in controls)
            {
                var item = new AccessibilityControlReport
                {
                    ElementName = control.Name,
                    ClassName = control.GetType().Name,
                    Enabled = control.IsEnabled,
                    IsTabStop = control.IsTabStop,
                    TabIndex = control.TabIndex,
                };
                report.Controls.Add(item);
                try
                {
                    var peer = FrameworkElementAutomationPeer.FromElement(control)
                        ?? FrameworkElementAutomationPeer.CreatePeerForElement(control);
                    if (peer is null) { item.Error = "MissingPeer"; continue; }
                    if (peer.IsOffscreen()) { report.Controls.Remove(item); continue; }
                    item.Name = peer.GetName();
                    item.ControlType = peer.GetAutomationControlType().ToString();
                    item.KeyboardFocusable = peer.IsKeyboardFocusable();
                    item.Patterns = Patterns.Where(pattern => peer.GetPattern(pattern) is not null)
                        .Select(pattern => pattern.ToString()).ToArray();
                    item.PatternProbeSource = "WinRTAutomationPeer";
                    // 系统文本控件的 rich text provider 位于原生层，不投影为 WinRT
                    // ITextProvider/IValueProvider。空的 GetPattern 需要实际 UIA 客户端验证，
                    // 不能只凭 TextBox 类型或文档声明推断已支持。
                    // https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.itextprovider
                    if (control is TextBox or PasswordBox)
                    {
                        var peerBounds = peer.GetBoundingRectangle();
                        item.PeerBounds = new() { Left = peerBounds.X, Top = peerBounds.Y, Width = peerBounds.Width, Height = peerBounds.Height };
                        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                        var origin = NativeUiaClient.ClientOrigin(windowHandle);
                        var scale = control.XamlRoot!.RasterizationScale;
                        var layout = control.TransformToVisual(window.Content as UIElement).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
                        // 独立由 XAML 根布局（view pixels）与 owned window client origin
                        // 计算物理 screen pixels，不猜测 peer 的坐标是否已乘 DPI。
                        item.RasterizationScale = scale;
                        item.ExpectedNativeBounds = new()
                        {
                            Left = origin.X + layout.X * scale,
                            Top = origin.Y + layout.Y * scale,
                            Width = layout.Width * scale,
                            Height = layout.Height * scale,
                        };
                        var request = new NativeUiaRequest
                        {
                            WindowHandle = (long)windowHandle, AutomationId = peer.GetAutomationId(), Name = item.Name,
                            Left = item.ExpectedNativeBounds.Left, Top = item.ExpectedNativeBounds.Top,
                            Width = item.ExpectedNativeBounds.Width, Height = item.ExpectedNativeBounds.Height,
                            IncludeText = control is TextBox,
                        };
                        // Win32 UIA 客户端在短寿命 owned 子进程的 MTA 查询此窗口；
                        // await 保持窗口消息泵运行，不把客户端缓存留在被测进程。
                        // https://learn.microsoft.com/windows/win32/winauto/uiauto-threading
                        // 仅用于隔离原生崩溃的诊断；跳过时明确失败，不能当作验收通过。
                        var native = Environment.GetEnvironmentVariable("MAMBO_UI_DIAG_SKIP_NATIVE_UIA") == "1"
                            ? new NativeUiaResult("DiagnosticNativeUiaSkipped", [])
                            : await NativeUiaProbe.QueryAsync(request);
                        item.NativePatternProbeStatus = native.Status;
                        item.NativePatterns = native.Patterns;
                        item.NativeControlType = native.ControlType;
                        item.NativeWindowProcessMatched = native.WindowProcessMatched;
                        item.NativeControlProcessMatched = native.ControlProcessMatched;
                        item.NativeHelperParentMatched = native.HelperParentMatched;
                        item.NativeHelperProcessMatched = native.HelperProcessMatched;
                        item.NativeHelperExitedNormally = native.HelperExitedNormally;
                        item.ActualNativeBounds = native.Bounds;
                        item.NativeLookupMode = native.LookupMode;
                        item.PatternProbeSource = "WinRTAutomationPeerAndNativeUIA";
                        item.Patterns = item.Patterns.Concat(native.Patterns).Distinct(StringComparer.Ordinal).ToArray();
                        if (native.Status != "Checked") item.Error = native.Status;
                        if (!control.IsLoaded) item.Error = "PageChangedDuringNativeProbe";
                    }
                    item.HasExpectedPattern = HasExpectedPattern(control, item.Patterns);
                    // 不调用 Invoke/Value getter：不会播放、导航、修改设置或读取输入框内容。
                    if (control.IsEnabled && control.IsTabStop)
                    {
                        item.FocusAttempted = true;
                        item.FocusSucceeded = item.KeyboardFocusable && control.Focus(FocusState.Keyboard)
                            && peer.HasKeyboardFocus();
                    }
                }
                catch (Exception)
                {
                    // 自动化服务的失败不输出本机 HRESULT/路径/输入内容。
                    item.Error = "AutomationPeerFailed";
                }
            }
        }
        finally
        {
            if (originalFocus?.IsLoaded == true && originalFocus.IsEnabled)
                originalFocus.Focus(FocusState.Programmatic);
        }
        report.MissingNames = report.Controls.Count(item => string.IsNullOrWhiteSpace(item.Name));
        report.MissingPatterns = report.Controls.Count(item => !item.HasExpectedPattern);
        report.FocusFailures = report.Controls.Count(item => item.FocusAttempted && !item.FocusSucceeded);
        report.PeerFailures = report.Controls.Count(item => item.Error.Length > 0);
        report.Status = report.Controls.Count > 0 && report.MissingNames == 0 && report.MissingPatterns == 0
            && report.FocusFailures == 0 && report.PeerFailures == 0 ? "Passed" : "Failed";
        return report;
    }

    private static bool HasExpectedPattern(Control control, string[] patterns) => control switch
    {
        Slider => patterns.Contains(nameof(PatternInterface.RangeValue)),
        ToggleSwitch => patterns.Contains(nameof(PatternInterface.Toggle)),
        RadioButton => patterns.Contains(nameof(PatternInterface.SelectionItem)),
        ToggleButton => patterns.Contains(nameof(PatternInterface.Toggle)),
        ButtonBase => patterns.Contains(nameof(PatternInterface.Invoke)),
        ComboBox => patterns.Contains(nameof(PatternInterface.ExpandCollapse)) && patterns.Contains(nameof(PatternInterface.Selection)),
        TextBox => patterns.Contains(nameof(PatternInterface.Value)) || patterns.Contains(nameof(PatternInterface.Text)),
        PasswordBox => patterns.Contains(nameof(PatternInterface.Value)),
        _ => false,
    };

    private static IEnumerable<Control> VisibleControls(DependencyObject node)
    {
        if (node is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: <= 0 }) yield break;
        if (node is FrameworkElement element && (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0)) yield break;
        // 播放期间被 Shell 禁用的浏览页不属于活动交互区域。
        if (node is Control { IsEnabled: false } && node is not ButtonBase and not TextBox and not PasswordBox and not Slider and not ToggleSwitch and not ComboBox)
            yield break;
        if (node is Control control && control is ButtonBase or TextBox or PasswordBox or Slider or ToggleSwitch or ComboBox)
        {
            yield return control;
            // 控件模板内的箭头、密码揭示等由系统控件负责，避免重复检测内部实现。
            yield break;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var child in VisibleControls(VisualTreeHelper.GetChild(node, index))) yield return child;
    }

}

internal sealed class AccessibilityReport
{
    public string Status { get; set; } = "NotRun";
    public string Scope { get; set; } = "";
    public bool SystemHighContrast { get; set; }
    public string HighContrastScheme { get; set; } = "";
    public bool NarratorAudioVerified { get; set; }
    public bool PhysicalKeyboardVerified { get; set; }
    public bool SystemContrastThemesVisuallyVerified { get; set; }
    public int MissingNames { get; set; }
    public int MissingPatterns { get; set; }
    public int FocusFailures { get; set; }
    public int PeerFailures { get; set; }
    public List<AccessibilityControlReport> Controls { get; } = [];
}

internal sealed class AccessibilityControlReport
{
    public string ElementName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Name { get; set; } = "";
    public string ControlType { get; set; } = "";
    public string[] Patterns { get; set; } = [];
    public string PatternProbeSource { get; set; } = "";
    public string NativePatternProbeStatus { get; set; } = "NotRequested";
    public string[] NativePatterns { get; set; } = [];
    public int NativeControlType { get; set; }
    public bool NativeWindowProcessMatched { get; set; }
    public bool NativeControlProcessMatched { get; set; }
    public bool NativeHelperParentMatched { get; set; }
    public bool NativeHelperProcessMatched { get; set; }
    public bool NativeHelperExitedNormally { get; set; }
    public string NativeLookupMode { get; set; } = "";
    public double RasterizationScale { get; set; }
    public AccessibilityBoundsReport? PeerBounds { get; set; }
    public AccessibilityBoundsReport? ExpectedNativeBounds { get; set; }
    public AccessibilityBoundsReport? ActualNativeBounds { get; set; }
    public bool HasExpectedPattern { get; set; }
    public bool Enabled { get; set; }
    public bool IsTabStop { get; set; }
    public int TabIndex { get; set; }
    public bool KeyboardFocusable { get; set; }
    public bool FocusAttempted { get; set; }
    public bool FocusSucceeded { get; set; }
    public string Error { get; set; } = "";
}

internal sealed class AccessibilityBoundsReport
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
