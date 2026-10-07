using System.Text.Json;
using Mambo.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    private static async Task RunInputCaretOnlyAsync(MainWindow window, string reportPath, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        var report = new MotionReport();
        var well = window.Shell.FindName("WellContent").As<Grid>();
        var mount = new StackPanel { Spacing = 16, Padding = new Thickness(32), RequestedTheme = ElementTheme.Light };
        var text = new TextBox { Text = "输入光标测试", Style = XamlResources.Style(Application.Current.Resources, "SoftInputStyle") };
        var samplePassword = Guid.NewGuid().ToString("N");
        var password = new PasswordBox { Password = samplePassword, Style = XamlResources.Style(Application.Current.Resources, "SoftPasswordStyle") };
        var plainText = new TextBox { Text = "默认样式" };
        var plainPassword = new PasswordBox { Password = samplePassword };
        var parking = new Button { Content = "移走焦点" };
        mount.Children.Add(text);
        mount.Children.Add(password);
        mount.Children.Add(plainText);
        mount.Children.Add(plainPassword);
        mount.Children.Add(parking);
        Canvas.SetZIndex(mount, 1000);
        try
        {
            await WaitAsync(() => window.Shell.IsLoaded && window.Shell.ActualWidth > 0, token);
            // 只检查实际控件树与编程焦点，不发送键鼠，也不把结果当作人工观感验收。
            well.Children.Add(mount);
            await WaitAsync(() => mount.IsLoaded && text.IsLoaded, token);
            foreach (var control in new Control[] { text, password, plainText, plainPassword })
            {
                var name = ReferenceEquals(control, text) ? "SoftTextBox" : ReferenceEquals(control, password) ? "SoftPasswordBox" :
                    ReferenceEquals(control, plainText) ? "DefaultTextBox" : "DefaultPasswordBox";
                report.Stage = name;
                control.Focus(FocusState.Programmatic);
                await WaitAsync(() => FindInputCaret(control)?.CompositeMode == ElementCompositeMode.SourceOver, token);
                var caret = FindInputCaret(control)!;
                MotionCheck(report, name + "LightCaretIsGray", caret.Fill is SolidColorBrush { Color: { R: 0x60, G: 0x65, B: 0x6F, A: 255 } });
                var width = caret.Width;
                var selectionBrush = control is TextBox box ? box.SelectionHighlightColor : ((PasswordBox)control).SelectionHighlightColor;
                var foreground = control.Foreground;

                mount.RequestedTheme = ElementTheme.Dark;
                await WaitAsync(() => caret.Fill is SolidColorBrush { Color: { R: 0xA4, G: 0xA9, B: 0xB3, A: 255 } }, token);
                MotionCheck(report, name + "ThemeSwitchKeepsNativeGeometry", ReferenceEquals(caret, FindInputCaret(control)) && caret.Width == width);
                mount.RequestedTheme = ElementTheme.Light;
                await WaitAsync(() => caret.Fill is SolidColorBrush { Color: { R: 0x60, G: 0x65, B: 0x6F, A: 255 } }, token);
                MotionCheck(report, name + "TextAndSelectionBrushesUnchanged", ReferenceEquals(foreground, control.Foreground) &&
                    ReferenceEquals(selectionBrush, control is TextBox current ? current.SelectionHighlightColor : ((PasswordBox)control).SelectionHighlightColor));

                parking.Focus(FocusState.Programmatic);
                await WaitAsync(() => control.FocusState == FocusState.Unfocused && (int)caret.CompositeMode == 3, token);
                MotionCheck(report, name + "BlurRestoresNativeCaret", (int)caret.CompositeMode == 3 &&
                    caret.Fill is SolidColorBrush { Color: { R: 255, G: 255, B: 255, A: 255 } });
            }

            report.Stage = "SelectionAndDirection";
            text.Focus(FocusState.Programmatic);
            text.Select(1, 2);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "TextSelectionPreserved", text.SelectionStart == 1 && text.SelectionLength == 2 && text.Text == "输入光标测试");
            text.Text = "שלום";
            text.Select(text.Text.Length, 0);
            text.FlowDirection = FlowDirection.RightToLeft;
            await AwaitNextRenderingAsync(token);
            await WaitAsync(() => FindInputCaret(text)?.CompositeMode == ElementCompositeMode.SourceOver, token);
            // RTL 指示小钩由系统键盘布局决定，不把设置 FlowDirection 当作它已被创建的证据。
            MotionCheck(report, "RtlTextKeepsStyledCaret", FindInputCaret(text)?.Fill == InputCaret.GetBrush(text));
            text.FlowDirection = FlowDirection.LeftToRight;
            text.Text = "输入光标测试";
            await AwaitNextRenderingAsync(token);
            await WaitAsync(() => FindInputCaret(text)?.CompositeMode == ElementCompositeMode.SourceOver, token);
            MotionCheck(report, "LtrTextKeepsStyledCaret", FindInputCaret(text)?.Fill == InputCaret.GetBrush(text));

            report.Stage = "PasswordReveal";
            password.Focus(FocusState.Programmatic);
            password.PasswordRevealMode = PasswordRevealMode.Visible;
            await WaitAsync(() => FindInputCaret(password)?.CompositeMode == ElementCompositeMode.SourceOver, token);
            MotionCheck(report, "PasswordRevealKeepsStyledCaretAndValue", password.Password == samplePassword &&
                FindInputCaret(password)?.Fill == InputCaret.GetBrush(password));

            report.Stage = "DetachAndReload";
            text.Focus(FocusState.Programmatic);
            await WaitAsync(() => FindInputCaret(text)?.CompositeMode == ElementCompositeMode.SourceOver, token);
            var savedCaret = FindInputCaret(text)!;
            var brush = InputCaret.GetBrush(text);
            InputCaret.SetBrush(text, null);
            MotionCheck(report, "DisablingRestoresNativeCaret", (int)savedCaret.CompositeMode == 3);
            report.Stage = "Reenable";
            InputCaret.SetBrush(text, brush);
            await WaitAsync(() => FindInputCaret(text)?.CompositeMode == ElementCompositeMode.SourceOver, token);
            savedCaret = FindInputCaret(text)!;
            report.Stage = "Unload";
            well.Children.Remove(mount);
            await WaitAsync(() => !text.IsLoaded && (int)savedCaret.CompositeMode == 3, token);
            MotionCheck(report, "UnloadingRestoresNativeCaret", (int)savedCaret.CompositeMode == 3);
            well.Children.Add(mount);
            await WaitAsync(() => text.IsLoaded, token);
            text.Focus(FocusState.Programmatic);
            await WaitAsync(() => FindInputCaret(text)?.CompositeMode == ElementCompositeMode.SourceOver, token);
            MotionCheck(report, "ReloadReattachesCaret", FindInputCaret(text)?.Fill == brush);
            report.Passed = true;
            report.Stage = "Completed";
        }
        catch (Exception error) { report.FailureKind = error is UiInputProbe.InputFailure ? error.Message : error.GetType().Name; }
        finally
        {
            well.Children.Remove(mount);
            var path = System.IO.Path.GetFullPath(reportPath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, UiLabJsonContext.Default.MotionReport), CancellationToken.None);
            await window.CloseForSmokeAsync();
            window.Close();
        }
    }

    private static Shape? FindInputCaret(DependencyObject root)
    {
        if (VisualTreeHelper.GetChildrenCount(root) == 0) return null;
        var template = VisualTreeHelper.GetChild(root, 0).As<FrameworkElement>();
        if (template.FindName("ContentElement") is not { } part) return null;
        var content = part.As<ScrollViewer>().Content;
        if (content is null) return null;
        var view = content.As<DependencyObject>();
        if (VisualTreeHelper.GetChildrenCount(view) != 1) return null;
        var child = VisualTreeHelper.GetChild(view, 0);
        try { return child.As<Rectangle>(); }
        catch (InvalidCastException) { return child.As<Microsoft.UI.Xaml.Shapes.Path>(); }
    }
}
