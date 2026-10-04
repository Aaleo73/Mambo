using System.Numerics;
using System.Runtime.InteropServices;
using Mambo.App.Images;
using Mambo.App.Themes;
using Mambo.App.Views.Controls;
using Mambo.Core.Contracts;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mambo.App.Shell;

/// <summary>The browse face's sole owner of decoded Hero artwork.</summary>
public sealed partial class HeroBackdropPresenter : Grid, IDisposable
{
    private readonly SemaphoreSlim decodeGate = new(1, 1);
    private WindowContext? window;
    private WindowMotionObserver? motionObserver;
    private WindowContrastObserver? contrastObserver;
    private ContainerVisual? root;
    private CompositionLinearGradientBrush? mask;
    private SpriteVisual? scrim;
    private Layer? lower;
    private Layer? upper;
    private Layer? target;
    private CancellationTokenSource? loading;
    private PreparedBackdrop? outstanding;
    private CompositionScopedBatch? batch;
    private TaskCompletionSource<bool>? transition;
    private int preparationGeneration;
    private int commitGeneration;
    private bool disposed;
    private bool highContrast;
    private bool dismissed;
    private double verticalOffset;
    private double artWidth;
    private double artHeight;

    public HeroBackdropPresenter()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Unloaded += OnUnloaded;
    }

    internal Task PendingTransition => transition?.Task ?? Task.CompletedTask;
    internal ImageRef? DisplayedSource => target?.Surface?.Source;
    internal LoadedImageSurface? DisplayedSurface => target?.Surface?.Surface;
    internal ImageRef? LowerSource => lower?.Surface?.Source;
    internal ImageRef? UpperSource => upper?.Surface?.Source;
    internal LoadedImageSurface? LowerSurface => lower?.Surface?.Surface;
    internal LoadedImageSurface? UpperSurface => upper?.Surface?.Surface;
    internal int DisplayedDecodeWidth => target?.Surface?.DecodeWidth ?? 0;
    internal int PresentedSurfaceCount => (lower?.Surface is null ? 0 : 1) + (upper?.Surface is null ? 0 : 1);
    internal int InFlightDecodeCount { get; private set; }
    internal long FetchStartedCount { get; private set; }
    internal long DecodeStartedCount { get; private set; }
    internal bool IsTransitioning => batch is not null;

    internal void Initialize(WindowContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ReferenceEquals(window, context)) return;
        motionObserver?.Dispose();
        contrastObserver?.Dispose();
        if (window is not null) window.ActiveChanged -= OnWindowActiveChanged;
        window = context;
        window.ActiveChanged += OnWindowActiveChanged;
        motionObserver = new WindowMotionObserver(context, DispatcherQueue, enabled => { if (!enabled) Settle(); });
        contrastObserver = new WindowContrastObserver(context, DispatcherQueue, ApplyContrast);
        ApplyContrast(contrastObserver.HighContrast);
    }

    internal async Task<PreparedBackdrop?> PrepareAsync(ImageRef? image, int decodeWidth, CancellationToken cancellationToken)
    {
        if (disposed) return null;
        var generation = ++preparationGeneration;
        loading?.Cancel();
        outstanding?.Dispose();
        outstanding = null;
        cancellationToken.ThrowIfCancellationRequested();
        decodeWidth = Math.Max(1, decodeWidth);
        if (highContrast || image is null || ImageLoader.Current is not { } loader)
            return outstanding = new PreparedBackdrop(null, generation, cancellationToken);
        var reusable = FindSurface(image, decodeWidth);
        if (reusable is not null)
            return outstanding = new PreparedBackdrop(reusable.Retain(), generation, cancellationToken);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loading = cancellation;
        var token = cancellation.Token;
        var entered = false;
        LoadedImageSurface? surface = null;
        try
        {
            // A cancelled native decode is detached and disposed before the next one starts.
            await decodeGate.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (disposed || generation != preparationGeneration) return null;
            FetchStartedCount++;
            using var stream = await loader.FetchStreamAsync(image, decodeWidth, ImagePriority.Hero, token);
            token.ThrowIfCancellationRequested();
            if (stream is not null)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                DecodeStartedCount++;
                InFlightDecodeCount++;
                try
                {
                    surface = LoadedImageSurface.StartLoadFromStream(stream, new Size(decodeWidth, decodeWidth * 9 / 16.0));
                    void Loaded(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args) =>
                        completion.TrySetResult(args.Status == LoadedImageSourceLoadStatus.Success);
                    surface.LoadCompleted += Loaded;
                    bool succeeded;
                    try
                    {
                        if (surface.DecodedPhysicalSize.Width > 0) completion.TrySetResult(true);
                        succeeded = await completion.Task.WaitAsync(token);
                    }
                    finally { surface.LoadCompleted -= Loaded; }
                    token.ThrowIfCancellationRequested();
                    if (!succeeded) { surface.Dispose(); surface = null; }
                }
                catch (Exception error) when (error is COMException or ArgumentException)
                {
                    surface?.Dispose();
                    surface = null;
                }
                finally { InFlightDecodeCount--; }
            }
            token.ThrowIfCancellationRequested();
            if (disposed || highContrast || generation != preparationGeneration) return null;
            var resource = surface is null ? null : new BackdropSurface(image, decodeWidth, surface);
            surface = null;
            return outstanding = new PreparedBackdrop(resource, generation, cancellationToken);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        finally
        {
            surface?.Dispose();
            if (entered) decodeGate.Release();
            if (ReferenceEquals(loading, cancellation)) loading = null;
        }
    }

    internal async Task<bool> CommitAsync(PreparedBackdrop prepared, bool animate, Func<bool> isCurrent, Action? committed = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(isCurrent);
        var request = ++commitGeneration;
        try
        {
            bool Valid() => !disposed && !prepared.IsDisposed && !prepared.CancellationToken.IsCancellationRequested
                && prepared.PreparationGeneration == preparationGeneration && request == commitGeneration && isCurrent();
            if (!Valid()) return false;
            // A third image never evicts either still-visible face to make room for itself.
            while (batch is not null && !IsPresented(prepared.Surface))
            {
                await PendingTransition;
                if (!Valid()) return false;
            }
            if (!Valid()) return false;
            EnsureVisuals();
            // 上一张已经随页面退出淡掉了：丢掉它，新图从空白淡入，而不是和旧图交叉溶解。
            var revealing = dismissed;
            if (dismissed)
            {
                dismissed = false;
                root!.StopAnimation("Opacity");
                lower!.SetSurface(null);
                upper!.SetSurface(null);
                target = null;
            }
            root!.Opacity = 1;
            if (ReferenceEquals(outstanding, prepared)) outstanding = null;
            var resource = prepared.Surface;
            var canAnimate = animate && IsLoaded && !highContrast && (window?.IsActive ?? true)
                && (motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled);
            if (resource is null || highContrast)
            {
                Settle();
                lower!.SetSurface(null);
                upper!.SetSurface(null);
                target = lower;
                committed?.Invoke();
                return true;
            }

            var match = ReferenceEquals(lower!.Surface, resource) ? lower : ReferenceEquals(upper!.Surface, resource) ? upper : null;
            if (match is not null)
            {
                var reversing = !ReferenceEquals(target, match);
                target = match;
                committed?.Invoke();
                if (batch is not null)
                {
                    if (!canAnimate) Settle();
                    else if (reversing) StartFade(ReferenceEquals(target, upper) ? 1 : 0);
                }
                prepared.Dispose();
                return await CompleteCommitAsync();
            }

            var sameImage = target?.Surface?.Source == resource.Source;
            if (!canAnimate || sameImage || target?.Surface is null)
            {
                Settle();
                lower.SetSurface(prepared.TakeSurface());
                upper!.SetSurface(null);
                lower.Visual.Opacity = 1;
                upper.Visual.Opacity = 0;
                target = lower;
                committed?.Invoke();
                if (revealing && canAnimate) FadeRoot(0, 1, Motion.Content, Motion.EaseOut);
                return true;
            }

            upper!.SetSurface(prepared.TakeSurface());
            upper.Visual.Opacity = 0;
            lower.Visual.Opacity = 1;
            target = upper;
            committed?.Invoke();
            StartFade(1);
            return await CompleteCommitAsync();
        }
        finally
        {
            if (ReferenceEquals(outstanding, prepared)) outstanding = null;
            prepared.Dispose();
        }
    }

    private async Task<bool> CompleteCommitAsync()
    {
        await PendingTransition;
        return true;
    }

    private BackdropSurface? FindSurface(ImageRef image, int width)
    {
        if (target?.Surface is { } current && current.Source == image && current.DecodeWidth >= width) return current;
        if (lower?.Surface is { } first && first.Source == image && first.DecodeWidth >= width) return first;
        if (upper?.Surface is { } second && second.Source == image && second.DecodeWidth >= width) return second;
        return null;
    }

    private bool IsPresented(BackdropSurface? surface) => surface is not null &&
        (ReferenceEquals(lower?.Surface, surface) || ReferenceEquals(upper?.Surface, surface));

    private void EnsureVisuals()
    {
        if (root is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        root = compositor.CreateContainerVisual();
        mask = HeroArt.CreateEdgeFade(compositor);
        lower = new Layer(compositor, mask);
        upper = new Layer(compositor, mask);
        root.Children.InsertAtTop(lower.Visual);
        root.Children.InsertAtTop(upper.Visual);
        scrim = HeroArt.CreateCopyScrim(compositor, mask);
        root.Children.InsertAtTop(scrim);
        ElementCompositionPreview.SetElementChildVisual(this, root);
        ApplyGeometry();
    }

    private void StartFade(float opacity)
    {
        // Target-only keyframes inherit the compositor's current opacity on reversal.
        ReleaseBatch();
        var compositor = upper!.Visual.Compositor;
        batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += OnCompleted;
        transition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // EaseOut 前 110 ms 就走完八成，400 ms 的纯淡变看起来像切换；对称曲线才是匀速感的溶解。
        using var easing = Motion.CreateEasing(compositor, Motion.Symmetric);
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = Motion.Image;
        animation.InsertKeyFrame(1, opacity, easing);
        upper.Visual.StartAnimation("Opacity", animation);
        batch.End();
    }

    private void OnCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (ReferenceEquals(sender, batch)) Settle();
    }

    internal void Settle()
    {
        ReleaseBatch();
        if (lower is null || upper is null) return;
        lower.Visual.StopAnimation("Opacity");
        upper.Visual.StopAnimation("Opacity");
        if (ReferenceEquals(target, upper))
        {
            (lower, upper) = (upper, lower);
            root!.Children.Remove(lower.Visual);
            root.Children.InsertAtBottom(lower.Visual);
        }
        lower.Visual.Opacity = 1;
        upper.Visual.Opacity = 0;
        upper.SetSurface(null);
        target = lower;
    }

    private void ReleaseBatch()
    {
        if (batch is not null)
        {
            batch.Completed -= OnCompleted;
            batch.Dispose();
            batch = null;
        }
        var completion = transition;
        transition = null;
        completion?.TrySetResult(true);
    }

    internal void UpdateGeometry(double offset, double width, double height)
    {
        if (disposed) return;
        verticalOffset = Math.Max(0, offset);
        artWidth = Math.Max(0, width);
        artHeight = Math.Max(0, height);
        ApplyGeometry();
    }

    private void ApplyGeometry()
    {
        if (root is null) return;
        var size = new Vector2((float)artWidth, (float)artHeight);
        root.Offset = new Vector3(0, -(float)verticalOffset, 0);
        root.Size = size;
        lower!.Visual.Size = upper!.Visual.Size = scrim!.Size = size;
    }

    private void ApplyContrast(bool value)
    {
        highContrast = value;
        Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        if (value) Clear();
    }

    private void OnWindowActiveChanged(object? sender, EventArgs e)
    {
        if (window?.IsActive != false) return;
        CancelPreparation();
        Settle();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPreparation();
        Settle();
    }

    private void CancelPreparation()
    {
        preparationGeneration++;
        commitGeneration++;
        loading?.Cancel();
        outstanding?.Dispose();
        outstanding = null;
    }

    internal void Clear()
    {
        CancelPreparation();
        Settle();
        lower?.SetSurface(null);
        upper?.SetSurface(null);
        target = null;
        dismissed = false;
        if (root is null) return;
        root.StopAnimation("Opacity");
        root.Opacity = 0;
    }

    /// <summary>
    /// 换到另一部作品时调用：当前背景和旧页面一起淡出，新背景准备好后再淡入。
    /// 不这样做的话，旧海报会一直留到新页面的数据和图片都加载完。
    /// </summary>
    internal void Dismiss()
    {
        CancelPreparation();
        Settle();
        var canAnimate = root is not null && lower?.Surface is not null && IsLoaded && !highContrast && (window?.IsActive ?? true)
            && (motionObserver?.AnimationsEnabled ?? Motion.AnimationsEnabled);
        if (!canAnimate) { Clear(); return; }
        dismissed = true;
        FadeRoot(null, 0, Motion.Exit, Motion.EaseIn);
    }

    private void FadeRoot(float? from, float to, TimeSpan duration, (Vector2, Vector2) spline)
    {
        var compositor = root!.Compositor;
        using var easing = Motion.CreateEasing(compositor, spline);
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        if (from is { } start) animation.InsertKeyFrame(0, start);
        animation.InsertKeyFrame(1, to, easing);
        animation.Duration = duration;
        root.StartAnimation("Opacity", animation);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
        Unloaded -= OnUnloaded;
        motionObserver?.Dispose();
        contrastObserver?.Dispose();
        if (window is not null) window.ActiveChanged -= OnWindowActiveChanged;
        window = null;
        ElementCompositionPreview.SetElementChildVisual(this, null);
        lower?.Dispose();
        upper?.Dispose();
        if (scrim?.Brush is CompositionMaskBrush scrimMask)
        {
            scrimMask.Source?.Dispose();
            scrimMask.Dispose();
        }
        scrim?.Dispose();
        mask?.Dispose();
        root?.Dispose();
        root = null;
    }

    private sealed class Layer : IDisposable
    {
        private readonly CompositionMaskBrush mask;
        private CompositionSurfaceBrush? brush;
        internal Layer(Compositor compositor, CompositionBrush edgeFade)
        {
            mask = compositor.CreateMaskBrush();
            mask.Mask = edgeFade;
            Visual = compositor.CreateSpriteVisual();
            Visual.Brush = mask;
            Visual.Scale = Vector3.One;
        }
        internal SpriteVisual Visual { get; }
        internal BackdropSurface? Surface { get; private set; }
        internal void SetSurface(BackdropSurface? surface)
        {
            mask.Source = null;
            brush?.Dispose();
            brush = null;
            Surface?.Release();
            Surface = surface;
            if (surface is null) return;
            brush = Visual.Compositor.CreateSurfaceBrush(surface.Surface);
            brush.Stretch = CompositionStretch.UniformToFill;
            mask.Source = brush;
        }
        public void Dispose() { SetSurface(null); Visual.Dispose(); mask.Dispose(); }
    }
}

internal sealed class BackdropSurface(ImageRef source, int decodeWidth, LoadedImageSurface surface)
{
    private int references = 1;
    internal ImageRef Source { get; } = source;
    internal int DecodeWidth { get; } = decodeWidth;
    internal LoadedImageSurface Surface { get; } = surface;
    internal BackdropSurface Retain() { references++; return this; }
    internal void Release() { if (--references == 0) Surface.Dispose(); }
}

/// <summary>A single-use lease; rejected or superseded preparations release their native surface.</summary>
internal sealed class PreparedBackdrop(BackdropSurface? surface, int preparationGeneration, CancellationToken cancellationToken) : IDisposable
{
    internal NavEntry? Owner { get; set; }
    internal int OwnerGeneration { get; set; }
    internal CancellationTokenSource? OwnerCancellation { get; set; }
    internal int PreparationGeneration { get; } = preparationGeneration;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal BackdropSurface? Surface { get; private set; } = surface;
    internal bool IsDisposed { get; private set; }
    internal BackdropSurface? TakeSurface() { var result = Surface; Surface = null; return result; }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        OwnerCancellation?.Dispose();
        OwnerCancellation = null;
        Surface?.Release();
        Surface = null;
    }
}
