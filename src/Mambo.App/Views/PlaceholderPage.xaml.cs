using Mambo.App.Shell;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views;

/// <summary>P4 后续里程碑实现前的路由占位。</summary>
public sealed partial class PlaceholderPage : UserControl, INavigablePage
{
    public PlaceholderPage(string eyebrow, string title)
    {
        InitializeComponent();
        Header.Eyebrow = eyebrow;
        Header.Title = title;
    }

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
    }

    public void Refresh()
    {
    }
}
