using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT;

namespace Mambo.App.Themes;

/// <summary>只替换原生插入光标的画刷，位置、宽度、闪烁和输入法仍由 WinUI 管理。</summary>
public static partial class InputCaret
{
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(SolidColorBrush), typeof(InputCaret), new PropertyMetadata(null, OnBrushChanged));

    public static SolidColorBrush? GetBrush(DependencyObject element) => (SolidColorBrush?)element.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject element, SolidColorBrush? value) => element.SetValue(BrushProperty, value);

    private static readonly ConditionalWeakTable<Control, State> States = [];

    private static void OnBrushChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Control control || control is not (TextBox or PasswordBox)) return;
        if (args.NewValue is not SolidColorBrush)
        {
            if (States.TryGetValue(control, out var previous)) previous.Detach();
            States.Remove(control);
            return;
        }
        if (!States.TryGetValue(control, out var state))
        {
            state = new State(control);
            States.Add(control, state);
        }
        state.Refresh();
    }

    private sealed class State
    {
        // WinUI 的内部 DestInvert 值；不是可设置的公共主题资源。结构不匹配时保留原生光标。
        private const ElementCompositeMode NativeCaretCompositeMode = (ElementCompositeMode)3;
        private readonly Control control;
        private readonly long focusToken;
        private Shape? caret;
        private Brush? originalFill;
        private bool observingLayout;

        public State(Control control)
        {
            this.control = control;
            control.Loaded += OnLoaded;
            control.Unloaded += OnStopped;
            // FocusState 同步通知；延迟到达的 GotFocus/LostFocus 不能覆盖较新的焦点状态。
            focusToken = control.RegisterPropertyChangedCallback(Control.FocusStateProperty, OnFocusChanged);
        }

        public void Detach()
        {
            Stop();
            control.Loaded -= OnLoaded;
            control.Unloaded -= OnStopped;
            control.UnregisterPropertyChangedCallback(Control.FocusStateProperty, focusToken);
        }

        public void Refresh()
        {
            if (!control.IsLoaded || control.FocusState == FocusState.Unfocused)
            {
                Stop();
                return;
            }
            if (!observingLayout)
            {
                // 光标在首次聚焦后才创建；RTL 方向切换或重新应用模板也会重建它。
                control.LayoutUpdated += OnLayoutUpdated;
                observingLayout = true;
            }
            Update();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Refresh();
        private void OnStopped(object sender, RoutedEventArgs args) => Stop();
        private void OnFocusChanged(DependencyObject sender, DependencyProperty property) => Refresh();
        private void OnLayoutUpdated(object? sender, object args) => Refresh();

        private void Update()
        {
            Shape? current = null;
            if (FindCaretShape(control) is { } shape &&
                !shape.IsHitTestVisible && shape.HorizontalAlignment == HorizontalAlignment.Left &&
                shape.VerticalAlignment == VerticalAlignment.Top &&
                (ReferenceEquals(shape, caret) || shape.CompositeMode == NativeCaretCompositeMode))
                current = shape;

            if (!ReferenceEquals(current, caret))
            {
                Restore();
                caret = current;
                originalFill = current?.Fill;
            }
            if (caret is null) return;
            var brush = GetBrush(control);
            if (!ReferenceEquals(caret.Fill, brush)) caret.Fill = brush;
            if (caret.CompositeMode != ElementCompositeMode.SourceOver) caret.CompositeMode = ElementCompositeMode.SourceOver;
        }

        private void Stop()
        {
            if (observingLayout) control.LayoutUpdated -= OnLayoutUpdated;
            observingLayout = false;
            Restore();
        }

        private void Restore()
        {
            if (caret is null) return;
            caret.Fill = originalFill;
            caret.CompositeMode = NativeCaretCompositeMode;
            caret = null;
            originalFill = null;
        }

        private static Shape? FindCaretShape(Control control)
        {
            // AOT 下原生模板元素可能只投影为 DependencyObject，必须显式查询 WinRT 类型。
            try
            {
                if (VisualTreeHelper.GetChildrenCount(control) == 0) return null;
                var root = VisualTreeHelper.GetChild(control, 0).As<FrameworkElement>();
                if (root.FindName("ContentElement") is not { } contentHost) return null;
                var content = contentHost.As<ScrollViewer>().Content;
                if (content is null) return null;
                var view = content.As<DependencyObject>();
                if (VisualTreeHelper.GetChildrenCount(view) != 1) return null;
                var child = VisualTreeHelper.GetChild(view, 0);
                try { return child.As<Rectangle>(); }
                catch (InvalidCastException) { return child.As<Microsoft.UI.Xaml.Shapes.Path>(); }
            }
            catch (InvalidCastException)
            {
                // SDK 或控件模板变动时只放弃定制，不影响编辑能力。
                return null;
            }
        }
    }
}
