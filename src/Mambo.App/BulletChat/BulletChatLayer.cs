using System.Collections.Immutable;
using System.Numerics;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.BulletChat;

/// <summary>
/// 叠在视频上方的弹幕层。每条弹幕是一个画好的精灵，位置由合成器按一个共用时钟逐帧求值；
/// 界面线程只在 100ms 的节拍里生成即将出现的弹幕、回收已离场的，不参与逐帧运动。
/// 只依赖 Contracts；所有成员在界面线程调用。
/// </summary>
public sealed partial class BulletChatLayer : Grid, IDisposable
{
    private const double Lookahead = 1;
    private const double FixedSeconds = 5;
    private const double RetireMargin = 0.25;
    private const double RebaseAfter = 600;
    private const double ClockSpan = 3600;
    private const float BaseFontSize = 25;
    private const float EdgeMargin = 6;
    private const int MaximumSprites = 400;
    private const int MaximumPooled = 64;

    private sealed class Sprite(SpriteVisual visual, CompositionSurfaceBrush brush, CompositionDrawingSurface surface)
    {
        public SpriteVisual Visual { get; } = visual;
        public CompositionSurfaceBrush Brush { get; } = brush;
        public CompositionDrawingSurface Surface { get; } = surface;
        public ExpressionAnimation? Horizontal { get; set; }
        public ExpressionAnimation? Vertical { get; set; }
        public ExpressionAnimation? Fade { get; set; }
        public BulletChatMode Mode { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
    }

    private readonly DispatcherQueueTimer timer;
    private readonly BulletChatClock clock = new();
    private readonly BulletChatLanePlanner planner = new();
    private readonly List<Sprite> active = [];
    private readonly Stack<Sprite> pool = new();
    private Compositor? compositor;
    private ContainerVisual? root;
    private CompositionPropertySet? time;
    private CompositionPropertySet? layout;
    private LinearEasingFunction? linear;
    private ScalarKeyFrameAnimation? timeAnimation;
    private BulletChatTextRasterizer? rasterizer;
    private XamlRoot? observedRoot;
    private ImmutableArray<BulletChatComment> comments = [];
    private BulletChatSettings settings = new();
    private int cursor;
    private double baseSeconds;
    private double scale = 1;
    private float fontSize = BaseFontSize;
    private float laneHeight = 36;
    private bool presentable;
    private bool rendering;
    private bool disposed;

    public BulletChatLayer()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    internal int ActiveSprites => active.Count;
    internal long SpawnedTotal { get; private set; }
    internal bool IsRendering => rendering;
    internal bool IsClockRunning => rendering && clock.Running;
    internal double ClockSeconds => clock.Now(Environment.TickCount64);
    internal bool UsesBundledFont => rasterizer?.UsesBundledFont ?? false;

    /// <summary>换一组弹幕（按时间升序）；空数组即清空画面。</summary>
    public void SetComments(ImmutableArray<BulletChatComment> value)
    {
        if (disposed) return;
        var next = value.IsDefault ? [] : value;
        if (next == comments) return;
        comments = next;
        Rebuild();
    }

    public void Apply(BulletChatSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (disposed || value == settings) return;
        var previous = settings;
        settings = value;
        if (!rendering) return;
        // 字号与显示区域决定轨道的高度和数量，只能重排；透明度和速度直接改合成器上的值。
        if (previous.FontScale != value.FontScale || previous.Area != value.Area) { Rebuild(); return; }
        root!.Opacity = (float)value.Opacity;
        layout!.InsertScalar("D", (float)value.ScrollSeconds);
        foreach (var sprite in active)
            if (sprite.Mode == BulletChatMode.Scroll) sprite.End = sprite.Start + value.ScrollSeconds;
    }

