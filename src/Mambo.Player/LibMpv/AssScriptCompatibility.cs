using System.Globalization;
using System.Text;

namespace Mambo.Player.LibMpv;

/// <summary>只补齐包含完整 ASS 对白的异常格式头；正常格式头和无法明确识别的内容原样交给 mpv。</summary>
internal static class AssScriptCompatibility
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private const string EventFormat = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";

    internal static bool TryRepair(string? source, out string repaired)
    {
        repaired = "";
        if (string.IsNullOrEmpty(source) || source.Length > MaximumBytes || source.Contains('\0') ||
            Encoding.UTF8.GetByteCount(source) > MaximumBytes) return false;

        bool scriptInfo = false, assStyles = false, events = false, eventFormat = false;
        var offset = 0;
        foreach (var line in source.AsSpan().EnumerateLines())
        {
            var text = line.Trim().TrimStart('\uFEFF');
            if (text.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase)) scriptInfo = true;
            if (text.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)) assStyles = true;
            if (text.Equals("[Events]", StringComparison.OrdinalIgnoreCase)) events = true;
            if (events && text.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) eventFormat = true;
            if (text.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                if (!scriptInfo || !assStyles || eventFormat || !ValidDialogue(text["Dialogue:".Length..].TrimStart())) return false;
                var addition = (events ? "" : "[Events]\n") + EventFormat + "\n";
                repaired = source.Insert(offset, addition);
                return true;
            }
            offset += line.Length;
            if (offset < source.Length && source[offset] == '\r') offset++;
            if (offset < source.Length && source[offset] == '\n') offset++;
        }
        return false;
    }

    private static bool ValidDialogue(ReadOnlySpan<char> dialogue)
    {
        Span<Range> fields = stackalloc Range[10];
        if (dialogue.Split(fields, ',') != 10 ||
            !int.TryParse(dialogue[fields[0]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            !Timestamp(dialogue[fields[1]].Trim(), out var start) ||
            !Timestamp(dialogue[fields[2]].Trim(), out var end) || end <= start) return false;
        return !dialogue[fields[3]].Trim().IsEmpty && !dialogue[fields[9]].Trim().IsEmpty;
    }

    private static bool Timestamp(ReadOnlySpan<char> text, out double seconds)
    {
        seconds = 0;
        Span<Range> parts = stackalloc Range[3];
        if (text.Split(parts, ':') != 3 ||
            !int.TryParse(text[parts[0]], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(text[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes is < 0 or > 59 ||
            !double.TryParse(text[parts[2]], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var remainder) ||
            !double.IsFinite(remainder) || remainder is < 0 or >= 60) return false;
        seconds = hours * 3600d + minutes * 60d + remainder;
        return true;
    }
}
