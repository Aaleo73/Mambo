using Mambo.Core.Contracts;

namespace Mambo.App.Debug;

// WinUI XAML metadata 会为 record 的 init 属性生成普通 setter。
// 调试列表用只有 getter 的投影，保持跨前后端的 record 契约不可变。
public sealed partial class DemoLibraryRow
{
    internal DemoLibraryRow(MediaLibrary item) { Id = item.Id; Name = item.Name; }
    public string Id { get; }
    public string Name { get; }
}

public sealed partial class DemoMediaRow
{
    private readonly MediaItem item;
    internal DemoMediaRow(MediaItem item) => this.item = item;
    internal MediaItem Item => item;
    public string Name => item.Name;
    public string Overview => item.Overview ?? "";
}
