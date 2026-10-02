using Microsoft.UI.Xaml;

namespace Mambo.App.Shell;

/// <summary>页面向标题栏中间放内容（首页 hero 分页点）；外壳创建后接管显示。</summary>
public sealed class TitleBarService
{
    private Action<UIElement?>? presenter;
    private UIElement? owner;

    public void Attach(Action<UIElement?> show) => presenter = show;

    public void Show(UIElement content)
    {
        owner = content;
        presenter?.Invoke(content);
    }

    /// <summary>只撤下自己放上去的内容，避免页面切换时互相覆盖。</summary>
    public void Hide(UIElement content)
    {
        if (!ReferenceEquals(owner, content)) return;
        owner = null;
        presenter?.Invoke(null);
    }
}
