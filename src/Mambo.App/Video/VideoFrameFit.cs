namespace Mambo.App.Video;

/// <summary>
/// 圆角黑框里视频矩形的摆法。交换链由系统垫在界面层下面合成，界面层的圆角裁不到它，
/// 所以矩形必须整块落在圆角以内。圆角只切黑边：黑边多厚，圆角最多就多大，影片本身不缩。不依赖 WinUI。
/// </summary>
internal static class VideoFrameFit
{
    /// <summary>视频矩形（相对黑框左上角）和黑框此刻能用的圆角，单位都是物理像素。</summary>
    internal readonly record struct Frame(int X, int Y, int Width, int Height, int Radius);

    /// <summary>同一高度的两种布局共用圆角；任一布局黑边不足时，两者一起减小。</summary>
    internal static int ComputeSharedRadius(int firstWidth, int secondWidth, int height, int maxRadius, double aspect) =>
        Math.Min(Compute(firstWidth, height, maxRadius, aspect).Radius,
            Compute(secondWidth, height, maxRadius, aspect).Radius);

    /// <param name="aspect">影片显示出来的宽高比；无效时按铺满处理，圆角为 0。</param>
    internal static Frame Compute(int width, int height, int maxRadius, double aspect)
    {
        if (width <= 0 || height <= 0) return default;
        if (maxRadius <= 0 || !double.IsFinite(aspect) || aspect <= 0) return new(0, 0, width, height, 0);
        // 影片比黑框宽，黑边在上下；否则在左右。
        var vertical = aspect * height >= width;
        var bar = vertical ? (height - width / aspect) / 2 : (width - height * aspect) / 2;
        // 留半个像素：mpv 自己取整之后影片仍然放得下，不会反过来被压小。
        var radius = (int)Math.Clamp(Math.Floor(bar - 0.5), 0, maxRadius);
        return vertical
            ? new(0, radius, width, height - 2 * radius, radius)
            : new(radius, 0, width - 2 * radius, height, radius);
    }
}
