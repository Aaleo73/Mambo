using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mambo.App.Video;

/// <summary>公开 API 不暴露 Player 类型。所有 Attach / Detach 和尺寸事件均在 UI 线程执行。</summary>
public sealed partial class VideoSurface : SwapChainPanel
{
    private nint swapChain;
    private FrameworkElement? host;
    private XamlRoot? root;
    private Size committed;
    private Size target;
    private (int Width, int Height) wanted;
    private bool liveResize;
    private readonly CompositeTransform stretch = new();
    private readonly DispatcherQueueTimer poll;
    private readonly DispatcherQueueTimer commit;
    private long deadline;
    private double lastDpiScale = 1;
    private readonly InputSystemCursor arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    public event Action<int, int>? PixelSizeRequested;
    public event Action<string>? DiagnosticError;
    public double DpiScale => XamlRoot?.RasterizationScale ?? 1;
    public (int Width, int Height) PixelSize =>
        (Math.Max(1, (int)Math.Round(target.Width * DpiScale)), Math.Max(1, (int)Math.Round(target.Height * DpiScale)));
    public (int Width, int Height) BufferSize => SwapChainPanelInterop.BufferSize(swapChain);

    public VideoSurface()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransform = stretch;
        RenderTransformOrigin = new Point(0, 0);
        poll = DispatcherQueue.CreateTimer();
        poll.Interval = TimeSpan.FromMilliseconds(8);
        poll.Tick += PollBuffer;
        commit = DispatcherQueue.CreateTimer();
        commit.Interval = TimeSpan.FromMilliseconds(40);
        commit.IsRepeating = false;
        commit.Tick += (_, _) =>
        {
            if (swapChain == 0 || BufferSize != wanted) return;
            committed = new Size(wanted.Width / DpiScale, wanted.Height / DpiScale);
            LayoutPanel();
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            Detach();
            if (host is not null) host.SizeChanged -= HostSizeChanged;
            if (root is not null) root.Changed -= DpiChanged;
            host = null;
            root = null;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        host = Parent as FrameworkElement;
        root = XamlRoot;
        if (host is not null)
        {
            host.SizeChanged += HostSizeChanged;
            target = committed = new Size(host.ActualWidth, host.ActualHeight);
            LayoutPanel();
        }
        if (root is not null) root.Changed += DpiChanged;
        ApplyClip();
    }

    // P0 内部探针入口；P1a 以后对前端只公开 Attach(IPlaybackSession)。
    internal void Attach(nint swapChainAddress)
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("视频绑定必须在界面线程执行。");
        if (swapChainAddress == 0) { Detach(); return; }
        SwapChainPanelInterop.Bind(this, swapChainAddress);
        swapChain = swapChainAddress;
        lastDpiScale = DpiScale;
        SwapChainPanelInterop.SetScale(swapChain, DpiScale);
        var actual = BufferSize;
        committed = new Size(actual.Width / DpiScale, actual.Height / DpiScale);
        LayoutPanel();
        RequestSize();
    }

    public void Detach()
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("视频解绑必须在界面线程执行。");
        poll.Stop();
        commit.Stop();
        if (swapChain != 0) SwapChainPanelInterop.Bind(this, 0);
        swapChain = 0;
        HideCursor(false);
    }

    public void HideCursor(bool hide) { ProtectedCursor = hide ? null : arrow; }
    public void SetLiveResize(bool active)
    {
        liveResize = active;
        if (active) { poll.Stop(); commit.Stop(); }
        else RequestSize();
    }

    private void HostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        target = args.NewSize;
        if (committed.Width <= 0 || committed.Height <= 0) committed = target;
        LayoutPanel();
        ApplyClip();
        if (!liveResize) RequestSize();
    }

    private void DpiChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(lastDpiScale - DpiScale) < 0.0001) return;
        lastDpiScale = DpiScale;
        SwapChainPanelInterop.SetScale(swapChain, DpiScale);
        if (!liveResize) RequestSize();
    }

    private void LayoutPanel()
    {
        if (committed.Width <= 0 || committed.Height <= 0) return;
        Width = committed.Width;
        Height = committed.Height;
        stretch.ScaleX = target.Width / committed.Width;
        stretch.ScaleY = target.Height / committed.Height;
    }

    private void RequestSize()
    {
        if (target.Width <= 0 || target.Height <= 0) return;
        wanted = PixelSize;
        PixelSizeRequested?.Invoke(wanted.Width, wanted.Height);
        if (swapChain == 0) return;
        commit.Stop();
        deadline = Environment.TickCount64 + 1000;
        poll.Start();
    }

    private void PollBuffer(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (BufferSize == wanted) { poll.Stop(); commit.Start(); }
            else if (Environment.TickCount64 >= deadline)
            {
                poll.Stop();
                DiagnosticError?.Invoke("视频缓冲区尺寸同步超时，保留已提交尺寸。");
            }
        }
        catch { poll.Stop(); DiagnosticError?.Invoke("无法读取视频缓冲区尺寸。"); }
    }

    private void ApplyClip()
    {
        if (host is null || target.Width <= 0 || target.Height <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(host);
        var compositor = visual.Compositor;
        var rectangle = compositor.CreateRoundedRectangleGeometry();
        rectangle.Size = new System.Numerics.Vector2((float)target.Width, (float)target.Height);
        rectangle.CornerRadius = new System.Numerics.Vector2(8, 8);
        visual.Clip = compositor.CreateGeometricClip(rectangle);
    }
}
