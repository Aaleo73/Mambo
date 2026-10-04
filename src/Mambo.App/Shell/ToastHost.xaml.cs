using System.Collections.Specialized;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Shell;

public sealed partial class ToastHost : UserControl, IDisposable, IMotionParticipant
{
    private const int Capacity = 3;
    private readonly ToastService toasts;
    private readonly WindowContext window;
    private readonly WindowMotionObserver motionObserver;
    private readonly DataTemplate toastTemplate;
    private readonly List<PresentedToast> presented = new(Capacity);
    private long exitOrder;
    private bool loaded;
    private bool disposed;

    public ToastHost(ToastService toasts, WindowContext window)
    {
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(window);
        this.toasts = toasts;
        this.window = window;
        InitializeComponent();
        toastTemplate = XamlResources.Template(Resources, "ToastTemplate");
        motionObserver = new WindowMotionObserver(window, DispatcherQueue, OnAnimationsChanged);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        window.ActiveChanged += OnWindowActiveChanged;
        toasts.Items.CollectionChanged += OnItemsChanged;
        Synchronize(false);
    }

    internal int PresentedCount => presented.Count;
    internal int ExitingCount
    {
        get
        {
            var count = 0;
            foreach (var entry in presented)
                if (entry.Exiting) count++;
            return count;
        }
    }
    internal IReadOnlyList<ToastItem> PresentedIdentities => presented.Select(static entry => entry.Item).ToArray();
    internal Task PendingTransition => presented.Count == 0 ? Task.CompletedTask
        : Task.WhenAll(presented.Select(static entry => entry.Completion?.Task ?? Task.CompletedTask));
    internal FrameworkElement? GetPresentedNode(ToastItem item) => Find(item)?.Panel;

    private PresentedToast? Find(ToastItem item)
    {
        foreach (var entry in presented)
            if (ReferenceEquals(entry.Item, item)) return entry;
        return null;
    }

    private PresentedToast? Find(ContentPresenter panel)
    {
        foreach (var entry in presented)
            if (ReferenceEquals(entry.Panel, panel)) return entry;
        return null;
    }

