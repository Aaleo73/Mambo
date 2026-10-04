using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Shell;

/// <summary>保留退场画面的同时隔离输入、Tab 和自动化，不使用会改变颜色的 IsEnabled。</summary>
internal sealed class PageInputScope : IDisposable
{
    private readonly FrameworkElement page;
    private readonly Action<Exception> failed;
    private readonly Dictionary<DependencyObject, SavedState> saved = [];
    private readonly bool hitTestVisible;
    private WeakReference<Control>? previousFocus;
    private bool enabled = true;
    private bool updating;
    private bool disposed;

    public PageInputScope(FrameworkElement page, Action<Exception> failed)
    {
        this.page = page;
        this.failed = failed;
        hitTestVisible = page.IsHitTestVisible;
        page.GettingFocus += OnGettingFocus;
    }

    public bool CaptureFocus()
    {
        if (page.XamlRoot is null || FocusManager.GetFocusedElement(page.XamlRoot) is not DependencyObject focused || !Contains(page, focused))
            return false;
        if (focused is Control control) previousFocus = new(control);
        return true;
    }

    public void SetEnabled(bool value)
    {
        if (disposed || enabled == value) return;
        enabled = value;
        page.IsHitTestVisible = value && hitTestVisible;
        if (value)
        {
            page.LayoutUpdated -= OnLayoutUpdated;
            page.Loaded -= OnLoaded;
            Restore();
        }
        else
        {
            // Composition opacity/translation never causes layout. This hook only discovers controls
            // materialized by real XAML layout (including nested repeaters and ItemsControl templates).
            page.LayoutUpdated += OnLayoutUpdated;
            page.Loaded += OnLoaded;
            CaptureTree(page);
        }
    }

    public bool RestoreFocus()
    {
        if (!enabled || disposed || !page.IsLoaded) return false;
        if (previousFocus is not null && previousFocus.TryGetTarget(out var previous) && CanFocus(previous) && previous.Focus(FocusState.Programmatic))
            return true;
        return FocusManager.FindFirstFocusableElement(page) is Control first && CanFocus(first) && first.Focus(FocusState.Programmatic);
    }

    private bool CanFocus(Control control)
    {
        if (!control.IsLoaded || !control.IsEnabled || !control.IsTabStop || !ReferenceEquals(control.XamlRoot, page.XamlRoot)) return false;
        for (DependencyObject? node = control; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (ReferenceEquals(node, page)) return true;
        }
        return false;
    }

    internal static bool Contains(DependencyObject root, DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(root, node)) return true;
        return false;
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (enabled || disposed) return;
        args.TryCancel();
        args.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => Discover();
    private void OnLayoutUpdated(object? sender, object args)
    {
        if (page.Visibility == Visibility.Visible) Discover();
    }

    private void Discover()
    {
        if (enabled || disposed || updating) return;
        try { CaptureTree(page); }
        catch (Exception error) { failed(error); }
    }

    private void CaptureTree(DependencyObject node)
    {
        if (!saved.ContainsKey(node))
        {
            var state = new SavedState(AutomationProperties.GetAccessibilityView(node), node is Control control && control.IsTabStop);
            saved.Add(node, state);
            updating = true;
            try
            {
                AutomationProperties.SetAccessibilityView(node, AccessibilityView.Raw);
                if (node is Control target) target.IsTabStop = false;
            }
            finally { updating = false; }
            state.AutomationToken = node.RegisterPropertyChangedCallback(AutomationProperties.AccessibilityViewProperty, OnPropertyChanged);
            if (node is Control)
                state.TabToken = node.RegisterPropertyChangedCallback(Control.IsTabStopProperty, OnPropertyChanged);
            if (node is FrameworkElement element) element.Unloaded += OnElementUnloaded;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) CaptureTree(VisualTreeHelper.GetChild(node, index));
    }

    private void OnPropertyChanged(DependencyObject node, DependencyProperty property)
    {
        if (updating || enabled || disposed || !saved.TryGetValue(node, out var state)) return;
        try
        {
            updating = true;
            if (property == Control.IsTabStopProperty && node is Control control)
            {
                state.TabStop = control.IsTabStop;
                control.IsTabStop = false;
            }
            else
            {
                state.Accessibility = AutomationProperties.GetAccessibilityView(node);
                AutomationProperties.SetAccessibilityView(node, AccessibilityView.Raw);
            }
        }
        catch (Exception error) { failed(error); }
        finally { updating = false; }
    }

    private void OnElementUnloaded(object sender, RoutedEventArgs args)
    {
        // Recycled controls must leave with their own values, not a previous page's disabled state.
        try
        {
            if (sender is DependencyObject node && saved.Remove(node, out var state)) RestoreNode(node, state);
        }
        catch (Exception error) { failed(error); }
    }

    private void RestoreNode(DependencyObject node, SavedState state)
    {
        node.UnregisterPropertyChangedCallback(AutomationProperties.AccessibilityViewProperty, state.AutomationToken);
        if (node is Control control)
        {
            node.UnregisterPropertyChangedCallback(Control.IsTabStopProperty, state.TabToken);
            control.IsTabStop = state.TabStop;
        }
        AutomationProperties.SetAccessibilityView(node, state.Accessibility);
        if (node is FrameworkElement element) element.Unloaded -= OnElementUnloaded;
    }

    private void Restore()
    {
        foreach (var (node, state) in saved)
        {
            RestoreNode(node, state);
        }
        saved.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        page.GettingFocus -= OnGettingFocus;
        page.LayoutUpdated -= OnLayoutUpdated;
        page.Loaded -= OnLoaded;
        Restore();
        page.IsHitTestVisible = hitTestVisible;
        previousFocus = null;
    }

    private sealed class SavedState(AccessibilityView accessibility, bool tabStop)
    {
        public AccessibilityView Accessibility = accessibility;
        public bool TabStop = tabStop;
        public long AutomationToken;
        public long TabToken;
    }
}
