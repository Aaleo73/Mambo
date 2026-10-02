using Mambo.Core.Contracts;

namespace Mambo.App.Shell;

/// <summary>卡片的共用动作；卡片在 XAML 模板里创建，无法注入服务，经 Current 取得。</summary>
public sealed class CardActions
{
    private readonly Navigator navigator;
    private readonly ILibraryService library;
    private readonly PlaybackLauncher launcher;

    public CardActions(Navigator navigator, ILibraryService library, PlaybackLauncher launcher)
    {
        this.navigator = navigator;
        this.library = library;
        this.launcher = launcher;
        Current = this;
    }

    public static CardActions? Current { get; private set; }

    public void Open(string itemId) => navigator.Navigate(Route.Detail(itemId));

    /// <summary>卡片悬停 300ms 后预热详情（后台优先级，失败不通知界面）。</summary>
    public void Prefetch(string itemId) => library.PrefetchDetail(itemId);

    public Task PlayAsync(string itemId, long? startTicks = null) => launcher.PlayAsync(itemId, startTicks);
}
