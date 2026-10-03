using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WinRT;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Mambo.App.Themes;

/// <summary>
/// 线条图标：把 Icons.xaml 里的路径数据画成描边（或实心）图形，颜色跟随 Foreground，
/// 所以放进按钮后会随悬停、按下、禁用一起变色。
/// </summary>
public sealed partial class LineIcon : Control
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(LineIcon),
        new PropertyMetadata(null, (d, _) => ((LineIcon)d).Apply()));
    public static readonly DependencyProperty BoxProperty = DependencyProperty.Register(nameof(Box), typeof(double), typeof(LineIcon),
        new PropertyMetadata(24d, (d, _) => ((LineIcon)d).Apply()));
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(LineIcon),
        new PropertyMetadata(1.8d, (d, _) => ((LineIcon)d).Apply()));
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(LineIcon),
        new PropertyMetadata(false, (d, _) => ((LineIcon)d).Apply()));

    private Grid? canvas;
    private Path? stroke;
    private Path? fill;

    // 模板来自 Controls.xaml 里的隐式样式，不设 DefaultStyleKey。
    public LineIcon() => IsTabStop = false;

    /// <summary>路径数据（XAML 路径小语言）。</summary>
    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    /// <summary>路径数据所在的正方形视框边长，默认 24。</summary>
    public double Box { get => (double)GetValue(BoxProperty); set => SetValue(BoxProperty, value); }
    /// <summary>视框坐标下的描边宽度，默认 1.8。</summary>
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    /// <summary>为 true 时填充而不描边。</summary>
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        canvas = GetTemplateChild("Canvas")?.As<Grid>();
        stroke = GetTemplateChild("Stroke")?.As<Path>();
        fill = GetTemplateChild("Fill")?.As<Path>();
        Apply();
    }

    private void Apply()
    {
        if (canvas is null || stroke is null || fill is null) return;
        canvas.Width = canvas.Height = Box;
        stroke.StrokeThickness = StrokeWidth;
        var active = Filled ? fill : stroke;
        var idle = Filled ? stroke : fill;
        idle.Data = null;
        idle.Visibility = Visibility.Collapsed;
        active.Visibility = Visibility.Visible;
        // Geometry 不能在多个 Path 间共享，所以每个实例各自从字符串转换。
        active.Data = string.IsNullOrEmpty(Glyph) ? null : XamlBindingHelper.ConvertValue(typeof(Geometry), Glyph).As<Geometry>();
    }
}
