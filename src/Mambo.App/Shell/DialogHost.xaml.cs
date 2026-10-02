using System.Numerics;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mambo.App.Shell;

/// <summary>窗口内的模态确认框：半透明遮罩 + 弹出面；焦点限制在框内，关闭后恢复到原处。</summary>
public sealed partial class DialogHost : UserControl, IDialogPresenter
{
    private TaskCompletionSource<bool>? pending;
    private Control? previousFocus;

    public DialogHost()
    {
        InitializeComponent();
    }

    public Task<bool> PresentAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        pending = new TaskCompletionSource<bool>();
        TitleText.Text = request.Title;
        BodyText.Text = request.Text;
        BodyText.Visibility = string.IsNullOrEmpty(request.Text) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = request.CancelText;
        CancelButton.Visibility = request.CancelText is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = request.ConfirmText;
        DangerButton.Content = request.ConfirmText;
        ConfirmButton.Visibility = request.Danger ? Visibility.Collapsed : Visibility.Visible;
        DangerButton.Visibility = request.Danger ? Visibility.Visible : Visibility.Collapsed;
        previousFocus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        Visibility = Visibility.Visible;
        PlayOpen();
        (request.Danger && request.CancelText is not null ? CancelButton : request.Danger ? DangerButton : ConfirmButton).Focus(FocusState.Programmatic);
        return pending.Task;
    }

    private void Close(bool result)
    {
        if (pending is null) return;
        var completion = pending;
        pending = null;
        Visibility = Visibility.Collapsed;
        previousFocus?.Focus(FocusState.Programmatic);
        previousFocus = null;
        completion.TrySetResult(result);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Close(true);
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close(false);
    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => Close(false);
    private void OnPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Close(false);
    }

    private void PlayOpen()
    {
        if (!Motion.AnimationsEnabled) return;
        var scrim = ElementCompositionPreview.GetElementVisual(Scrim);
        var panel = ElementCompositionPreview.GetElementVisual(Panel);
        var compositor = panel.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, Motion.CreateEasing(compositor, Motion.Enter));
        fade.Duration = Motion.Fast;
        scrim.StartAnimation("Opacity", fade);
        Panel.UpdateLayout();
        panel.CenterPoint = new Vector3((float)Panel.ActualWidth / 2, (float)Panel.ActualHeight / 2, 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(1.04f, 1.04f, 1));
        scale.InsertKeyFrame(1, Vector3.One, Motion.CreateEasing(compositor, Motion.Settle));
        scale.Duration = Motion.Normal;
        panel.StartAnimation("Scale", scale);
    }
}
