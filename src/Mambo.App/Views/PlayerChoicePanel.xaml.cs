using System.ComponentModel;
using System.Globalization;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mambo.App.Views;

/// <summary>播放页的选择面板（倍速、字幕与音轨）：带标题的几组选项，当前项打勾，点一行即生效。</summary>
public sealed partial class PlayerChoicePanel : UserControl
{
    private List<PlayerChoiceGroup> groups = [];
    private bool applyingColor;

    public PlayerChoicePanel()
    {
        InitializeComponent();
        SubtitleControls.PropertyChanged += OnSubtitlePropertyChanged;
        UpdateColor();
    }

    public SubtitleControlsViewModel SubtitleControls { get; } = new();

    public void EnableSubtitleControls(IPlaybackSession session)
    {
        Width = 360;
        SubtitleControls.Attach(session);
    }

    public void DisposeSubtitleControls()
    {
        SubtitleControls.PropertyChanged -= OnSubtitlePropertyChanged;
        SubtitleControls.Dispose();
    }

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
        if (groups.Count == value.Count && groups.Zip(value).All(pair => pair.First.Title == pair.Second.Title &&
            pair.First.Choices.Select(choice => choice.Label).SequenceEqual(pair.Second.Choices.Select(choice => choice.Label))))
        {
            foreach (var pair in groups.SelectMany(group => group.Choices).Zip(value.SelectMany(group => group.Choices)))
            { pair.First.IsSelected = pair.Second.IsSelected; pair.First.Choose = pair.Second.Choose; }
            return;
        }
        groups = value;
        GroupList.ItemsSource = SubtitleControls.IsVisible ? value.Take(1).ToList() : value;
        TrailingGroupList.ItemsSource = SubtitleControls.IsVisible ? value.Skip(1).ToList() : new List<PlayerChoiceGroup>();
    }

    /// <summary>收起后放掉选项：它们的回调捕获着播放层，不能留在缓存的弹出层里。</summary>
    public void Clear()
    {
        SubtitleControls.FlushPending();
        SetGroups([]);
    }

    private void OnDelayEarlier(object sender, RoutedEventArgs args) => SubtitleControls.AdjustDelay(-.1);
    private void OnDelayLater(object sender, RoutedEventArgs args) => SubtitleControls.AdjustDelay(.1);
    private void OnDelayReset(object sender, RoutedEventArgs args) => SubtitleControls.ResetDelay();
    private void OnStyleReset(object sender, RoutedEventArgs args) => SubtitleControls.ResetStyle();
    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        SubtitleControls.FlushPending();
    }

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (applyingColor || !SubtitleControls.CanEditStyle) return;
        SubtitleControls.TextColor = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
    }

    private void OnSubtitlePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SubtitleControlsViewModel.TextColor)) UpdateColor();
    }

    private void UpdateColor()
    {
        var text = SubtitleControls.TextColor;
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return;
        applyingColor = true;
        SubtitleColorPicker.Color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        applyingColor = false;
    }

    private void OnChoiceClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is PlayerChoice choice) choice.Choose();
    }
}
