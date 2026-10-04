using System.Diagnostics;
using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Shell;

/// <summary>两个面的中心 Y 轴翻折；只有独立的标量进度动画，矩阵和遮罩由合成器计算。</summary>
internal sealed class PlayerFoldTransition : IDisposable
{
    // Row-vector convention: translate to origin, rotate, project, translate back.
    private const string MatrixExpression =
        "Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, -face.Size.X/2,-face.Size.Y/2,0,1) * " +
        "Matrix4x4(Cos(state.Angle),0,-Sin(state.Angle),0, 0,1,0,0, Sin(state.Angle),0,Cos(state.Angle),0, 0,0,0,1) * " +
        "Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,1,-1/1400.0, 0,0,0,1) * " +
        "Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, face.Size.X/2,face.Size.Y/2,0,1)";

    private readonly FrameworkElement browse;
    private readonly FrameworkElement player;
    private readonly Visual browseVisual;
    private readonly Visual playerVisual;
    private readonly Visual browseShade;
    private readonly Visual playerShade;
    private readonly CompositionPropertySet clock;
    private readonly CompositionPropertySet browseState;
    private readonly CompositionPropertySet playerState;
    private readonly DispatcherQueue dispatcher;
    private CompositionScopedBatch? batch;
    private DispatcherQueueTimer? facingTimer;
    private XamlRoot? observedRoot;
    private TaskCompletionSource? completion;
    private long generation;
    private long batchGeneration;
    private long facingGeneration;
    private long queuedLayoutGeneration = -1;
    private long segmentStarted;
    private double segmentFrom;
    private TimeSpan segmentDuration;
    private bool requestedPlayer;
    private bool segmentPlayer;
    private bool facingPlayer;
    private bool clockRunning;
    private bool expressionsRunning;
    private bool waitingLayout;
    private bool observingLifetime;
    private bool disposed;

    internal PlayerFoldTransition(FrameworkElement browse, FrameworkElement player,
        FrameworkElement browseShade, FrameworkElement playerShade)
    {
        this.browse = browse;
        this.player = player;
        dispatcher = browse.DispatcherQueue;
        browseVisual = ElementCompositionPreview.GetElementVisual(browse);
        playerVisual = ElementCompositionPreview.GetElementVisual(player);
        this.browseShade = ElementCompositionPreview.GetElementVisual(browseShade);
        this.playerShade = ElementCompositionPreview.GetElementVisual(playerShade);
        var compositor = browseVisual.Compositor;
        clock = compositor.CreatePropertySet();
        clock.InsertScalar(nameof(Progress), 0);
        browseState = compositor.CreatePropertySet();
        browseState.InsertScalar("Angle", 0);
        playerState = compositor.CreatePropertySet();
        playerState.InsertScalar("Angle", 0);
        // Observe the faces before their first load, not only after a request needs their layout.
        browse.Loaded += OnLoaded;
        player.Loaded += OnLoaded;
        browse.Unloaded += OnUnloaded;
        player.Unloaded += OnUnloaded;
    }

    internal bool IsRunning => completion is not null;
    internal event Action<bool>? FacingChanged;

    /// <summary>请求时钟的模型值，不是合成器/GPU 的呈现值回读。</summary>
    internal double Progress
    {
        get
        {
            if (!clockRunning) return segmentFrom;
            var elapsed = Stopwatch.GetElapsedTime(segmentStarted).TotalSeconds;
            var t = Math.Clamp(elapsed / segmentDuration.TotalSeconds, 0, 1);
            var eased = t * t * (3 - 2 * t);
            return segmentFrom + ((segmentPlayer ? 1 : 0) - segmentFrom) * eased;
        }
    }

    // Shell records intent before fullscreen/focus changes can synchronously settle the fold.
    // The running segment keeps its own target until PlayAsync replaces it.
    internal void RequestTarget(bool showPlayer)
    {
        if (!disposed) requestedPlayer = showPlayer;
    }

    internal Task PlayAsync(bool showPlayer, bool animate)
    {
        if (disposed) return Task.CompletedTask;
        RequestTarget(showPlayer);
        if (!animate || !Motion.AnimationsEnabled || !HostCanPresent())
        {
            Settle();
            return Task.CompletedTask;
        }
        if (completion is not null && segmentPlayer == showPlayer) return completion.Task;

        var from = Progress;
        if (Math.Abs((showPlayer ? 1 : 0) - from) < 0.001)
        {
            Settle();
            return Task.CompletedTask;
        }

        var superseded = completion;
        generation++;
        ReleaseRequest();
        segmentFrom = from;
        segmentPlayer = showPlayer;
        clockRunning = false;
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = completion.Task;
        superseded?.TrySetResult();

        try
        {
            // Set the first frame before restoring the collapsed face's layout.
            // During reversal the existing expressions and scalar presentation are left untouched.
            if (!expressionsRunning)
            {
                browseVisual.Opacity = from < 0.5 ? 1 : 0;
                playerVisual.Opacity = from < 0.5 ? 0 : 1;
            }
            browse.IsHitTestVisible = player.IsHitTestVisible = false;
            ObserveLifetime();
            waitingLayout = true;
            browse.SizeChanged += OnSizeChanged;
            player.SizeChanged += OnSizeChanged;
            browse.LayoutUpdated += OnLayoutUpdated;
            browse.Visibility = player.Visibility = Visibility.Visible;
            if (!HasBounds(browse) || !HasBounds(player))
            {
                // An interrupted request can also lose layout. LeaveCurrentValue holds the
                // actual Composition scalar while layout catches up; never write a model endpoint.
                clock.StopAnimation(nameof(Progress));
            }
            TryStart(generation);
        }
        catch
        {
            Settle();
            throw;
        }
        return result;
    }

