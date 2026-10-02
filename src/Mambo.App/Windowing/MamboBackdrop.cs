using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Windowing;

/// <summary>窗口亚克力；失焦时保持激活外观，系统不支持时退回 Mica。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "控制器在 OnTargetDisconnected 中释放。")]
public sealed partial class MamboBackdrop : SystemBackdrop
{
    private ISystemBackdropControllerWithTargets? controller;
    private SystemBackdropConfiguration? configuration;
    private FrameworkElement? root;

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
        if (controller is null) return;
        controller.AddSystemBackdropTarget(connectedTarget);
        controller.SetSystemBackdropConfiguration(configuration);
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
    }

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
