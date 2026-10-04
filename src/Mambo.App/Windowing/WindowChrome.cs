using Mambo.App.Shell;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace Mambo.App.Windowing;

/// <summary>
/// 自绘标题栏的非客户区：显式登记 Caption 拖动区域，按钮登记为 Passthrough，
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
    private XamlRoot? root;

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
        shell.TitleBarLayoutChanged += OnTitleBarLayoutChanged;
        window.SizeChanged += OnSizeChanged;
        shell.Loaded += OnLoaded;
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
        if (window.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            source.SetRegionRects(NonClientRegionKind.Caption, []);
            source.SetRegionRects(NonClientRegionKind.Passthrough, []);
            source.SetRegionRects(NonClientRegionKind.Maximize, []);
            return;
        }
        var scale = root.RasterizationScale;
        var passthrough = shell.PassthroughElements.Where(e => e.ActualWidth > 0 && e.ActualHeight > 0).Select(e => ToRect(e, scale)).ToArray();
        source.SetRegionRects(NonClientRegionKind.Caption, [ToRect(shell.TitleBarElement, scale)]);
        source.SetRegionRects(NonClientRegionKind.Passthrough, passthrough);
        source.SetRegionRects(NonClientRegionKind.Maximize, [ToRect(shell.MaximizeElement, scale)]);
    }

    public void Dispose()
    {
        source.PointerEntered -= OnPointerEntered;
        source.PointerExited -= OnPointerExited;
        window.AppWindow.Changed -= OnAppWindowChanged;
        shell.TitleBarLayoutChanged -= OnTitleBarLayoutChanged;
        window.SizeChanged -= OnSizeChanged;
        shell.Loaded -= OnLoaded;
        if (root is not null) root.Changed -= OnRootChanged;
        root = null;
        hook.MaximizePressedChanged -= OnMaximizePressed;
        hook.MaximizeClicked -= ToggleMaximize;
        hook.Dispose();
        source.SetRegionRects(NonClientRegionKind.Caption, []);
        source.SetRegionRects(NonClientRegionKind.Passthrough, []);
        source.SetRegionRects(NonClientRegionKind.Maximize, []);
    }

    private void OnTitleBarLayoutChanged(object? sender, EventArgs args) => UpdateRegions();
    private void OnSizeChanged(object sender, WindowSizeChangedEventArgs args) => UpdateRegions();
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateRegions();
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (root is not null) root.Changed -= OnRootChanged;
        root = shell.XamlRoot;
        if (root is not null) root.Changed += OnRootChanged;
        UpdateRegions();
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
