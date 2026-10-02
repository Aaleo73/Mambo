using Mambo.App.Shell;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace Mambo.App.Windowing;

/// <summary>
/// 自绘标题栏的非客户区：标题栏整体可拖动（SetTitleBar），按钮登记为 Passthrough，
/// 最大化按钮登记为 Maximize 区域以获得贴靠布局；悬停视觉由非客户区指针事件驱动。
/// </summary>
internal sealed class WindowChrome : IDisposable
{
    private readonly Microsoft.UI.Xaml.Window window;
    private readonly OverlappedPresenter presenter;
    private readonly ShellView shell;
    private readonly InputNonClientPointerSource source;
    private readonly NonClientHook hook;
    private bool hover;
    private bool pressed;

    public WindowChrome(Microsoft.UI.Xaml.Window window, OverlappedPresenter presenter, ShellView shell, nint hwnd)
    {
        this.window = window;
        this.presenter = presenter;
        this.shell = shell;
        source = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        source.PointerEntered += OnPointerEntered;
        source.PointerExited += OnPointerExited;
        hook = new NonClientHook(hwnd);
        hook.MaximizePressedChanged += OnMaximizePressed;
        hook.MaximizeClicked += ToggleMaximize;
        shell.TitleBarLayoutChanged += (_, _) => UpdateRegions();
        window.SizeChanged += (_, _) => UpdateRegions();
        shell.Loaded += (_, _) =>
        {
            UpdateRegions();
            if (shell.XamlRoot is { } root) root.Changed += (_, _) => UpdateRegions();
        };
        window.AppWindow.Changed += OnAppWindowChanged;
        UpdateVisual();
    }

    public void ToggleMaximize()
    {
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
    }

    public void UpdateRegions()
    {
        if (shell.XamlRoot is not { } root || shell.ActualWidth <= 0) return;
        var scale = root.RasterizationScale;
        var passthrough = shell.PassthroughElements.Where(e => e.ActualWidth > 0 && e.ActualHeight > 0).Select(e => ToRect(e, scale)).ToArray();
        source.SetRegionRects(NonClientRegionKind.Passthrough, passthrough);
        source.SetRegionRects(NonClientRegionKind.Maximize, [ToRect(shell.MaximizeElement, scale)]);
    }

    public void Dispose()
    {
        source.PointerEntered -= OnPointerEntered;
        source.PointerExited -= OnPointerExited;
        window.AppWindow.Changed -= OnAppWindowChanged;
        hook.Dispose();
    }

    private static RectInt32 ToRect(FrameworkElement element, double scale)
    {
        var origin = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new RectInt32(
            (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
            (int)Math.Round(element.ActualWidth * scale), (int)Math.Round(element.ActualHeight * scale));
    }

    private void OnPointerEntered(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        if (args.RegionKind != NonClientRegionKind.Maximize) return;
        hover = true;
        UpdateVisual();
    }

    private void OnPointerExited(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        if (args.RegionKind != NonClientRegionKind.Maximize) return;
        hover = false;
        pressed = false;
        UpdateVisual();
    }

    private void OnMaximizePressed(bool value)
    {
        pressed = value;
        UpdateVisual();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange) UpdateVisual();
    }

    private void UpdateVisual() => shell.SetMaximizeVisual(hover, pressed, presenter.State == OverlappedPresenterState.Maximized);
}