    private bool HostCanPresent() =>
        (browse.IsLoaded || player.IsLoaded) &&
        (browse.XamlRoot ?? player.XamlRoot) is { IsHostVisible: true };

    private static bool HasBounds(FrameworkElement face) =>
        face.IsLoaded && face.ActualWidth > 0 && face.ActualHeight > 0;

    private void ObserveLifetime()
    {
        observingLifetime = true;
        observedRoot = browse.XamlRoot ?? player.XamlRoot;
        if (observedRoot is not null) observedRoot.Changed += OnRootChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (completion is not null) Settle();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!sender.IsHostVisible) Settle();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => QueueLayoutStart();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => QueueLayoutStart();
    private void OnLayoutUpdated(object? sender, object args) => QueueLayoutStart();

    private void QueueLayoutStart()
    {
        if (disposed || !waitingLayout || queuedLayoutGeneration == generation) return;
        var version = generation;
        queuedLayoutGeneration = version;
        if (!dispatcher.TryEnqueue(() =>
        {
            if (disposed || version != generation || !waitingLayout) return;
            queuedLayoutGeneration = -1;
            TryStart(version);
        })) Settle();
    }

    private void StopWaitingForLayout()
    {
        if (!waitingLayout) return;
        waitingLayout = false;
        queuedLayoutGeneration = -1;
        browse.SizeChanged -= OnSizeChanged;
        player.SizeChanged -= OnSizeChanged;
        browse.LayoutUpdated -= OnLayoutUpdated;
    }

    private void TryStart(long version)
    {
        if (disposed || version != generation || !waitingLayout) return;
        try
        {
            if (!HostCanPresent() || !Motion.AnimationsEnabled)
            {
                Settle();
                return;
            }
            if (!HasBounds(browse) || !HasBounds(player)) return;
            StopWaitingForLayout();
            StartSegment(version);
        }
        catch (Exception error)
        {
            var failed = completion;
            completion = null;
            Settle();
            failed?.TrySetException(error);
        }
    }

    private void StartSegment(long version)
    {
        if (!expressionsRunning)
        {
            // Expressions are outside the finite animation batch: they have no completion.
            expressionsRunning = true;
            clock.InsertScalar(nameof(Progress), (float)segmentFrom);
            StartExpression(browseState, "Angle", "Min(clock.Progress * 2, 1) * 1.570796327");
            StartExpression(playerState, "Angle", "-Min((1 - clock.Progress) * 2, 1) * 1.570796327");
            StartMatrix(browseVisual, browseState);
            StartMatrix(playerVisual, playerState);
            StartExpression(browseVisual, "Opacity", "clock.Progress < 0.5 ? 1 : 0");
            StartExpression(playerVisual, "Opacity", "clock.Progress < 0.5 ? 0 : 1");
            StartExpression(browseShade, "Opacity", "0.18 * (1 - Abs(2 * clock.Progress - 1))");
            StartExpression(playerShade, "Opacity", "0.18 * (1 - Abs(2 * clock.Progress - 1))");
        }

        var target = segmentPlayer ? 1 : 0;
        segmentDuration = TimeSpan.FromTicks((long)(Motion.Mode.Ticks * Math.Abs(target - segmentFrom)));
        var compositor = browseVisual.Compositor;
        batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batchGeneration = version;
        batch.Completed += OnCompleted;
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        using var easing = Motion.CreateEasing(compositor, Motion.Symmetric);
        animation.InsertKeyFrame(1, target, easing);
        animation.Duration = segmentDuration;
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        segmentStarted = Stopwatch.GetTimestamp();
        clockRunning = true;
        // No starting keyframe and no StopAnimation on reversal: Composition continues
        // from its current presentation, while the model only determines remaining time.
        clock.StartAnimation(nameof(Progress), animation);
        batch.End();
        ScheduleFacingChange(version, target);
        ChangeFacing(segmentFrom >= 0.5);
    }

