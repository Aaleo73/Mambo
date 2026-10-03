using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

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
    private readonly UISettings systemSettings = new();
    private readonly DispatcherQueue dispatcher;
    private CompositionScopedBatch? batch;
    private TaskCompletionSource? completion;
    private bool targetPlayer;
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
        clock.InsertScalar("Progress", 0);
        clock.InsertScalar("Smooth", 0);
        browseState = compositor.CreatePropertySet();
        browseState.InsertScalar("Angle", 0);
        playerState = compositor.CreatePropertySet();
        playerState.InsertScalar("Angle", 0);
        systemSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
    }

    internal bool IsRunning => completion is not null;

    internal Task PlayAsync(bool showPlayer, bool animate)
    {
        Settle();
        targetPlayer = showPlayer;
        var hasBounds = (browse.IsLoaded && browse.ActualWidth > 0)
            || (player.IsLoaded && player.ActualWidth > 0);
        if (disposed || !animate || !Motion.AnimationsEnabled || !hasBounds)
        {
            Settle();
            return Task.CompletedTask;
        }

        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = completion.Task;
        // 先隐藏入场面再恢复布局，避免上一轮 Collapsed 的浏览面闪现。
        browseVisual.Opacity = showPlayer ? 1 : 0;
        playerVisual.Opacity = showPlayer ? 0 : 1;
        browse.Visibility = player.Visibility = Visibility.Visible;
        browse.IsHitTestVisible = player.IsHitTestVisible = false;
        clock.InsertScalar("Progress", 0);
        StartExpression(clock, "Smooth", "clock.Progress * clock.Progress * (3 - 2 * clock.Progress)");
        StartExpression(browseState, "Angle", showPlayer
            ? "Min(clock.Smooth * 2, 1) * 1.570796327"
            : "Min((1 - clock.Smooth) * 2, 1) * 1.570796327");
        StartExpression(playerState, "Angle", showPlayer
            ? "-Min((1 - clock.Smooth) * 2, 1) * 1.570796327"
            : "-Min(clock.Smooth * 2, 1) * 1.570796327");
        StartMatrix(browseVisual, browseState);
        StartMatrix(playerVisual, playerState);
        var browseOpacity = showPlayer ? "clock.Progress < 0.5 ? 1 : 0" : "clock.Progress < 0.5 ? 0 : 1";
        var playerOpacity = showPlayer ? "clock.Progress < 0.5 ? 0 : 1" : "clock.Progress < 0.5 ? 1 : 0";
        StartExpression(browseVisual, "Opacity", browseOpacity);
        StartExpression(playerVisual, "Opacity", playerOpacity);
        StartExpression(browseShade, "Opacity", "0.28 * (1 - Abs(2 * clock.Smooth - 1))");
        StartExpression(playerShade, "Opacity", "0.28 * (1 - Abs(2 * clock.Smooth - 1))");
        var compositor = browseVisual.Compositor;
        batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += OnCompleted;
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        using var linear = compositor.CreateLinearEasingFunction();
        animation.InsertKeyFrame(1, 1, linear);
        animation.Duration = Motion.Fold;
        clock.StartAnimation("Progress", animation);
        batch.End();
        return result;
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
        if (ReferenceEquals(sender, batch)) Settle();
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args) => dispatcher.TryEnqueue(() =>
    {
        if (!disposed && !Motion.AnimationsEnabled) Settle();
    });

    /// <summary>取消或完成都落到最近请求的终态，唤醒等待者而不抛取消异常。</summary>
    internal void Settle()
    {
        if (batch is not null)
        {
            batch.Completed -= OnCompleted;
            batch.Dispose();
            batch = null;
        }
        clock.StopAnimation("Progress");
        clock.StopAnimation("Smooth");
        browseState.StopAnimation("Angle");
        playerState.StopAnimation("Angle");
        ResetFace(browseVisual, browseShade);
        ResetFace(playerVisual, playerShade);
        browse.Visibility = targetPlayer ? Visibility.Collapsed : Visibility.Visible;
        player.Visibility = targetPlayer ? Visibility.Visible : Visibility.Collapsed;
        browse.IsHitTestVisible = !targetPlayer;
        player.IsHitTestVisible = targetPlayer;
        var finished = completion;
        completion = null;
        finished?.TrySetResult();
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
        systemSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        Settle();
        clock.Dispose();
        browseState.Dispose();
        playerState.Dispose();
    }
}
