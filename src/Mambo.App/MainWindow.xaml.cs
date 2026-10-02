using Mambo.App.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Mambo.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WinUI 窗口的 Closed 事件释放 resizeHook。")]
public sealed partial class MainWindow : Window
{
    private readonly WindowResizeHook resizeHook;
    private bool closing;
    public MainWindow()
    {
        InitializeComponent();
        var presenter = OverlappedPresenter.Create();
        presenter.PreferredMinimumWidth = 1100;
        presenter.PreferredMinimumHeight = 720;
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new SizeInt32(1500, 860));
        resizeHook = new WindowResizeHook(WinRT.Interop.WindowNative.GetWindowHandle(this), Lab.SetLiveResize);
        Lab.Initialize(this, presenter);
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => resizeHook.Dispose();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true;
        closing = true;
        await Lab.CloseAsync();
        Close();
    }
}