    private void ScheduleFacingChange(long version, double target)
    {
        var u = (0.5 - segmentFrom) / (target - segmentFrom);
        if (!(u > 0 && u < 1) && !(u == 0 && target < segmentFrom)) return;
        var crossing = 0.5 - Math.Sin(Math.Asin(1 - 2 * u) / 3);
        var due = TimeSpan.FromTicks((long)(segmentDuration.Ticks * crossing));
        var remaining = due - Stopwatch.GetElapsedTime(segmentStarted);
        facingTimer = dispatcher.CreateTimer();
        facingGeneration = version;
        facingTimer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromTicks(1);
        facingTimer.IsRepeating = false;
        facingTimer.Tick += OnFacingTimer;
        facingTimer.Start();
    }

    private void OnFacingTimer(DispatcherQueueTimer sender, object args)
    {
        if (disposed || !ReferenceEquals(sender, facingTimer) || facingGeneration != generation) return;
        StopFacingTimer();
        ChangeFacing(segmentPlayer);
    }

    private void ChangeFacing(bool showPlayer)
    {
        if (facingPlayer == showPlayer) return;
        facingPlayer = showPlayer;
        FacingChanged?.Invoke(showPlayer);
    }

    private void StartExpression(CompositionObject target, string property, string expression)
    {
        using var animation = browseVisual.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("clock", clock);
        target.StartAnimation(property, animation);
    }

    private static void StartMatrix(Visual face, CompositionPropertySet state)
    {
        using var animation = face.Compositor.CreateExpressionAnimation(MatrixExpression);
        animation.SetReferenceParameter("face", face);
        animation.SetReferenceParameter("state", state);
        face.StartAnimation("TransformMatrix", animation);
    }

    private void OnCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (disposed || !ReferenceEquals(sender, batch) || batchGeneration != generation) return;
        // This event, not Progress or the half timer, is the real attach boundary.
        // A recorded next request may be waiting for Shell's settings write. Finish this
        // physical segment without committing/unlocking its now-obsolete logical terminal.
        CompleteAt(segmentPlayer, commitTarget: segmentPlayer == requestedPlayer);
    }

    private void StopFacingTimer()
    {
        if (facingTimer is null) return;
        facingTimer.Stop();
        facingTimer.Tick -= OnFacingTimer;
        facingTimer = null;
    }

    private void ReleaseRequest()
    {
        StopFacingTimer();
        StopWaitingForLayout();
        if (batch is not null)
        {
            batch.Completed -= OnCompleted;
            batch.Dispose();
            batch = null;
        }
        if (!observingLifetime) return;
        observingLifetime = false;
        if (observedRoot is not null) observedRoot.Changed -= OnRootChanged;
        observedRoot = null;
    }

    /// <summary>立即提交最新请求的终态；过期请求的等待者也正常完成，不抛取消异常。</summary>
    internal void Settle()
    {
        if (!disposed) CompleteAt(requestedPlayer, commitTarget: true);
    }

    private void CompleteAt(bool showPlayer, bool commitTarget)
    {
        generation++;
        ReleaseRequest();
        var finished = completion;
        completion = null;
        clockRunning = false;
        segmentFrom = showPlayer ? 1 : 0;
        segmentPlayer = showPlayer;
        segmentStarted = 0;
        segmentDuration = TimeSpan.Zero;
        clock.StopAnimation(nameof(Progress));
        clock.InsertScalar(nameof(Progress), (float)segmentFrom);
        if (expressionsRunning)
        {
            expressionsRunning = false;
            browseState.StopAnimation("Angle");
            playerState.StopAnimation("Angle");
        }
        ResetFace(browseVisual, browseShade);
        ResetFace(playerVisual, playerShade);
        if (commitTarget)
        {
            browse.Visibility = showPlayer ? Visibility.Collapsed : Visibility.Visible;
            player.Visibility = showPlayer ? Visibility.Visible : Visibility.Collapsed;
            browse.IsHitTestVisible = !showPlayer;
            player.IsHitTestVisible = showPlayer;
        }
        else
        {
            // Keep the next request's navigation/input lock while holding a static face.
            browseVisual.Opacity = showPlayer ? 0 : 1;
            playerVisual.Opacity = showPlayer ? 1 : 0;
            browse.IsHitTestVisible = player.IsHitTestVisible = false;
        }
        finished?.TrySetResult();
        ChangeFacing(showPlayer);
    }

    private static void ResetFace(Visual face, Visual shade)
    {
        face.StopAnimation("TransformMatrix");
        face.StopAnimation("Opacity");
        face.TransformMatrix = Matrix4x4.Identity;
        face.Opacity = 1;
        shade.StopAnimation("Opacity");
        shade.Opacity = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        browse.Loaded -= OnLoaded;
        player.Loaded -= OnLoaded;
        browse.Unloaded -= OnUnloaded;
        player.Unloaded -= OnUnloaded;
        CompleteAt(requestedPlayer, commitTarget: true);
        FacingChanged = null;
        clock.Dispose();
        browseState.Dispose();
        playerState.Dispose();
    }
}
