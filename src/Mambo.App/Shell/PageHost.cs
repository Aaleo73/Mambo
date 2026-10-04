using System.Numerics;
using Mambo.App.Themes;
using Mambo.App.Views;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Shell;

public interface INavigablePage
{
    /// <summary>created 为 true 表示页面实例刚创建，需要从 entry 恢复视图状态。</summary>
    void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created);
    void OnNavigatedFrom(NavEntry entry);
    void Refresh();
}

/// <summary>逻辑导航立即提交；两张呈现页独立交接，第三个请求只保留最新目标。</summary>
public sealed partial class PageHost : Grid, IDisposable, IMotionParticipant
{
    private const int Capacity = 4;
    private readonly List<CachedPage> pages = [];
    private Func<Route, FrameworkElement>? factory;
    private Navigator? navigator;
    private BrowseTransitionCoordinator? transitions;
    private WindowMotionObserver? motion;
    private Action<Exception>? reportFailure;
    private CachedPage? current;
    private CachedPage? presented;
    private PagePair? pair;
    private PresentationRequest? requested;
    private WeakReference<DependencyObject>? parkedFocus;
    private bool restoreFocus;
    private bool showingError;
    private bool disposed;
    private bool clearing;
    private long generation;
    private Task pendingTransition = Task.CompletedTask;

    public FrameworkElement? CurrentPage => current?.Page;
    internal Task PendingTransition => pendingTransition;
    internal bool IsTransitioning => pair is not null || requested is { Finished: false };
    internal bool IsWaitingForPresentation => requested is { Ready: false, Finished: false };
    internal int CachedPageCount => showingError ? 0 : pages.Count;
    internal int PresentedPageCount => pair is not null ? 2 : presented is not null ? 1 : 0;
    internal FrameworkElement? PresentedPage => pair?.Target.Page ?? presented?.Page;
    internal FrameworkElement? OutgoingPage => pair?.Other(pair.Target).Page;
    internal FrameworkElement? PendingPage => requested is { Finished: false } request ? request.Target.Page : null;
    internal long PresentationGeneration => generation;
    internal Action? ParkFocus { get; set; }
    internal event Action<Exception>? DiagnosticFailure;

