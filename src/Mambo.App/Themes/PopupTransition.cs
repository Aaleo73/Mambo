using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mambo.App.Themes;

/// <summary>单面轻弹层；宿主保留逻辑状态、输入和退场后的卸载所有权。</summary>
internal sealed class PopupTransition : IDisposable
{
    private static readonly Vector3 HiddenOffset = new(0, 4, 0);
    private readonly FrameworkElement panel;
    private readonly Visual visual;
    private CompositionScopedBatch? batch;
    private TaskCompletionSource? completion;
    private long generation;
    private long batchGeneration;
    private bool targetVisible;
    private bool disposed;

    internal PopupTransition(FrameworkElement panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        this.panel = panel;
        ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
        visual = ElementCompositionPreview.GetElementVisual(panel);
        visual.Properties.InsertVector3("Translation", HiddenOffset);
        visual.Opacity = 0;
        panel.Visibility = Visibility.Collapsed;
    }

    internal bool IsRunning => completion is not null;
    internal Task PendingTransition => completion?.Task ?? Task.CompletedTask;

    internal Task OpenAsync(bool animate) => ChangeAsync(true, animate);
    internal Task CloseAsync(bool animate) => ChangeAsync(false, animate);

    private Task ChangeAsync(bool visible, bool animate)
    {
        if (disposed) return Task.CompletedTask;
        if (!animate || !Motion.AnimationsEnabled || !Motion.IsActive(panel) || Motion.IsEntranceSuppressed(panel))
        {
            Settle(visible);
            return Task.CompletedTask;
        }
        if (targetVisible == visible) return PendingTransition;

        var superseded = completion;
        generation++;
        ReleaseBatch();
        targetVisible = visible;
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = completion.Task;
        superseded?.TrySetResult();
        panel.Visibility = Visibility.Visible;
        try
        {
            var compositor = visual.Compositor;
            batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batchGeneration = generation;
            batch.Completed += OnCompleted;
            using var easing = Motion.CreateEasing(compositor, visible ? Motion.EaseOut : Motion.EaseIn);
            using var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = visible ? Motion.Feedback : Motion.Exit;
            fade.InsertKeyFrame(1, visible ? 1 : 0, easing);
            using var move = compositor.CreateVector3KeyFrameAnimation();
            move.Duration = fade.Duration;
            move.InsertKeyFrame(1, visible ? Vector3.Zero : HiddenOffset, easing);
            // No stop or starting keyframe: a reversal continues from the current presentation.
            visual.StartAnimation("Opacity", fade);
            visual.Properties.StartAnimation("Translation", move);
            batch.End();
        }
        catch
        {
            Settle(visible);
            throw;
        }
        return result;
    }

    private void OnCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (disposed || !ReferenceEquals(sender, batch) || batchGeneration != generation) return;
        Settle(targetVisible);
    }

    internal void Settle(bool visible)
    {
        if (disposed) return;
        generation++;
        targetVisible = visible;
        ReleaseBatch();
        visual.StopAnimation("Opacity");
        visual.Properties.StopAnimation("Translation");
        visual.Opacity = visible ? 1 : 0;
        visual.Properties.InsertVector3("Translation", visible ? Vector3.Zero : HiddenOffset);
        panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var finished = completion;
        completion = null;
        finished?.TrySetResult();
    }

    private void ReleaseBatch()
    {
        if (batch is null) return;
        batch.Completed -= OnCompleted;
        batch.Dispose();
        batch = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        Settle(false);
        disposed = true;
    }
}
