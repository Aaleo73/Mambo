using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Windowing;

/// <summary>窗口亚克力；失焦时保持激活外观，依次退回 Mica 和外壳实色背景。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "控制器在 OnTargetDisconnected 中释放。")]
public sealed partial class MamboBackdrop : SystemBackdrop
{
    private ISystemBackdropControllerWithTargets? controller;
    private SystemBackdropConfiguration? configuration;
    private FrameworkElement? root;
    private bool available;

    internal event Action<bool>? AvailabilityChanged;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        configuration = new SystemBackdropConfiguration { IsInputActive = true };
        root = xamlRoot.Content as FrameworkElement;
        if (root is not null)
        {
            root.ActualThemeChanged += OnThemeChanged;
            UpdateTheme();
        }
        if (DesktopAcrylicController.IsSupported())
            controller = new DesktopAcrylicController();
        else if (MicaController.IsSupported())
            controller = new MicaController();
        if (controller is null) { SetAvailable(false); return; }
        controller.AddSystemBackdropTarget(connectedTarget);
        controller.SetSystemBackdropConfiguration(configuration);
        SetAvailable(true);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (root is not null) root.ActualThemeChanged -= OnThemeChanged;
        if (controller is not null)
        {
            controller.RemoveSystemBackdropTarget(disconnectedTarget);
            (controller as IDisposable)?.Dispose();
            controller = null;
        }
        root = null;
        configuration = null;
        SetAvailable(false);
    }

    private void SetAvailable(bool value)
    {
        if (available == value) return;
        available = value;
        AvailabilityChanged?.Invoke(value);
    }

    /// <summary>
    /// 默认配置变化（主题、激活状态）时框架回调这里。配置由本类自己维护（始终保持激活外观），
    /// 不调用基类：基类实现面向默认配置，对自定义控制器会抛 ArgumentException。
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) => UpdateTheme();

    private void OnThemeChanged(FrameworkElement sender, object args) => UpdateTheme();

    private void UpdateTheme()
    {
        if (configuration is null || root is null) return;
        configuration.Theme = root.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
    }
}
