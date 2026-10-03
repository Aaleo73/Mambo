using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Mambo.Core.Playback;

namespace Mambo.App.Video;

/// <summary>公开 API 不暴露 Player 类型。所有 Attach / Detach 和尺寸事件均在 UI 线程执行。</summary>
public sealed partial class VideoSurface : SwapChainPanel, IDisposable
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
    private IPlaybackSession? demoSession;
    private PlaybackVideoBridge? playbackBridge;
    private PlaybackSession? playbackSession;
    private uint? demoColor;
    private SpriteVisual? demoVisual;
    private CompositionColorBrush? demoBrush;
    private readonly InputSystemCursor arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private CompositionRoundedRectangleGeometry? clipGeometry;
    private CompositionGeometricClip? clip;
    private float clipRadius = 8;
    private bool clipTopOnly;
    private bool disposed;
    internal event Action<int, int>? PixelSizeRequested;
    internal event Action<string>? DiagnosticError;
    internal double DpiScale => XamlRoot?.RasterizationScale ?? 1;
    internal (int Width, int Height) PixelSize =>
        (Math.Max(1, (int)Math.Round(target.Width * DpiScale)), Math.Max(1, (int)Math.Round(target.Height * DpiScale)));
    internal (int Width, int Height) BufferSize => SwapChainPanelInterop.BufferSize(swapChain);
    internal bool IsDemoAttached => demoSession is not null;

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
        commit.Tick += CommitBuffer;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>设置视口裁剪；只圆上角时向下延伸几何，不改变交换链的目标像素尺寸。</summary>
    public void SetViewportClip(float radius, bool topOnly)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("视频裁剪必须在界面线程执行。");
        if (!float.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (clipRadius == radius && clipTopOnly == topOnly) return;
        clipRadius = radius;
        clipTopOnly = topOnly;
        ApplyClip();
    }

    private void CommitBuffer(DispatcherQueueTimer sender, object args)
    {
        if (swapChain == 0 || BufferSize != wanted) return;
        committed = new Size(wanted.Width / DpiScale, wanted.Height / DpiScale);
        LayoutPanel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Detach();
        ReleaseHost();
    }

    private void ReleaseHost()
    {
        if (host is not null)
        {
            host.SizeChanged -= HostSizeChanged;
            if (clip is not null) ElementCompositionPreview.GetElementVisual(host).Clip = null;
        }
        if (root is not null) root.Changed -= DpiChanged;
        clip?.Dispose(); clip = null;
        clipGeometry?.Dispose(); clipGeometry = null;
        host = null;
        root = null;
    }

    /// <summary>永久释放控件的计时器、光标和 Composition 资源；释放后不能再次绑定播放。</summary>
    public void Dispose()
    {
        if (disposed) return;
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("视频释放必须在界面线程执行。");
        Detach();
        disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ReleaseHost();
        poll.Tick -= PollBuffer;
        commit.Tick -= CommitBuffer;
        ProtectedCursor = null;
        arrow.Dispose();
        PixelSizeRequested = null;
        DiagnosticError = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (disposed) return;
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
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("视频绑定必须在界面线程执行。");
        if (demoSession is not null) Detach();
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
        playbackBridge?.Dispose(); playbackBridge = null;
        if (playbackSession is { } playback) playback.SnapshotChanged -= PlaybackChanged;
        playbackSession = null;
        if (demoSession is { } demo) demo.SnapshotChanged -= DemoChanged;
        demoSession = null;
        demoColor = null;
        if (demoVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(this, null);
            demoVisual.Dispose();
            demoBrush?.Dispose();
            demoVisual = null;
            demoBrush = null;
        }
        if (swapChain != 0) SwapChainPanelInterop.Bind(this, 0);
        swapChain = 0;
        HideCursor(false);
    }

    internal void ClearNativeSwapChain()
    {
        poll.Stop(); commit.Stop();
        if (swapChain != 0) SwapChainPanelInterop.Bind(this, 0);
        swapChain = 0;
    }
    internal void ReportDiagnosticError(string message) => DiagnosticError?.Invoke(message);
    /// <summary>前端绑定入口；交换链、HDR 和关闭解绑由后端桥接管理。</summary>
    public void Attach(IPlaybackSession session)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("视频绑定必须在界面线程执行。");
        Detach();
        if (session is PlaybackSession real)
        {
            playbackSession = real;
            real.SnapshotChanged += PlaybackChanged;
            playbackBridge = new(this, real);
            return;
        }
        if (session.Snapshot.EngineKind != EngineKind.Demo)
            throw new AppException(new AppError(AppErrorKind.Player, ErrorCodes.PlaybackFailed, "此播放会话不支持内置视频画面。", false));
        demoSession = session;
        session.SnapshotChanged += DemoChanged;
        DemoChanged(session, EventArgs.Empty);
    }
    private void PlaybackChanged(object? sender, EventArgs args)
    {
        if (sender == playbackSession && playbackSession?.Snapshot.Phase == PlayerPhase.Closed) Detach();
    }

    private void DemoChanged(object? sender, EventArgs args)
    {
        if (sender != demoSession || demoSession is null) return;
        var snapshot = demoSession.Snapshot;
        if (snapshot.Phase == PlayerPhase.Closed) { Detach(); return; }
        var color = snapshot.DemoColorArgb ?? 0xFF203048u;
        if (color == demoColor) return;
        demoColor = color;
        var value = new Windows.UI.Color
        {
            A = (byte)(color >> 24), R = (byte)(color >> 16), G = (byte)(color >> 8), B = (byte)color,
        };
        if (demoBrush is not null) { demoBrush.Color = value; return; }
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        demoBrush = compositor.CreateColorBrush(value);
        demoVisual = compositor.CreateSpriteVisual();
        demoVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;
        demoVisual.Brush = demoBrush;
        ElementCompositionPreview.SetElementChildVisual(this, demoVisual);
    }

    public void HideCursor(bool hide) { if (!disposed) ProtectedCursor = hide ? null : arrow; }
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
        if (clipGeometry is null)
        {
            clipGeometry = compositor.CreateRoundedRectangleGeometry();
            clip = compositor.CreateGeometricClip(clipGeometry);
            visual.Clip = clip;
        }
        clipGeometry.CornerRadius = new System.Numerics.Vector2(clipRadius, clipRadius);
        clipGeometry.Size = new System.Numerics.Vector2((float)target.Width,
            (float)target.Height + (clipTopOnly ? clipRadius : 0));
    }
}
