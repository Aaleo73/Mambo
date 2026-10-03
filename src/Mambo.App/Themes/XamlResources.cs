using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Mambo.App.Themes;

/// <summary>原生 XAML 资源可能以 DependencyObject 投影返回，显式查询所需的 WinRT 类型。</summary>
internal static class XamlResources
{
    public static Style Style(ResourceDictionary resources, string key) => resources[key].As<Style>();
    public static DataTemplate Template(ResourceDictionary resources, string key) => resources[key].As<DataTemplate>();
    public static Border Border(DataTemplate template) => template.LoadContent().As<Border>();
}
