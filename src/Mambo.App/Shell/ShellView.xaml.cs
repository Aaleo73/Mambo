using System.Numerics;
using Mambo.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mambo.App.Shell;

/// <summary>外壳：40px 标题栏、208px 侧栏、内容区（PageHost）以及通知、对话框覆盖层。</summary>
public sealed partial class ShellView : UserControl
{
    private readonly Navigator navigator;
    private CompositionRoundedRectangleGeometry? wellClip;

    public ShellView(ShellViewModel viewModel, Navigator navigator, ToastService toasts, DialogService dialogs, Func<Route, FrameworkElement> pageFactory)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ViewModel = viewModel;
        this.navigator = navigator;
        InitializeComponent();
        Sidebar = new SidebarView(viewModel, navigator);
        SidebarSlot.Child = Sidebar;
        OverlayLayer.Children.Add(new ToastHost(toasts));
        dialogs.Attach(Dialogs);
        Pages.Initialize(pageFactory);
        navigator.Navigated += OnNavigated;
        viewModel.AccountChanged += OnAccountChanged;
        Well.SizeChanged += (_, _) => UpdateWellClip();
        TitleBar.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        NavButtons.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        CenterContent.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        Pages.Show(new NavigatedEventArgs(null, navigator.Current, NavigationMode.New));
        Loaded += (_, _) => Sidebar.FocusNavigation();
    }

    public ShellViewModel ViewModel { get; }
    public SidebarView Sidebar { get; }
    public PageHost PageHost => Pages;
    public FrameworkElement TitleBarElement => TitleBar;
    public FrameworkElement MaximizeElement => MaximizeButton;
    public IEnumerable<FrameworkElement> PassthroughElements => [NavButtons, MinimizeButton, CloseButton, CenterContent];

    public event EventHandler? TitleBarLayoutChanged;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? CloseRequested;

    /// <summary>标题栏中间的可交互内容，例如首页 hero 分页点。</summary>
    public void SetCenterContent(UIElement? content)
    {
        CenterContent.Content = content;
        TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMaximizeVisual(bool hover, bool pressed, bool maximized)
    {
        MaximizeHover.Opacity = hover && !pressed ? 1 : 0;
        MaximizePressed.Opacity = pressed ? 1 : 0;
        MaximizeGlyph.Glyph = maximized ? "" : "";
        ToolTipService.SetToolTip(MaximizeButton, maximized ? "还原" : "最大化");
    }

    private void OnNavigated(object? sender, NavigatedEventArgs e) => Pages.Show(e);

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        var keep = navigator.Current.Route.Kind == PageKind.Settings ? Route.Settings : Route.Home;
        Pages.Clear();
        navigator.Reset(keep);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => navigator.GoBack();
    private void OnForwardClick(object sender, RoutedEventArgs e) => navigator.GoForward();
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed) { navigator.GoBack(); e.Handled = true; }
        else if (properties.IsXButton2Pressed) { navigator.GoForward(); e.Handled = true; }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (alt && e.Key == VirtualKey.Left) { navigator.GoBack(); e.Handled = true; }
        else if (alt && e.Key == VirtualKey.Right) { navigator.GoForward(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape && !e.Handled && !IsTextInputFocused()) { e.Handled = navigator.GoBack(); }
    }

    private bool IsTextInputFocused() =>
        XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox;

    /// <summary>内容区只圆左上、右上两个角：裁剪几何向下多延伸一个圆角，把下方两个角藏到可见区域外。</summary>
    private void UpdateWellClip()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Pages);
        var compositor = visual.Compositor;
        if (wellClip is null)
        {
            wellClip = compositor.CreateRoundedRectangleGeometry();
            wellClip.CornerRadius = new Vector2(11, 11);
            visual.Clip = compositor.CreateGeometricClip(wellClip);
        }
        wellClip.Size = new Vector2((float)Pages.ActualWidth, (float)Pages.ActualHeight + 12);
    }
}
