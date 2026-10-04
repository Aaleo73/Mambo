using Mambo.App.Themes;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mambo.App.Shell;

/// <summary>暗幕与卡片独立呈现；退场完成前仍持有模态输入和请求所有权。</summary>
public sealed partial class DialogHost : UserControl, IDialogPresenter, IDisposable, IMotionParticipant
{
    private readonly PopupTransition transition;
    private readonly Visual scrimVisual;
    private TaskCompletionSource<bool>? pending;
    private TaskCompletionSource? scrimCompletion;
    private CompositionScopedBatch? scrimBatch;
    private WeakReference<Control>? previousFocus;
    private Control? preferredFocus;
    private UIElement? focusRoot;
    private WindowContext? window;
    private WindowMotionObserver? motionObserver;
    private long generation;
    private bool closing;
    private bool closeResult;
    private bool scrimTarget;
    private bool disposed;

    public DialogHost()
    {
        InitializeComponent();
        transition = new PopupTransition(Panel);
        scrimVisual = ElementCompositionPreview.GetElementVisual(Scrim);
        scrimVisual.Opacity = 0;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void Initialize(WindowContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (motionObserver is not null) return;
        window = context;
        motionObserver = new WindowMotionObserver(context, DispatcherQueue, OnAnimationsChanged);
        context.ActiveChanged += OnWindowActiveChanged;
    }

    internal bool IsClosing => closing;
    internal bool IsPresented => pending is not null;
    internal bool FocusFenceActive => focusRoot is not null;
    internal Task PendingTransition { get; private set; } = Task.CompletedTask;
    private bool CanAnimate => motionObserver?.AnimationsEnabled == true && window?.IsActive == true;

    public Task<bool> PresentAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (disposed) return Task.FromResult(false);
        if (pending is not null) throw new InvalidOperationException("确认请求必须顺序呈现。");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion;
        generation++;
        closing = false;
        TitleText.Text = request.Title;
        BodyText.Text = request.Text;
        BodyText.Visibility = string.IsNullOrEmpty(request.Text) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = request.CancelText;
        CancelButton.Visibility = request.CancelText is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = request.ConfirmText;
        DangerButton.Content = request.ConfirmText;
        ConfirmButton.Visibility = request.Danger ? Visibility.Collapsed : Visibility.Visible;
        DangerButton.Visibility = request.Danger ? Visibility.Visible : Visibility.Collapsed;
        preferredFocus = request.Danger && request.CancelText is not null ? CancelButton : request.Danger ? DangerButton : ConfirmButton;
        previousFocus = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Control focused ? new(focused) : null;
        Panel.IsHitTestVisible = true;
        SetButtonTabStops(true);
        Visibility = Visibility.Visible;
        AttachFocusFence();
        OpenPresentation();
        Panel.UpdateLayout();
        FocusDefault();
        return completion.Task;
    }

    private async void OpenPresentation()
    {
        var version = generation;
        var card = transition.OpenAsync(CanAnimate);
        var shade = FadeScrim(true, CanAnimate);
        await (PendingTransition = AwaitBothAsync(card, shade));
        if (!disposed && version == generation && pending is not null && !closing) FocusDefault();
    }

    private async void Close(bool result)
    {
        if (disposed || pending is null || closing) return;
        closing = true;
        closeResult = result;
        var version = ++generation;
        Panel.IsHitTestVisible = false;
        SetButtonTabStops(false);
        Focus(FocusState.Programmatic);
        await (PendingTransition = ClosePresentationAsync(version));
    }

    private async Task ClosePresentationAsync(long version)
    {
        var card = transition.CloseAsync(CanAnimate);
        var shade = FadeScrim(false, CanAnimate);
        await card;
        await shade;
        if (!disposed && version == generation && closing) CompleteClose(closeResult, restoreFocus: true);
    }

    private static async Task AwaitBothAsync(Task first, Task second)
    {
        await first;
        await second;
    }

    private Task FadeScrim(bool visible, bool animate)
    {
        if (!animate)
        {
            SettleScrim(visible);
            return Task.CompletedTask;
        }
        var superseded = scrimCompletion;
        ReleaseScrimBatch();
        scrimTarget = visible;
        scrimCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = scrimCompletion.Task;
        superseded?.TrySetResult();
        var compositor = scrimVisual.Compositor;
        scrimBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        scrimBatch.Completed += OnScrimCompleted;
        using var fade = compositor.CreateScalarKeyFrameAnimation();
        using var easing = Motion.CreateEasing(compositor, visible ? Motion.EaseOut : Motion.EaseIn);
        fade.Duration = visible ? Motion.Feedback : Motion.Exit;
        fade.InsertKeyFrame(1, visible ? 1 : 0, easing);
        scrimVisual.StartAnimation("Opacity", fade);
        scrimBatch.End();
        return result;
    }

