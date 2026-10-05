using Mambo.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

/// <summary>播放页的选择面板（倍速、字幕与音轨）：带标题的几组选项，当前项打勾，点一行即生效。</summary>
public sealed partial class PlayerChoicePanel : UserControl
{
    private List<PlayerChoiceGroup> groups = [];

    public PlayerChoicePanel() => InitializeComponent();

    internal int ChoiceCount => groups.Sum(group => group.Choices.Count);
    internal string? SelectedLabels => string.Join('|', groups.SelectMany(group => group.Choices).Where(choice => choice.IsSelected).Select(choice => choice.Label));

    /// <summary>诊断入口：与点击该行走同一个回调。</summary>
    internal bool ChooseForSmoke(string label)
    {
        if (groups.SelectMany(group => group.Choices).FirstOrDefault(choice => choice.Label == label) is not { } choice) return false;
        choice.Choose();
        return true;
    }

    /// <summary>每次打开前按当前播放状态重新给出选项。</summary>
    public void SetGroups(List<PlayerChoiceGroup> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        groups = value;
        GroupList.ItemsSource = value;
    }

    /// <summary>收起后放掉选项：它们的回调捕获着播放层，不能留在缓存的弹出层里。</summary>
    public void Clear() => SetGroups([]);

    private void OnChoiceClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is PlayerChoice choice) choice.Choose();
    }
}