    /// <summary>跟随播放状态。<paramref name="visible"/> 为 false（转场、关闭、外置播放）或不在播放阶段时清空并停下。</summary>
    public void Sync(SessionSnapshot snapshot, bool visible)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (disposed) return;
        var show = visible && snapshot.Phase == PlayerPhase.Playing;
        if (!show)
        {
            if (!presentable) return;
            presentable = false;
            Rebuild();
            return;
        }
        if (!presentable) { presentable = true; clock.Reset(); }
        var now = Environment.TickCount64;
        var delay = snapshot.CapturedAtUtc == default ? 0 : Math.Clamp((DateTimeOffset.UtcNow - snapshot.CapturedAtUtc).TotalMilliseconds, 0, 2000);
        var running = !snapshot.IsPaused && !snapshot.IsBuffering && !snapshot.IsSeeking;
        var change = clock.Observe(snapshot.PositionTicks / (double)TimeSpan.TicksPerSecond, snapshot.PlaybackRate, running, now - (long)delay, now);
        if (!CanRender)
        {
            // 还没有弹幕或尚未布局：只维护镜像时钟，等条件具备时由 SetComments / Loaded 重排。
            if (rendering) Rebuild();
            return;
        }
        if (change.HasFlag(BulletChatClockChange.Jump) || !rendering) Rebuild();
        // 暂停、继续、变速：合成器时钟从当前值接着走，弹幕不跳。
        else if (change != BulletChatClockChange.None) DriveClock(null);
    }

    private bool CanRender => !disposed && presentable && !comments.IsEmpty && IsLoaded && ActualWidth > 0 && ActualHeight > 0;

