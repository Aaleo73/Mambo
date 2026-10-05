using System.Collections.Immutable;
using System.Globalization;
using Mambo.Core.Contracts;

namespace Mambo.Core.BulletChat;

public static class BulletChatParser
{
    public const int MaximumComments = 30000;
    private const int MaximumLength = 120;

    /// <summary>丢弃无法解析、空白或不支持的弹幕；结果按时间升序且不超过 <see cref="MaximumComments"/> 条。</summary>
    public static ImmutableArray<BulletChatComment> Parse(IReadOnlyList<DandanComment>? comments)
    {
        if (comments is null || comments.Count == 0) return [];
        // 弹幕库里混有被当成文本的二进制数据。带控制字符的一定是垃圾；同一发送者的其余弹幕
        // 往往也是（只是恰好没有控制字符），一并丢弃。
        HashSet<string>? junkSenders = null;
        foreach (var comment in comments)
            if (comment?.M is { } message && IsBinary(message) && Sender(comment.P) is { Length: > 0 } sender) (junkSenders ??= new(StringComparer.Ordinal)).Add(sender);
        var parsed = new List<BulletChatComment>(Math.Min(comments.Count, MaximumComments));
        foreach (var comment in comments)
        {
            if (comment?.M is not { } message || IsBinary(message)) continue;
            if (junkSenders is not null && Sender(comment.P) is { Length: > 0 } sender && junkSenders.Contains(sender)) continue;
            if (TryParse(comment.P, message, out var value)) parsed.Add(value);
        }
        // 稳定排序：同一时刻保留服务器给出的先后。
        var ordered = parsed.Select((value, index) => (value, index)).OrderBy(item => item.value.TimeSeconds).ThenBy(item => item.index)
            .Select(item => item.value).ToList();
        if (ordered.Count <= MaximumComments) return [.. ordered];
        // 超量时均匀抽取，保住整集的时间覆盖，而不是只留开头。
        var builder = ImmutableArray.CreateBuilder<BulletChatComment>(MaximumComments);
        for (var index = 0; index < MaximumComments; index++) builder.Add(ordered[(int)((long)index * ordered.Count / MaximumComments)]);
        return builder.MoveToImmutable();
    }

    private static bool TryParse(string? position, string? message, out BulletChatComment value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(position) || string.IsNullOrWhiteSpace(message)) return false;
        var fields = position.Split(',');
        if (fields.Length < 3 ||
            !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) || !double.IsFinite(time) || time < 0 || time > 86400 ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kind) ||
            !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var color)) return false;
        // 1–3 滚动、4 底部、5 顶部；逆向、高级与代码弹幕不支持。
        BulletChatMode mode;
        switch (kind)
        {
            case 1 or 2 or 3: mode = BulletChatMode.Scroll; break;
            case 4: mode = BulletChatMode.Bottom; break;
            case 5: mode = BulletChatMode.Top; break;
            default: return false;
        }
        var text = Clean(message);
        if (text.Length == 0) return false;
        value = new(time, mode, color & 0xFFFFFF, text);
        return true;
    }

    // 换行和制表符是正常输入；其余控制字符（含 C1 区）不会出现在人写的弹幕里。
    private static bool IsBinary(string message)
    {
        foreach (var character in message)
            if (char.IsControl(character) && character is not ('\t' or '\n' or '\r')) return true;
        return false;
    }

    private static string? Sender(string? position)
    {
        if (position is null) return null;
        var fields = position.Split(',');
        return fields.Length >= 4 ? fields[3].Trim() : null;
    }

    private static string Clean(string message)
    {
        var builder = new System.Text.StringBuilder(Math.Min(message.Length, MaximumLength));
        foreach (var character in message)
        {
            if (builder.Length >= MaximumLength) break;
            // 换行与控制字符会破坏单行排版，统一成空格。
            builder.Append(char.IsControl(character) ? ' ' : character);
        }
        // 截断不能留下半个代理对。
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) builder.Length--;
        return builder.ToString().Trim();
    }
}
