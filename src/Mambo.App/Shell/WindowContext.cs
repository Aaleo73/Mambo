using Microsoft.UI;

namespace Mambo.App.Shell;

/// <summary>页面需要窗口身份时使用（文件选择器等）；由 MainWindow 在创建后填写。</summary>
public sealed class WindowContext
{
    public WindowId WindowId { get; internal set; }
    public nint Handle { get; internal set; }

    /// <summary>窗口处于前台；失焦或最小化时为 false（hero 轮播据此暂停）。</summary>
    public bool IsActive { get; private set; } = true;

    public event EventHandler? ActiveChanged;

    internal void SetActive(bool active)
    {
        if (active == IsActive) return;
        IsActive = active;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }
}
