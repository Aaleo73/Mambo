using Microsoft.UI;

namespace Mambo.App.Shell;

/// <summary>页面需要窗口身份时使用（文件选择器等）；由 MainWindow 在创建后填写。</summary>
public sealed class WindowContext
{
    public WindowId WindowId { get; internal set; }
    public nint Handle { get; internal set; }
}
