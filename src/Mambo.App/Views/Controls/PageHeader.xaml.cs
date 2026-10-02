using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Views.Controls;

/// <summary>页头：眉标、标题、"N 项"计数胶囊，右侧放操作。</summary>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(nameof(Count), typeof(string), typeof(PageHeader), new PropertyMetadata("", OnCountChanged));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));
    public static readonly DependencyProperty HasCountProperty = DependencyProperty.Register(nameof(HasCount), typeof(bool), typeof(PageHeader), new PropertyMetadata(false));

    public PageHeader() => InitializeComponent();

    public string Eyebrow { get => (string)GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Count { get => (string)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public bool HasCount { get => (bool)GetValue(HasCountProperty); private set => SetValue(HasCountProperty, value); }

    private static void OnCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageHeader)d).HasCount = !string.IsNullOrEmpty(e.NewValue as string);
}
