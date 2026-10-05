namespace Mambo.App.ViewModels;

/// <summary>播放页选择面板里的一行。</summary>
public sealed class PlayerChoice(string label, bool isSelected, Action choose)
{
    public string Label { get; } = label;
    public bool IsSelected { get; } = isSelected;
    internal Action Choose { get; } = choose;
}

/// <summary>选择面板里带标题的一组；没有可选项时显示 <see cref="EmptyText"/>。</summary>
public sealed class PlayerChoiceGroup(string title, List<PlayerChoice> choices, string emptyText = "")
{
    public string Title { get; } = title;
    // 交给 XAML 列表控件的集合必须是具体类型：编译器为只读接口合成的集合类型没有 WinRT 投影。
    public List<PlayerChoice> Choices { get; } = choices;
    public string EmptyText { get; } = emptyText;
    public bool HasChoices => Choices.Count > 0;
    public bool IsEmpty => Choices.Count == 0 && EmptyText.Length > 0;
}
