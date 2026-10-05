using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace Mambo.App.BulletChat;

/// <summary>每条弹幕只量一次、画一次；之后的运动全部交给合成器。只在界面线程使用。</summary>
internal sealed partial class BulletChatTextRasterizer : IDisposable
{
    /// <summary>描边与抗锯齿在文字四周占用的留白（DIP）。</summary>
    internal const float Padding = 3;
    private const float Outline = 1.25f;
    private const string FontFile = "MiSans-Semibold.ttf";
    private const string FallbackFamily = "Segoe UI";
    private readonly CompositionGraphicsDevice graphics;
    private readonly CanvasStrokeStyle stroke = new() { LineJoin = CanvasLineJoin.Round };
    private readonly Dictionary<int, CanvasTextFormat> formats = [];
    private CanvasDevice device;
    private bool disposed;

    public BulletChatTextRasterizer(Compositor compositor)
    {
        device = CanvasDevice.GetSharedDevice();
        device.DeviceLost += OnDeviceLost;
        graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, device);
        graphics.RenderingDeviceReplaced += OnRenderingDeviceReplaced;
        FontFamily = ResolveFontFamily();
    }

    /// <summary>图形设备已更换，此前画过的表面内容全部失效，需要重画。</summary>
    public event Action? SurfacesInvalidated;
    internal string FontFamily { get; }
    internal bool UsesBundledFont => FontFamily != FallbackFamily;
    internal CanvasDevice Device => device;

    public BulletChatText Measure(string text, float fontSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var layout = new CanvasTextLayout(device, text, Format(fontSize, FontFamily), 0, 0);
        var bounds = layout.LayoutBounds;
        return new(layout, new((float)Math.Ceiling(bounds.Width) + Padding * 2, (float)Math.Ceiling(bounds.Height) + Padding * 2));
    }

    public CompositionDrawingSurface CreateSurface()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return graphics.CreateDrawingSurface(new Size(1, 1), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
    }

    /// <summary>把表面调整到文字的像素尺寸并重画；<paramref name="scale"/> 为 RasterizationScale。</summary>
    public void Draw(CompositionDrawingSurface surface, BulletChatText text, uint rgb, float scale)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CanvasComposition.Resize(surface, new Size(Math.Ceiling(text.Size.X * scale), Math.Ceiling(text.Size.Y * scale)));
        using var session = CanvasComposition.CreateDrawingSession(surface);
        session.Clear(Color.FromArgb(0, 0, 0, 0));
        session.Transform = Matrix3x2.CreateScale(scale);
        Paint(session, text, rgb);
    }

    internal void Paint(CanvasDrawingSession session, BulletChatText text, uint rgb)
    {
        var origin = new Vector2(Padding, Padding);
        var fill = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        // 深色字配浅色描边，否则在暗画面上看不见。
        var dark = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B < 60;
        var edge = dark ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(230, 0, 0, 0);
        using var geometry = CanvasGeometry.CreateText(text.Layout);
        // 描边以轮廓为中心，画两倍宽度，外侧可见部分正好是 Outline。
        session.DrawGeometry(geometry, origin, edge, Outline * 2, stroke);
        session.DrawTextLayout(text.Layout, origin, fill);
    }

    private CanvasTextFormat Format(float fontSize, string family)
    {
        var key = HashCode.Combine((int)Math.Round(fontSize * 4), family);
        if (formats.TryGetValue(key, out var format)) return format;
        format = new CanvasTextFormat
        {
            FontFamily = family, FontSize = fontSize, FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap, Options = CanvasDrawTextOptions.EnableColorFont,
        };
        formats.Add(key, format);
        return format;
    }

    // 免打包应用里 Win2D 的自定义字体写法并不都可用：逐个尝试，以排版宽度与"不存在的字体"是否不同来判断是否真的生效。
    private string ResolveFontFamily()
    {
        const string sample = "弹幕 Bullet 0123";
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", FontFile);
        if (!File.Exists(file)) return FallbackFamily;
        var missing = Width(sample, "Mambo-No-Such-Font");
        string[] candidates =
        [
            "Assets/Fonts/" + FontFile + "#MiSans",
            "ms-appx:///Assets/Fonts/" + FontFile + "#MiSans",
            new Uri(file).AbsoluteUri + "#MiSans",
            file + "#MiSans",
        ];
        foreach (var candidate in candidates)
        {
            try { if (Math.Abs(Width(sample, candidate) - missing) > .5) return candidate; }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        return FallbackFamily;
    }

    private double Width(string text, string family)
    {
        using var format = new CanvasTextFormat { FontFamily = family, FontSize = 32, FontWeight = FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(device, text, format, 0, 0);
        return layout.LayoutBounds.Width;
    }

    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        if (disposed) return;
        sender.DeviceLost -= OnDeviceLost;
        device = CanvasDevice.GetSharedDevice();
        device.DeviceLost += OnDeviceLost;
        // 换设备后合成器触发 RenderingDeviceReplaced，由它通知重画。
        CanvasComposition.SetCanvasDevice(graphics, device);
    }

    private void OnRenderingDeviceReplaced(CompositionGraphicsDevice sender, RenderingDeviceReplacedEventArgs args)
    {
        if (!disposed) SurfacesInvalidated?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SurfacesInvalidated = null;
        device.DeviceLost -= OnDeviceLost;
        graphics.RenderingDeviceReplaced -= OnRenderingDeviceReplaced;
        foreach (var format in formats.Values) format.Dispose();
        formats.Clear();
        stroke.Dispose();
        graphics.Dispose();
    }
}

/// <summary>一条已排版的弹幕文字；<see cref="Size"/> 为含留白的 DIP 尺寸。画完或放弃后释放。</summary>
internal sealed partial class BulletChatText(CanvasTextLayout layout, Vector2 size) : IDisposable
{
    public CanvasTextLayout Layout { get; } = layout;
    public Vector2 Size { get; } = size;
    public void Dispose() => Layout.Dispose();
}