    private void OnScrimCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (disposed || !ReferenceEquals(sender, scrimBatch)) return;
        SettleScrim(scrimTarget);
    }

    private void SettleScrim(bool visible)
    {
        ReleaseScrimBatch();
        scrimTarget = visible;
        scrimVisual.StopAnimation("Opacity");
        scrimVisual.Opacity = visible ? 1 : 0;
        var finished = scrimCompletion;
        scrimCompletion = null;
        finished?.TrySetResult();
    }

    private void ReleaseScrimBatch()
    {
        if (scrimBatch is null) return;
        scrimBatch.Completed -= OnScrimCompleted;
        scrimBatch.Dispose();
        scrimBatch = null;
    }

    private void CompleteClose(bool result, bool restoreFocus)
    {
        var completion = pending;
        pending = null;
        closing = false;
        generation++;
        Visibility = Visibility.Collapsed;
        DetachFocusFence();
        preferredFocus = null;
        if (restoreFocus && window?.IsActive != false)
        {
            var restored = previousFocus is not null && previousFocus.TryGetTarget(out var prior) && CanRestore(prior) && prior.Focus(FocusState.Programmatic);
            if (!restored && XamlRoot?.Content is ShellView shell) shell.Sidebar.FocusNavigation();
        }
        previousFocus = null;
        // The service may show its next queued request only after the old modal and focus fence are gone.
        completion?.TrySetResult(result);
    }

    private bool CanRestore(Control control)
    {
        if (!control.IsLoaded || !control.IsEnabled || !control.IsTabStop || !ReferenceEquals(control.XamlRoot, XamlRoot)) return false;
        for (DependencyObject? node = control; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed } || node is Control { IsEnabled: false }) return false;
        return true;
    }

    private void SetButtonTabStops(bool enabled)
    {
        CancelButton.IsTabStop = enabled && CancelButton.Visibility == Visibility.Visible;
        ConfirmButton.IsTabStop = enabled && ConfirmButton.Visibility == Visibility.Visible;
        DangerButton.IsTabStop = enabled && DangerButton.Visibility == Visibility.Visible;
    }

    private void AttachFocusFence()
    {
        if (focusRoot is not null || XamlRoot?.Content is not UIElement root) return;
        focusRoot = root;
        focusRoot.GettingFocus += OnRootGettingFocus;
    }

    private void DetachFocusFence()
    {
        if (focusRoot is not null) focusRoot.GettingFocus -= OnRootGettingFocus;
        focusRoot = null;
    }

    private void OnRootGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (pending is null || PageInputScope.Contains(this, args.NewFocusedElement)) return;
        var target = closing ? this : preferredFocus ?? this;
        if (!args.TrySetNewFocusedElement(target)) args.TryCancel();
        args.Handled = true;
    }

    private void FocusDefault()
    {
        if (pending is null || closing || XamlRoot is null) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (PageInputScope.Contains(Panel, focused)) return;
        if (preferredFocus?.Focus(FocusState.Programmatic) != true) Focus(FocusState.Programmatic);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (disposed || pending is null) return;
        AttachFocusFence();
        FocusDefault();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (disposed || pending is null) return;
        transition.Settle(false);
        SettleScrim(false);
        CompleteClose(false, restoreFocus: false);
    }

    private void SettlePresentation()
    {
        if (disposed) return;
        var visible = pending is not null && !closing;
        transition.Settle(visible);
        SettleScrim(visible);
        if (closing) CompleteClose(closeResult, restoreFocus: true);
    }

    void IMotionParticipant.SettleMotion() => SettlePresentation();
    private void OnAnimationsChanged(bool enabled) { if (!enabled) SettlePresentation(); }
    private void OnWindowActiveChanged(object? sender, EventArgs args) { if (window?.IsActive == false) SettlePresentation(); }
    private void OnConfirmClick(object sender, RoutedEventArgs args) => Close(true);
    private void OnCancelClick(object sender, RoutedEventArgs args) => Close(false);
    private void OnScrimTapped(object sender, TappedRoutedEventArgs args) => Close(false);
    private void OnPanelTapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (pending is null || args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        Close(false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        transition.Dispose();
        SettleScrim(false);
        CompleteClose(false, restoreFocus: false);
        motionObserver?.Dispose();
        if (window is not null) window.ActiveChanged -= OnWindowActiveChanged;
        window = null;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        KeyDown -= OnPanelKeyDown;
        ModalRoot.Tapped -= OnScrimTapped;
        Panel.Tapped -= OnPanelTapped;
        CancelButton.Click -= OnCancelClick;
        ConfirmButton.Click -= OnConfirmClick;
        DangerButton.Click -= OnConfirmClick;
        Content = null;
    }
}
