using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Mambo.App.Themes;

/// <summary>读取 Tokens.xaml 的动效 token，供 Composition 动画使用；系统关闭动画时一律瞬间完成。</summary>
public static class Motion
{
    private static readonly UISettings Settings = new();
    private static readonly Dictionary<string, TimeSpan> Durations = [];
    private static readonly Dictionary<string, (Vector2, Vector2)> Splines = [];

    public static bool AnimationsEnabled => Settings.AnimationsEnabled;

    public static TimeSpan Press => Duration("MotionPressDuration");
    public static TimeSpan Exit => Duration("MotionExitDuration");
    public static TimeSpan Feedback => Duration("MotionFeedbackDuration");
    public static TimeSpan Content => Duration("MotionContentDuration");
    public static TimeSpan Image => Duration("MotionImageDuration");
    public static TimeSpan Mode => Duration("MotionModeDuration");

    public static (Vector2 P1, Vector2 P2) EaseOut => Spline("MotionEaseOutKeySpline");
    public static (Vector2 P1, Vector2 P2) EaseIn => Spline("MotionEaseInKeySpline");
    public static (Vector2 P1, Vector2 P2) Symmetric => Spline("MotionSymmetricKeySpline");

    private static readonly DependencyProperty EntranceSuppressedProperty = DependencyProperty.RegisterAttached(
        "EntranceSuppressed", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    private static readonly DependencyProperty InactiveProperty = DependencyProperty.RegisterAttached(
        "Inactive", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    internal static bool IsEntranceSuppressed(DependencyObject element) => HasAncestorFlag(element, EntranceSuppressedProperty);
    internal static bool IsActive(DependencyObject element) => !HasAncestorFlag(element, InactiveProperty);

    internal static void SetEntranceSuppressed(DependencyObject root, bool suppressed)
    {
        root.SetValue(EntranceSuppressedProperty, suppressed);
        if (suppressed)
        {
            if (root is IMotionParticipant participant) participant.SettleMotion();
            SettleDescendants(root);
        }
    }

    internal static void SetActive(DependencyObject root, bool active)
    {
        root.SetValue(InactiveProperty, !active);
        if (!active)
        {
            if (root is IMotionParticipant participant) participant.SettleMotion();
            SettleDescendants(root);
        }
    }

    // Only traverse realized children at ownership/lifecycle boundaries, never on animation frames.
    internal static void SettleDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is IMotionParticipant participant) participant.SettleMotion();
            SettleDescendants(child);
        }
    }

    private static bool HasAncestorFlag(DependencyObject element, DependencyProperty property)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if ((bool)current.GetValue(property)) return true;
        return false;
    }

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

internal interface IMotionParticipant
{
    void SettleMotion();
}
