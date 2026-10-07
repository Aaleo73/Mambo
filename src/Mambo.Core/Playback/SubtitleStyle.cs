using Mambo.Core.Contracts;

namespace Mambo.Core.Playback;

public static class SubtitleStyle
{
    public static SubtitleStyleSettings Normalize(SubtitleStyleSettings? value)
    {
        var defaults = new SubtitleStyleSettings();
        if (value is null) return defaults;
        return value with
        {
            FontFamily = ValidFont(value.FontFamily) ? value.FontFamily : defaults.FontFamily,
            FontSize = ValidNumber(value.FontSize, 18, 72) ? value.FontSize : defaults.FontSize,
            TextColor = ValidColor(value.TextColor) ? value.TextColor : defaults.TextColor,
            OutlineSize = ValidNumber(value.OutlineSize, 0, 6) ? value.OutlineSize : defaults.OutlineSize,
            BottomMargin = ValidNumber(value.BottomMargin, 0, 180) ? Math.Round(value.BottomMargin) : defaults.BottomMargin,
        };
    }

    public static void Validate(SubtitleStyleSettings? value)
    {
        if (value is null || Normalize(value) != value)
            throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "字幕样式无效。", false));
    }

    /// <summary>mpv stable manual / Subtitles: scaled pixels at 720 height; border-* 为 outline-* 的兼容别名。
    /// force 才会将 sub-* 文本选项用于 ASS；no 保留 ASS 排版。不可使用会影响 ASS/位图的 sub-scale、sub-pos。</summary>
    public static IReadOnlyDictionary<string, MpvValue> Properties(SubtitleStyleSettings style)
    {
        Validate(style);
        return new Dictionary<string, MpvValue>(StringComparer.Ordinal)
        {
            ["sub-font"] = new MpvValue.Text(style.FontFamily),
            ["sub-font-size"] = new MpvValue.Number(style.FontSize),
            ["sub-color"] = new MpvValue.Text(style.TextColor),
            ["sub-border-color"] = new MpvValue.Text("#000000"),
            ["sub-border-size"] = new MpvValue.Number(style.OutlineSize),
            ["sub-margin-y"] = new MpvValue.WholeNumber((long)style.BottomMargin),
            ["sub-ass-override"] = new MpvValue.Text(style.OverrideAssStyle ? "force" : "no"),
        };
    }

    internal static bool ValidNumber(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    internal static bool ValidColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);
    internal static bool ValidFont(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 96 && !value.Any(char.IsControl) && value.IndexOfAny(['/', '\\', ':', '|']) < 0;
}