    private bool CanAnimate => loaded && window.IsActive && motionObserver.AnimationsEnabled
        && Motion.IsActive(this) && !Motion.IsEntranceSuppressed(this);

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!disposed) Synchronize(CanAnimate);
    }

    private void Synchronize(bool animate)
    {
        for (var index = presented.Count - 1; index >= 0; index--)
        {
            var entry = presented[index];
            if (!entry.Exiting && !toasts.Items.Contains(entry.Item)) Retire(entry, animate);
        }
        foreach (var item in toasts.Items)
        {
            var existing = Find(item);
            if (existing is { Exiting: true }) Finish(existing);
            if (existing is null || existing.Exiting) Present(item, animate);
        }
    }

    private void Present(ToastItem item, bool animate)
    {
        while (presented.Count >= Capacity)
        {
            PresentedToast? oldestExit = null;
            foreach (var candidate in presented)
                if (candidate.Exiting && (oldestExit is null || candidate.ExitOrder < oldestExit.ExitOrder))
                    oldestExit = candidate;
            Finish(oldestExit ?? presented[0]);
        }

        var panel = new ContentPresenter { Content = item, ContentTemplate = toastTemplate };
        var entry = new PresentedToast(item, panel, new PopupTransition(panel)) { EnterOnLoad = animate };
        panel.Loaded += OnToastLoaded;
        panel.Unloaded += OnToastUnloaded;
        panel.GettingFocus += OnToastGettingFocus;
        presented.Add(entry);
        if (animate)
        {
            entry.Completion = NewCompletion();
            // The first layout is already transparent; Loaded starts only this toast's entrance.
            panel.Visibility = Visibility.Visible;
        }
        else entry.Transition.Settle(true);
        ToastStack.Children.Add(panel);
    }

    private void OnToastLoaded(object sender, RoutedEventArgs args)
    {
        if (disposed || sender is not ContentPresenter panel || Find(panel) is not { } entry) return;
        if (entry.Exiting) ExcludeInput(entry.Panel);
        else if (entry.EnterOnLoad)
        {
            entry.EnterOnLoad = false;
            var generation = ++entry.Generation;
            var completion = entry.Completion ??= NewCompletion();
            ObserveTransition(entry, generation, completion, entry.Transition.OpenAsync(CanAnimate));
        }
    }

    private void Retire(PresentedToast entry, bool animate)
    {
        entry.Exiting = true;
        entry.ExitOrder = ++exitOrder;
        entry.EnterOnLoad = false;
        entry.Panel.IsHitTestVisible = false;
        ExcludeInput(entry.Panel);
        ResumeHover(entry);
        ToastStack.LayoutUpdated -= OnRetiringLayoutUpdated;
        ToastStack.LayoutUpdated += OnRetiringLayoutUpdated;
        if (loaded && XamlRoot is { } root && PageInputScope.Contains(entry.Panel, FocusManager.GetFocusedElement(root) as DependencyObject))
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = root.Content });
        if (!animate || !entry.Panel.IsLoaded)
        {
            Finish(entry);
            return;
        }

        entry.Completion?.TrySetResult();
        var completion = entry.Completion = NewCompletion();
        var generation = ++entry.Generation;
        ObserveTransition(entry, generation, completion, entry.Transition.CloseAsync(true));
    }

    // PopupTransition tasks cannot fault; unexpected UI cleanup errors still reach the UI exception boundary.
    private async void ObserveTransition(PresentedToast entry, long generation, TaskCompletionSource completion, Task transition)
    {
        try
        {
            await transition;
            if (disposed || entry.Generation != generation || !presented.Contains(entry)) return;
            if (entry.Exiting) Finish(entry);
            else entry.Completion = null;
        }
        finally { completion.TrySetResult(); }
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Finish(PresentedToast entry)
    {
        if (!presented.Remove(entry)) return;
        entry.Generation++;
        entry.Completion?.TrySetResult();
        entry.Completion = null;
        entry.Panel.IsHitTestVisible = false;
        ExcludeInput(entry.Panel);
        ResumeHover(entry);
        entry.Transition.Dispose();
        entry.Panel.Loaded -= OnToastLoaded;
        entry.Panel.Unloaded -= OnToastUnloaded;
        entry.Panel.GettingFocus -= OnToastGettingFocus;
        DetachTemplateHandlers(entry.Panel);
        ToastStack.Children.Remove(entry.Panel);
        entry.Panel.Content = null;
        entry.Panel.ContentTemplate = null;
        if (ExitingCount == 0) ToastStack.LayoutUpdated -= OnRetiringLayoutUpdated;
    }

    private void DetachTemplateHandlers(DependencyObject node)
    {
        if (node is FrameworkElement element) element.Tag = null;
        if (node is Border border)
        {
            border.PointerEntered -= OnPointerEntered;
            border.PointerExited -= OnPointerExited;
        }
        if (node is Button button)
        {
            button.Click -= OnActionClick;
            button.Click -= OnCloseClick;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            DetachTemplateHandlers(VisualTreeHelper.GetChild(node, index));
    }

    private static void ExcludeInput(DependencyObject node)
    {
        AutomationProperties.SetAccessibilityView(node, AccessibilityView.Raw);
        if (node is Control control) control.IsTabStop = false;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            ExcludeInput(VisualTreeHelper.GetChild(node, index));
    }

    private void OnRetiringLayoutUpdated(object? sender, object args)
    {
        foreach (var entry in presented)
            if (entry.Exiting) ExcludeInput(entry.Panel);
    }

    private void OnToastGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (sender is not ContentPresenter panel || Find(panel) is not { } entry || (!disposed && !entry.Exiting)) return;
        args.TryCancel();
        args.Handled = true;
    }

    private PresentedToast? ActiveEntry(object sender)
    {
        if (disposed || sender is not DependencyObject node) return null;
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ContentPresenter panel && Find(panel) is { } entry)
                return !entry.Exiting && toasts.Items.Contains(entry.Item) ? entry : null;
        return null;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (ActiveEntry(sender) is not { } entry) return;
        entry.Hovered = true;
        ToastService.Pause(entry.Item);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (ActiveEntry(sender) is { } entry) ResumeHover(entry);
    }

    private static void ResumeHover(PresentedToast entry)
    {
        if (!entry.Hovered) return;
        entry.Hovered = false;
        ToastService.Resume(entry.Item);
    }

    private void OnActionClick(object sender, RoutedEventArgs args)
    {
        if (ActiveEntry(sender) is { } entry) toasts.Invoke(entry.Item);
    }

    private void OnCloseClick(object sender, RoutedEventArgs args)
    {
        if (ActiveEntry(sender) is { } entry) toasts.Dismiss(entry.Item);
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => loaded = !disposed;

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        loaded = false;
        SettleTransitions();
        foreach (var entry in presented) ResumeHover(entry);
    }

    private void OnToastUnloaded(object sender, RoutedEventArgs args)
    {
        if (disposed || sender is not ContentPresenter panel || Find(panel) is not { } entry) return;
        if (entry.Exiting) Finish(entry);
        else
        {
            SettleVisible(entry);
            ResumeHover(entry);
        }
    }

    private static void SettleVisible(PresentedToast entry)
    {
        entry.Generation++;
        entry.EnterOnLoad = false;
        entry.Transition.Settle(true);
        entry.Completion?.TrySetResult();
        entry.Completion = null;
    }

    private void SettleTransitions()
    {
        for (var index = presented.Count - 1; index >= 0; index--)
        {
            var entry = presented[index];
            if (entry.Exiting) Finish(entry);
            else SettleVisible(entry);
        }
    }

    private void OnAnimationsChanged(bool enabled) { if (!enabled) SettleTransitions(); }
    private void OnWindowActiveChanged(object? sender, EventArgs args) { if (!window.IsActive) SettleTransitions(); }
    void IMotionParticipant.SettleMotion() => SettleTransitions();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        loaded = false;
        toasts.Items.CollectionChanged -= OnItemsChanged;
        window.ActiveChanged -= OnWindowActiveChanged;
        motionObserver.Dispose();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ToastStack.LayoutUpdated -= OnRetiringLayoutUpdated;
        while (presented.Count > 0) Finish(presented[^1]);
        Content = null;
    }

    private sealed class PresentedToast(ToastItem item, ContentPresenter panel, PopupTransition transition)
    {
        internal ToastItem Item { get; } = item;
        internal ContentPresenter Panel { get; } = panel;
        internal PopupTransition Transition { get; } = transition;
        internal TaskCompletionSource? Completion;
        internal long Generation;
        internal long ExitOrder;
        internal bool EnterOnLoad;
        internal bool Exiting;
        internal bool Hovered;
    }
}