    public void Initialize(Func<Route, FrameworkElement> pageFactory, Navigator navigation,
        BrowseTransitionCoordinator transitions, Action<Exception>? failureReporter = null)
    {
        ArgumentNullException.ThrowIfNull(pageFactory);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(transitions);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (factory is not null) throw new InvalidOperationException("PageHost 已经初始化。");
        factory = pageFactory;
        navigator = navigation;
        this.transitions = transitions;
        reportFailure = failureReporter;
        transitions.Failed += OnBackgroundFailure;
        transitions.Window.ActiveChanged += OnWindowActiveChanged;
        motion = new WindowMotionObserver(transitions.Window, DispatcherQueue, OnAnimationsChanged);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Show(NavigatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (factory is null) throw new InvalidOperationException("PageHost 尚未初始化。");
        CachedPage? candidate = null;
        var leaving = true;
        var navigationGeneration = generation;
        try
        {
            if (showingError) Clear();
            CancelRequest();
            navigationGeneration = ++generation;
            ParkCurrentFocus();
            if (current is { } previous)
            {
                previous.Input.SetEnabled(false);
                Motion.SetActive(previous.Page, false);
                if (previous.Page is INavigablePage navigable && args.From is not null) navigable.OnNavigatedFrom(args.From);
            }
            if (navigationGeneration != generation) return;
            leaving = false;
            var kind = transitions!.BeginNavigation(args);
            var key = args.To.Route.Key;
            var index = pages.FindIndex(page => page.Key == key);
            var created = index < 0;
            if (created)
            {
                var page = factory(args.To.Route);
                try
                {
                    candidate = new CachedPage(key, page, args.To, OnBackgroundFailure);
                    SetVisual(page, 0, 0);
                    page.Visibility = Visibility.Collapsed;
                    candidate.Input.SetEnabled(false);
                    Motion.SetEntranceSuppressed(page, true);
                    Motion.SetActive(page, false);
                    pages.Add(candidate);
                    Children.Add(page);
                }
                catch
                {
                    if (candidate is null) ReleaseUntracked(page);
                    throw;
                }
            }
            else
            {
                candidate = pages[index];
                pages.RemoveAt(index);
                pages.Add(candidate);
                candidate.Entry = args.To;
            }
            current = candidate;
            candidate.Input.SetEnabled(false);
            Motion.SetEntranceSuppressed(candidate.Page, true);
            Motion.SetActive(candidate.Page, true);
            var immediate = presented is null && pair is null || args.Mode == NavigationMode.Reset ||
                args.Mode == NavigationMode.Replace && args.To.Route.Kind == PageKind.Search && args.From?.Route.Kind == PageKind.Search ||
                kind == BrowseTransitionKind.None || !CanAnimate;
            var request = new PresentationRequest(candidate, args.To, kind, generation);
            requested = request;
            pendingTransition = request.Completion.Task;
            HideUnusedCandidates();
            // A candidate can measure and restore its scroll offset, but never contributes a third
            // visible layer: its composition opacity is zero until the existing pair has finished.
            if (!IsPresented(candidate)) SetVisual(candidate.Page, 0, 0);
            candidate.Page.Visibility = Visibility.Visible;
            (candidate.Page as INavigablePage)?.OnNavigatedTo(args.To, args.Mode, created);
            if (!ReferenceEquals(requested, request)) return;
            if (immediate || ReferenceEquals(candidate, presented) && pair is null)
                SettleTransition();
            else
            {
                Evict();
                _ = PresentAsync(request);
            }
        }
        catch (Exception error)
        {
            ReportFailure(error);
            if (candidate is not null && !pages.Contains(candidate)) Release(candidate);
            if (navigationGeneration != generation) return;
            if (leaving && args.From is not null) ResetViewState(args.From);
            ShowError(args.To);
        }
    }

    private bool CanAnimate => IsLoaded && transitions?.Window.IsActive == true && motion?.AnimationsEnabled == true && Motion.IsActive(this);
    private bool IsPresented(CachedPage page) => ReferenceEquals(presented, page) || pair?.Contains(page) == true;
    private bool IsCurrent(PresentationRequest request) => !disposed && !clearing && ReferenceEquals(requested, request) && request.Generation == generation;

    private async Task PresentAsync(PresentationRequest request)
    {
        var token = request.Cancellation.Token;
        try
        {
            if (request.Target.Page is ITransitionReadyPage ready) await ready.WaitForPresentationAsync(token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(request)) return;
            if (!IsPresented(request.Target) && request.Target.Page is not ITransitionReadyPage)
            {
                using var frame = new PresentationFrame(request.Target.Page, token);
                request.Frame = frame;
                await frame.Completion;
                request.Frame = null;
            }
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(request)) return;
            request.Ready = true;
            while (IsCurrent(request))
            {
                if (!CanAnimate)
                {
                    SettleTransition();
                    return;
                }
                if (pair is { } running)
                {
                    if (running.Contains(request.Target)) RetargetPair(running, request.Target, request.Kind);
                    await running.Completion.Task.WaitAsync(token);
                    continue;
                }
                if (ReferenceEquals(presented, request.Target))
                {
                    CompleteRequest(request);
                    return;
                }
                if (presented is null)
                {
                    SettleTransition();
                    return;
                }
                StartPair(presented, request.Target, request.Kind);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrent(request)) { }
        catch (Exception error)
        {
            if (IsCurrent(request)) OnBackgroundFailure(error);
            else ReportFailure(error);
        }
        finally
        {
            try { request.Frame?.Dispose(); }
            catch (Exception error) { ReportFailure(error); }
            request.Frame = null;
        }
    }

    private void StartPair(CachedPage from, CachedPage to, BrowseTransitionKind kind)
    {
        Motion.SetEntranceSuppressed(from.Page, true);
        Motion.SetEntranceSuppressed(to.Page, true);
        from.Input.SetEnabled(false);
        to.Input.SetEnabled(false);
        SetVisual(to.Page, 0, kind == BrowseTransitionKind.DetailEnter ? 4 : 0);
        to.Page.Visibility = Visibility.Visible;
        var next = new PagePair(from, to);
        pair = next;
        AnimatePair(next, to, kind);
    }

    private void RetargetPair(PagePair running, CachedPage target, BrowseTransitionKind kind)
    {
        if (ReferenceEquals(running.Target, target)) return;
        // Detach only the old completion. Do not StopAnimation or write an endpoint before the
        // replacement keyframes: Composition starts these from the currently presented values.
        DetachBatch(running);
        AnimatePair(running, target, kind);
    }

    private void AnimatePair(PagePair running, CachedPage target, BrowseTransitionKind kind)
    {
        running.Target = target;
        var outgoing = running.Other(target);
        outgoing.Input.SetEnabled(false);
        target.Input.SetEnabled(ReferenceEquals(target, current) && Motion.IsActive(this));
        var compositor = ElementCompositionPreview.GetElementVisual(target.Page).Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        running.Batch = batch;
        batch.Completed += OnPairCompleted;
        Animate(target.Page, 1, 0, Motion.Content, Motion.EaseOut);
        var detail = kind is BrowseTransitionKind.DetailEnter or BrowseTransitionKind.DetailLeave;
        Animate(outgoing.Page, 0, kind == BrowseTransitionKind.DetailLeave ? 4 : 0,
            detail ? Motion.Exit : Motion.Content, detail ? Motion.EaseIn : Motion.EaseOut);
        batch.End();
    }

    private static void Animate(FrameworkElement page, float opacity, float y, TimeSpan duration, (Vector2, Vector2) spline)
    {
        var visual = ElementCompositionPreview.GetElementVisual(page);
        var compositor = visual.Compositor;
        using var easing = Motion.CreateEasing(compositor, spline);
        using var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opacity, easing);
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);
        using var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(1, new Vector3(0, y, 0), easing);
        translation.Duration = duration;
        visual.Properties.StartAnimation("Translation", translation);
    }

    private void OnPairCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (pair is not { } finished || !ReferenceEquals(finished.Batch, sender)) return;
        try
        {
            DetachBatch(finished);
            var incoming = finished.Target;
            var outgoing = finished.Other(incoming);
            SetVisual(incoming.Page, 1, 0);
            if (ReferenceEquals(outgoing, current))
            {
                // A reversal may still be restoring its target when the old batch ends.
                // Keep that logical target laid out and active instead of cancelling its readiness.
                SetVisual(outgoing.Page, 0, 0);
            }
            else Hide(outgoing);
            presented = incoming;
            pair = null;
            finished.Completion.TrySetResult();
            Evict();
        }
        catch (Exception error) { OnBackgroundFailure(error); }
    }

    private void CompleteRequest(PresentationRequest request)
    {
        if (!IsCurrent(request)) return;
        request.Ready = true;
        request.Finished = true;
        Motion.SetEntranceSuppressed(request.Target.Page, false);
        request.Target.Input.SetEnabled(Motion.IsActive(this));
        RestorePageFocus(request.Target);
        transitions!.CompleteNavigation(request.Entry);
        Evict();
        request.Completion.TrySetResult();
    }

    /// <summary>生命周期/禁动画直接呈现最新逻辑目标，不再等待数据或旧动画。</summary>
    internal void SettleTransition()
    {
        if (disposed || clearing) return;
        try
        {
            generation++;
            var completion = requested?.Completion;
            CancelRequest();
            StopPair();
            foreach (var page in pages)
            {
                if (ReferenceEquals(page, current))
                {
                    SetVisual(page.Page, 1, 0);
                    page.Page.Visibility = Visibility.Visible;
                    Motion.SetEntranceSuppressed(page.Page, false);
                    page.Input.SetEnabled(Motion.IsActive(this));
                }
                else Hide(page);
            }
            presented = current;
            if (current is { } target)
            {
                RestorePageFocus(target);
                transitions?.CompleteNavigation(target.Entry);
            }
            Evict();
            completion?.TrySetResult();
            pendingTransition = Task.CompletedTask;
        }
        catch (Exception error) { OnBackgroundFailure(error); }
    }

    void IMotionParticipant.SettleMotion() => SettleTransition();

    private static void SetVisual(FrameworkElement page, float opacity, float y)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        var visual = ElementCompositionPreview.GetElementVisual(page);
        visual.StopAnimation("Opacity");
        visual.Properties.StopAnimation("Translation");
        visual.Opacity = opacity;
        visual.Properties.InsertVector3("Translation", new Vector3(0, y, 0));
    }

    private static void Hide(CachedPage page)
    {
        page.Input.SetEnabled(false);
        Motion.SetActive(page.Page, false);
        SetVisual(page.Page, 0, 0);
        page.Page.Visibility = Visibility.Collapsed;
        Motion.SetEntranceSuppressed(page.Page, false);
    }

    private void HideUnusedCandidates()
    {
        foreach (var page in pages)
            if (!ReferenceEquals(page, current) && !IsPresented(page)) Hide(page);
    }

    private void ParkCurrentFocus()
    {
        if (current?.Input.CaptureFocus() != true) return;
        ParkFocus?.Invoke();
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        restoreFocus = focused is null || !PageInputScope.Contains(this, focused);
        parkedFocus = focused is null ? null : new(focused);
    }

    private void RestorePageFocus(CachedPage target)
    {
        if (!restoreFocus || !IsLoaded || !Motion.IsActive(this)) return;
        restoreFocus = false;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        // A user who moved to another sidebar item, modal or player while waiting owns the focus.
        if (focused is not null && (parkedFocus is null || !parkedFocus.TryGetTarget(out var parked) || !ReferenceEquals(focused, parked)))
        {
            parkedFocus = null;
            return;
        }
        parkedFocus = null;
        if (!target.Input.RestoreFocus()) ParkFocus?.Invoke();
    }

    private void CancelRequest()
    {
        var old = requested;
        requested = null;
        if (old is null) return;
        try { old.Frame?.Dispose(); }
        catch (Exception error) { ReportFailure(error); }
        old.Frame = null;
        try { old.Cancellation.Cancel(); }
        catch (Exception error) { ReportFailure(error); }
        finally { old.Cancellation.Dispose(); }
        old.Finished = true;
        old.Completion.TrySetResult();
    }

    private void StopPair()
    {
        if (pair is not { } old) return;
        pair = null;
        try { DetachBatch(old); }
        catch (Exception error) { ReportFailure(error); }
        try { SetVisual(old.First.Page, 0, 0); }
        catch (Exception error) { ReportFailure(error); }
        try { SetVisual(old.Second.Page, 0, 0); }
        catch (Exception error) { ReportFailure(error); }
        old.Completion.TrySetResult();
    }

    private void DetachBatch(PagePair owner)
    {
        if (owner.Batch is not { } batch) return;
        owner.Batch = null;
        batch.Completed -= OnPairCompleted;
        batch.Dispose();
    }

    public void RefreshCurrent()
    {
        if (showingError) { navigator?.RetryCurrent(); return; }
        try { (current?.Page as INavigablePage)?.Refresh(); }
        catch (Exception error) { OnBackgroundFailure(error); }
    }

    /// <summary>账号变化或错误边界丢弃所有页面；保留宿主，允许下一次 Show。</summary>
    public void Clear()
    {
        if (clearing) return;
        clearing = true;
        generation++;
        try
        {
            try { ParkCurrentFocus(); }
            catch (Exception error) { ReportFailure(error); }
            CancelRequest();
            StopPair();
            var discarded = pages.ToArray();
            pages.Clear();
            current = null;
            presented = null;
            restoreFocus = false;
            parkedFocus = null;
            showingError = false;
            pendingTransition = Task.CompletedTask;
            try { transitions?.Clear(); }
            catch (Exception error) { ReportFailure(error); }
            foreach (var page in discarded) Release(page);
        }
        finally { clearing = false; }
    }

    private void ShowError(NavEntry entry)
    {
        if (disposed || clearing) return;
        Clear();
        ResetViewState(entry);
        showingError = true;
        Action retry = () => navigator?.RetryCurrent();
        Action home = () =>
        {
            if (navigator is null) return;
            if (navigator.Current.Route.Equals(Route.Home)) navigator.RetryCurrent();
            else navigator.Navigate(Route.Home);
        };
        FrameworkElement recovery;
        try { recovery = new ErrorPage(retry, home); }
        catch (Exception error)
        {
            ReportFailure(error);
            // 自定义资源本身失败时，仍使用系统样式保留恢复操作，不再调用页面工厂。
            var homeButton = new Button { Content = "返回首页" };
            var retryButton = new Button { Content = "重试" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(homeButton, "返回首页");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(retryButton, "重试页面");
            homeButton.Click += (_, _) => home();
            retryButton.Click += (_, _) => retry();
            recovery = new UserControl
            {
                Content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "出了点问题", FontSize = 20 },
                        new TextBlock { Text = "页面暂时无法显示，请重试或返回首页。", TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
                        homeButton,
                        retryButton,
                    },
                },
            };
        }
        var errorPage = new CachedPage(entry.Route.Key, recovery, entry, ReportFailure);
        pages.Add(errorPage);
        current = presented = errorPage;
        errorPage.Input.SetEnabled(Motion.IsActive(this));
        Motion.SetActive(recovery, Motion.IsActive(this));
        Children.Add(recovery);
    }

    private static void ResetViewState(NavEntry entry)
    {
        entry.ViewState = null;
        entry.VerticalOffset = 0;
    }

    private void Release(CachedPage page)
    {
        if (page.Released) return;
        page.Released = true;
        try { page.Input.Dispose(); }
        catch (Exception error) { ReportFailure(error); }
        ReleaseUntracked(page.Page);
    }

    private void ReleaseUntracked(FrameworkElement page)
    {
        try { Motion.SetActive(page, false); }
        catch (Exception error) { ReportFailure(error); }
        try { page.Visibility = Visibility.Collapsed; }
        catch (Exception error) { ReportFailure(error); }
        try { Children.Remove(page); }
        catch (Exception error) { ReportFailure(error); }
        try { (page as IDisposable)?.Dispose(); }
        catch (Exception error) { ReportFailure(error); }
    }

    private void OnBackgroundFailure(Exception error)
    {
        ReportFailure(error);
        if (clearing || disposed || navigator is null) return;
        try { ShowError(navigator.Current); }
        catch (Exception recoveryError)
        {
            ReportFailure(recoveryError);
            try { Clear(); }
            catch (Exception cleanupError) { ReportFailure(cleanupError); }
        }
    }

    private void ReportFailure(Exception error)
    {
        try { DiagnosticFailure?.Invoke(error); }
        catch (Exception) { }
        try { reportFailure?.Invoke(error); }
        catch (Exception) { }
    }

    private void Evict()
    {
        while (!showingError && pages.Count > Capacity)
        {
            var victim = pages.FindIndex(page => !ReferenceEquals(page, current) && !IsPresented(page) && page.Key != Route.Home.Key);
            if (victim < 0) return;
            var page = pages[victim];
            pages.RemoveAt(victim);
            Release(page);
        }
    }

    private void OnAnimationsChanged(bool enabled) { if (!enabled) SettleTransition(); }
    private void OnWindowActiveChanged(object? sender, EventArgs args)
    {
        if (transitions?.Window.IsActive != true) SettleTransition();
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (disposed || transitions is null) return;
        motion ??= new WindowMotionObserver(transitions.Window, DispatcherQueue, OnAnimationsChanged);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        SettleTransition();
        motion?.Dispose();
        motion = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear();
        disposed = true;
        motion?.Dispose();
        motion = null;
        if (transitions is not null)
        {
            transitions.Failed -= OnBackgroundFailure;
            transitions.Window.ActiveChanged -= OnWindowActiveChanged;
        }
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ParkFocus = null;
        factory = null;
        reportFailure = null;
        transitions = null;
        navigator = null;
        DiagnosticFailure = null;
    }

    private sealed class CachedPage(string key, FrameworkElement page, NavEntry entry, Action<Exception> failed)
    {
        public string Key { get; } = key;
        public FrameworkElement Page { get; } = page;
        public NavEntry Entry { get; set; } = entry;
        public PageInputScope Input { get; } = new(page, failed);
        public bool Released { get; set; }
    }

    private sealed class PresentationRequest(CachedPage target, NavEntry entry, BrowseTransitionKind kind, long generation)
    {
        public CachedPage Target { get; } = target;
        public NavEntry Entry { get; } = entry;
        public BrowseTransitionKind Kind { get; } = kind;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PresentationFrame? Frame { get; set; }
        public bool Ready { get; set; }
        public bool Finished { get; set; }
    }

    private sealed class PagePair(CachedPage first, CachedPage second)
    {
        public CachedPage First { get; } = first;
        public CachedPage Second { get; } = second;
        public CachedPage Target { get; set; } = second;
        public CompositionScopedBatch? Batch { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Contains(CachedPage page) => ReferenceEquals(page, First) || ReferenceEquals(page, Second);
        public CachedPage Other(CachedPage page) => ReferenceEquals(page, First) ? Second : First;
    }

    /// <summary>等待 XAML 完成一次真实布局帧；取消立即退订全局 Rendering。</summary>
    private sealed class PresentationFrame : IDisposable
    {
        private readonly FrameworkElement page;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration cancellation;
        private bool disposed;

        public PresentationFrame(FrameworkElement page, CancellationToken token)
        {
            this.page = page;
            CompositionTarget.Rendering += OnRendering;
            cancellation = token.Register(Dispose);
        }

        public Task Completion => completion.Task;

        private void OnRendering(object? sender, object args)
        {
            if (disposed || !page.IsLoaded || page.XamlRoot is null || page.ActualWidth <= 0 || page.ActualHeight <= 0) return;
            completion.TrySetResult();
            Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CompositionTarget.Rendering -= OnRendering;
            cancellation.Dispose();
            completion.TrySetCanceled();
        }
    }
}
