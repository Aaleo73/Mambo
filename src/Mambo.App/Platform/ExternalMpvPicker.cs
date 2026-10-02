using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace Mambo.App.Platform;

/// <summary>仅在用户点击选择按钮时调用；取消不会改动批准或播放方式。</summary>
public static class ExternalMpvPicker
{
    public static async Task<string?> PickAsync(WindowId windowId)
    {
        var picker = new FileOpenPicker(windowId);
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
