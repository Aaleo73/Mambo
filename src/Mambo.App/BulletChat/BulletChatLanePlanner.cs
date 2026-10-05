namespace Mambo.App.BulletChat;

/// <summary>
/// 给弹幕分配轨道，保证同一轨道里的弹幕不重叠。结果只取决于按时间顺序喂入的弹幕，
/// 所以跳转后从头重放同一段弹幕会得到相同的排布。不依赖 WinUI。
/// </summary>
internal sealed class BulletChatLanePlanner
{
    /// <summary>同一轨道里前后两条滚动弹幕之间至少留出的水平间隔（DIP）。</summary>
    public const double Gap = 16;
    private readonly record struct Scrolling(double Time, double Width, double Viewport, double Duration);
    private Scrolling[] scroll = [];
    private double[] top = [];
    private double[] bottom = [];

    public int ScrollLanes => scroll.Length;
    public int TopLanes => top.Length;
    public int BottomLanes => bottom.Length;

    /// <summary>设定各类轨道数；已有的占用记录在数量允许的范围内保留。</summary>
    public void Configure(int scrollLanes, int topLanes, int bottomLanes)
    {
        Array.Resize(ref scroll, Math.Max(0, scrollLanes));
        Resize(ref top, topLanes);
        Resize(ref bottom, bottomLanes);
    }

    public void Reset()
    {
        Array.Clear(scroll);
        Array.Fill(top, double.NegativeInfinity);
        Array.Fill(bottom, double.NegativeInfinity);
    }

    /// <summary>
    /// 滚动弹幕在 <paramref name="duration"/> 秒内从右侧外缘移到左侧外缘，越长的越快。
    /// 返回最靠上的可用轨道；都不可用时返回 -1，这条弹幕应被丢弃。
    /// </summary>
    public int PlaceScroll(double time, double width, double viewport, double duration)
    {
        if (viewport <= 0 || duration <= 0) return -1;
        for (var lane = 0; lane < scroll.Length; lane++)
        {
            var previous = scroll[lane];
            if (previous.Duration > 0)
            {
                var elapsed = time - previous.Time;
                // 重放或乱序时不往已占用的轨道里插。
                if (elapsed < 0) continue;
                // 前一条的尾部必须已经完全进入画面，并留出间隔。
                var travelled = (previous.Viewport + previous.Width) / previous.Duration * elapsed;
                if (travelled < previous.Width + Gap) continue;
                // 新的更快时会追上去：它的头到达左缘的时刻不能早于前一条完全离开的时刻。
                var reachesLeftEdge = time + viewport / ((viewport + width) / duration);
                if (reachesLeftEdge < previous.Time + previous.Duration) continue;
            }
            scroll[lane] = new(time, width, viewport, duration);
            return lane;
        }
        return -1;
    }

    /// <summary>顶部固定弹幕：停留 <paramref name="duration"/> 秒，轨道从上往下数。</summary>
    public int PlaceTop(double time, double duration) => PlaceFixed(top, time, duration);

    /// <summary>底部固定弹幕：轨道从下往上数。</summary>
    public int PlaceBottom(double time, double duration) => PlaceFixed(bottom, time, duration);

    private static int PlaceFixed(double[] lanes, double time, double duration)
    {
        if (duration <= 0) return -1;
        for (var lane = 0; lane < lanes.Length; lane++)
        {
            if (time < lanes[lane]) continue;
            lanes[lane] = time + duration;
            return lane;
        }
        return -1;
    }

    private static void Resize(ref double[] lanes, int count)
    {
        var previous = lanes.Length;
        Array.Resize(ref lanes, Math.Max(0, count));
        for (var lane = previous; lane < lanes.Length; lane++) lanes[lane] = double.NegativeInfinity;
    }
}