    /// <summary>按镜像时钟的当前时间重排：清空，再放回此刻仍在画面内的弹幕。</summary>
    private void Rebuild()
    {
        ClearSprites();
        if (!CanRender)
        {
            rendering = false;
            timer.Stop();
            time?.StopAnimation("T");
            return;
        }
        EnsureComposition();
        UpdateMetrics();
        var now = clock.Now(Environment.TickCount64);
        // 合成器时钟用相对秒，保持在较小的数值上，float 的精度才够逐帧平滑。
        baseSeconds = now - 1;
        DriveClock(1);
        planner.Reset();
        cursor = LowerBound(now - Math.Max(settings.ScrollSeconds, FixedSeconds));
        rendering = true;
        Advance(now);
        timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!rendering) return;
        var now = clock.Now(Environment.TickCount64);
        if (now - baseSeconds > RebaseAfter) Rebase(now);
        Advance(now);
    }

    private void Advance(double now)
    {
        var horizon = now + Lookahead;
        while (cursor < comments.Length && comments[cursor].TimeSeconds <= horizon)
        {
            var comment = comments[cursor++];
            if (comment.TimeSeconds + Duration(comment.Mode) > now) Spawn(comment);
        }
        for (var index = active.Count - 1; index >= 0; index--)
        {
            if (now <= active[index].End + RetireMargin) continue;
            Recycle(active[index]);
            active.RemoveAt(index);
        }
    }

    private double Duration(BulletChatMode mode) => mode == BulletChatMode.Scroll ? settings.ScrollSeconds : FixedSeconds;

    private void Spawn(BulletChatComment comment)
    {
        if (active.Count >= MaximumSprites) return;
        using var text = rasterizer!.Measure(comment.Text, fontSize);
        var lane = comment.Mode switch
        {
            BulletChatMode.Scroll => planner.PlaceScroll(comment.TimeSeconds, text.Size.X, ActualWidth, settings.ScrollSeconds),
            BulletChatMode.Top => planner.PlaceTop(comment.TimeSeconds, FixedSeconds),
            _ => planner.PlaceBottom(comment.TimeSeconds, FixedSeconds),
        };
        if (lane < 0) return;
        var sprite = pool.Count > 0 ? pool.Pop() : CreateSprite();
        rasterizer.Draw(sprite.Surface, text, comment.Rgb, (float)scale);
        sprite.Visual.Size = text.Size;
        sprite.Mode = comment.Mode;
        sprite.Start = comment.TimeSeconds;
        sprite.End = comment.TimeSeconds + Duration(comment.Mode);
        var start = (float)(comment.TimeSeconds - baseSeconds);
        var row = EdgeMargin + lane * laneHeight - BulletChatTextRasterizer.Padding;
        if (comment.Mode == BulletChatMode.Scroll)
        {
            sprite.Visual.Opacity = 1;
            sprite.Visual.Offset = new(0, row, 0);
            // 开始前在右缘之外，结束后在左缘之外；窗口缩放和调速只改 layout 上的值。
            sprite.Horizontal = compositor!.CreateExpressionAnimation("layout.W - (clock.T - start) / layout.D * (layout.W + this.Target.Size.X)");
            sprite.Horizontal.SetReferenceParameter("layout", layout);
            sprite.Horizontal.SetReferenceParameter("clock", time);
            sprite.Horizontal.SetScalarParameter("start", start);
            sprite.Visual.StartAnimation("Offset.X", sprite.Horizontal);
        }
        else
        {
            sprite.Visual.Offset = new(0, row, 0);
            sprite.Horizontal = compositor!.CreateExpressionAnimation("(layout.W - this.Target.Size.X) / 2");
            sprite.Horizontal.SetReferenceParameter("layout", layout);
            sprite.Visual.StartAnimation("Offset.X", sprite.Horizontal);
            if (comment.Mode == BulletChatMode.Bottom)
            {
                sprite.Vertical = compositor.CreateExpressionAnimation("layout.H - inset");
                sprite.Vertical.SetReferenceParameter("layout", layout);
                sprite.Vertical.SetScalarParameter("inset", EdgeMargin + (lane + 1) * laneHeight + BulletChatTextRasterizer.Padding);
                sprite.Visual.StartAnimation("Offset.Y", sprite.Vertical);
            }
            // 固定弹幕提前生成，到时间才显示，停留期满即隐藏。
            sprite.Fade = compositor.CreateExpressionAnimation("clock.T >= start && clock.T < start + stay ? 1 : 0");
            sprite.Fade.SetReferenceParameter("clock", time);
            sprite.Fade.SetScalarParameter("start", start);
            sprite.Fade.SetScalarParameter("stay", (float)FixedSeconds);
            sprite.Visual.StartAnimation("Opacity", sprite.Fade);
        }
        root!.Children.InsertAtTop(sprite.Visual);
        active.Add(sprite);
        SpawnedTotal++;
    }

    /// <summary>把相对时间的零点整体前移；所有精灵的起始时间与时钟同批改写，画面不动。</summary>
    private void Rebase(double now)
    {
        var shift = now - 1 - baseSeconds;
        baseSeconds += shift;
        foreach (var sprite in active)
        {
            var start = (float)(sprite.Start - baseSeconds);
            if (sprite.Mode == BulletChatMode.Scroll && sprite.Horizontal is { } horizontal)
            {
                horizontal.SetScalarParameter("start", start);
                sprite.Visual.StartAnimation("Offset.X", horizontal);
            }
            if (sprite.Fade is { } fade)
            {
                fade.SetScalarParameter("start", start);
                sprite.Visual.StartAnimation("Opacity", fade);
            }
        }
        DriveClock(1);
    }

    /// <summary>让合成器时钟按镜像的状态走或停。<paramref name="value"/> 非空时先把时钟设为该值，否则从当前值继续。</summary>
    private void DriveClock(double? value)
    {
        if (time is null || compositor is null) return;
        time.StopAnimation("T");
        if (value is { } seconds) time.InsertScalar("T", (float)seconds);
        timeAnimation?.Dispose();
        timeAnimation = null;
        if (!clock.Running) return;
        timeAnimation = compositor.CreateScalarKeyFrameAnimation();
        timeAnimation.InsertExpressionKeyFrame(1, "this.StartingValue + span", linear);
        timeAnimation.SetScalarParameter("span", (float)ClockSpan);
        timeAnimation.Duration = TimeSpan.FromSeconds(ClockSpan / clock.Rate);
        timeAnimation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        time.StartAnimation("T", timeAnimation);
    }

    private void EnsureComposition()
    {
        if (root is not null) return;
        compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        root = compositor.CreateContainerVisual();
        root.RelativeSizeAdjustment = Vector2.One;
        root.Clip = compositor.CreateInsetClip();
        time = compositor.CreatePropertySet();
        time.InsertScalar("T", 0);
        layout = compositor.CreatePropertySet();
        layout.InsertScalar("W", 0);
        layout.InsertScalar("H", 0);
        layout.InsertScalar("D", (float)settings.ScrollSeconds);
        linear = compositor.CreateLinearEasingFunction();
        rasterizer = new(compositor);
        rasterizer.SurfacesInvalidated += OnSurfacesInvalidated;
        ElementCompositionPreview.SetElementChildVisual(this, root);
    }

    private void UpdateMetrics()
    {
        scale = XamlRoot?.RasterizationScale ?? 1;
        fontSize = BaseFontSize * (float)settings.FontScale;
        using (var sample = rasterizer!.Measure("国Ag", fontSize))
            laneHeight = Math.Max(8, sample.Size.Y - BulletChatTextRasterizer.Padding * 2 + 2);
        root!.Opacity = (float)settings.Opacity;
        layout!.InsertScalar("D", (float)settings.ScrollSeconds);
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        layout!.InsertScalar("W", (float)ActualWidth);
        layout.InsertScalar("H", (float)ActualHeight);
        var lanes = Math.Max(1, (int)((ActualHeight * settings.Area - EdgeMargin) / laneHeight));
        planner.Configure(lanes, Math.Max(1, lanes / 2), Math.Max(1, lanes / 2));
    }

    private Sprite CreateSprite()
    {
        var surface = rasterizer!.CreateSurface();
        var brush = compositor!.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        var visual = compositor.CreateSpriteVisual();
        visual.Brush = brush;
        return new(visual, brush, surface);
    }

    private void Recycle(Sprite sprite)
    {
        root?.Children.Remove(sprite.Visual);
        sprite.Visual.StopAnimation("Offset.X");
        sprite.Visual.StopAnimation("Offset.Y");
        sprite.Visual.StopAnimation("Opacity");
        sprite.Horizontal?.Dispose(); sprite.Horizontal = null;
        sprite.Vertical?.Dispose(); sprite.Vertical = null;
        sprite.Fade?.Dispose(); sprite.Fade = null;
        if (pool.Count < MaximumPooled) pool.Push(sprite);
        else Release(sprite);
    }

    private static void Release(Sprite sprite)
    {
        sprite.Visual.Dispose();
        sprite.Brush.Dispose();
        sprite.Surface.Dispose();
    }

    private void ClearSprites()
    {
        foreach (var sprite in active) Recycle(sprite);
        active.Clear();
    }

    /// <summary>第一条时间不早于 <paramref name="seconds"/> 的弹幕的下标。</summary>
    private int LowerBound(double seconds)
    {
        int low = 0, high = comments.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (comments[middle].TimeSeconds < seconds) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void OnSurfacesInvalidated()
    {
        // 设备丢失的通知不一定在界面线程；池里的表面也失效了，一并丢弃后重排。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (disposed) return;
            ClearSprites();
            while (pool.Count > 0) Release(pool.Pop());
            Rebuild();
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (disposed) return;
        observedRoot = XamlRoot;
        if (observedRoot is not null) observedRoot.Changed += OnRootChanged;
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (observedRoot is not null) observedRoot.Changed -= OnRootChanged;
        observedRoot = null;
        if (!disposed) Rebuild();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (disposed) return;
        // 已在显示时只更新尺寸，在场的弹幕按新宽度继续走；从零尺寸变为可见才需要重排。
        if (rendering && CanRender) UpdateViewport();
        else Rebuild();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // 换显示器后按新的缩放比例重画，否则文字发虚。
        if (!disposed && rendering && Math.Abs(sender.RasterizationScale - scale) > 0.001) Rebuild();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        rendering = false;
        timer.Stop();
        timer.Tick -= OnTick;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        if (observedRoot is not null) observedRoot.Changed -= OnRootChanged;
        observedRoot = null;
        ClearSprites();
        while (pool.Count > 0) Release(pool.Pop());
        comments = [];
        if (root is null) return;
        time!.StopAnimation("T");
        ElementCompositionPreview.SetElementChildVisual(this, null);
        rasterizer!.SurfacesInvalidated -= OnSurfacesInvalidated;
        rasterizer.Dispose();
        timeAnimation?.Dispose();
        linear!.Dispose();
        layout!.Dispose();
        time.Dispose();
        root.Clip?.Dispose();
        root.Dispose();
        root = null;
    }
}
