using System.Text.RegularExpressions;

namespace Mambo.Core.Diagnostics;

public static partial class UrlRedactor
{
    // 敏感自由文本值包括引号包裹的JSON/头值；异常文本不能直接进入日志。
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = Header().Replace(text, "X-Emby-Token: <redacted>");
        value = KeyValue().Replace(value, match => match.Groups[1].Value + match.Groups[2].Value + "<redacted>");
        return PlayPath().Replace(value, "/play/<redacted>");
    }
    [GeneratedRegex(@"X-Emby-Token\s*:\s*[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Header();
    [GeneratedRegex("(\\b(?:api_key|access_token|token|password|authorization|signature|sig|expires|policy|key-pair-id|x-amz-[\\w-]+|x-oss-[\\w-]+|[\\w-]*(?:token|password|authorization|signature)[\\w-]*))([\\\"']?\\s*[=:]\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|[^&\\s,;\\\"'}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();
    [GeneratedRegex(@"/play/[^\s?\""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayPath();
}
