using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace Mambo.App.Themes;

/// <summary>读取 Tokens.xaml 的动效 token，供 Composition 动画使用；系统关闭动画时一律瞬间完成。</summary>
public static class Motion
{
    private static readonly UISettings Settings = new();
    private static readonly Dictionary<string, TimeSpan> Durations = [];
    private static readonly Dictionary<string, (Vector2, Vector2)> Splines = [];

    public static bool AnimationsEnabled => Settings.AnimationsEnabled;

    public static TimeSpan Micro => Duration("MotionMicroDuration");
    public static TimeSpan Fast => Duration("MotionFastDuration");
    public static TimeSpan Normal => Duration("MotionNormalDuration");
    public static TimeSpan Route => Duration("MotionRouteDuration");
    public static TimeSpan Player => Duration("MotionPlayerDuration");
    public static TimeSpan Hero => Duration("MotionHeroDuration");

    public static (Vector2 P1, Vector2 P2) Standard => Spline("MotionStandardKeySpline");
    public static (Vector2 P1, Vector2 P2) Enter => Spline("MotionEnterKeySpline");
    public static (Vector2 P1, Vector2 P2) Fluid => Spline("MotionFluidKeySpline");
    public static (Vector2 P1, Vector2 P2) Settle => Spline("MotionSettleKeySpline");

    public static CubicBezierEasingFunction CreateEasing(Compositor compositor, (Vector2 P1, Vector2 P2) spline)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        return compositor.CreateCubicBezierEasingFunction(spline.P1, spline.P2);
    }

    private static TimeSpan Duration(string key)
    {
        if (!Durations.TryGetValue(key, out var value))
        {
            value = TimeSpan.Parse((string)Application.Current.Resources[key], CultureInfo.InvariantCulture);
            Durations[key] = value;
        }
        return value;
    }

    private static (Vector2, Vector2) Spline(string key)
    {
        if (!Splines.TryGetValue(key, out var value))
        {
            var p = ((string)Application.Current.Resources[key]).Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            value = (new Vector2(p[0], p[1]), new Vector2(p[2], p[3]));
            Splines[key] = value;
        }
        return value;
    }
}
