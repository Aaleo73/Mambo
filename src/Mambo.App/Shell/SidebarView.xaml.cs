using System.Text;
using Mambo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mambo.App.Shell;

public sealed partial class SidebarView : UserControl
{
    private readonly Navigator navigator;

    public SidebarView(ShellViewModel viewModel, Navigator navigator)
    {
        ViewModel = viewModel;
        this.navigator = navigator;
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }

    public void FocusSearch()
    {
        if (!SearchBox.IsEnabled) return;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    /// <summary>NFKC 规范化并去掉标点和符号后至少剩 1 个字符才搜索。</summary>
    public static bool IsSearchable(string text) =>
        (text ?? "").Normalize(NormalizationForm.FormKC).Any(c => char.IsLetterOrDigit(c));

    /// <summary>启动时把焦点放在导航上，避免搜索框一开始就处于输入状态。</summary>
    public bool FocusNavigation() => HomeItem.Focus(FocusState.Programmatic);

    // 选中即导航：鼠标、键盘和辅助技术的"选择"都走这里；与当前页相同的路由由 Navigator 忽略。
    private void OnHomeChecked(object sender, RoutedEventArgs e) => navigator.Navigate(Route.Home);
    private void OnRecentChecked(object sender, RoutedEventArgs e) => navigator.Navigate(Route.Recent);
    private void OnSettingsChecked(object sender, RoutedEventArgs e) => navigator.Navigate(Route.Settings);

    private void OnLibraryChecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) navigator.Navigate(Route.Library(id));
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Search();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => Search();

    private void Search()
    {
        var text = SearchBox.Text.Trim();
        if (IsSearchable(text)) navigator.Navigate(Route.Search(text));
    }
}
