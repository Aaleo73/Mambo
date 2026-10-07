using System.Text;
using Mambo.Core.Contracts;

namespace Mambo.Core.Playback;

/// <summary>可持久化的安全轨道特征，不包含 native ID、序号或文件路径。</summary>
public sealed record TrackFingerprint(string? Title, string? Language, bool IsForced, string? Codec, string? AudioChannels)
{
    public TrackKind Kind { get; init; }
}

public static class TrackSelection
{
    public static TrackFingerprint Fingerprint(TrackInfo track) => new(SafeText(track.Title), NormalizeLanguage(track.Language),
        track.IsForced, SafeText(track.Codec), SafeText(track.AudioChannels)) { Kind = track.Kind };

    public static string? Match(TrackFingerprint fingerprint, IEnumerable<TrackInfo> tracks)
    {
        var candidates = tracks.Where(track => track.Kind == fingerprint.Kind && track.Source != TrackSource.Local)
            .Select(track => (track.Id, Fingerprint: Fingerprint(track)))
            .Where(candidate => candidate.Fingerprint.Language == fingerprint.Language && candidate.Fingerprint.IsForced == fingerprint.IsForced)
            .ToArray();
        if (fingerprint.Title is not null)
            return Unique(candidates.Where(candidate => candidate.Fingerprint.Title == fingerprint.Title &&
                Compatible(fingerprint.Codec, candidate.Fingerprint.Codec) && Compatible(fingerprint.AudioChannels, candidate.Fingerprint.AudioChannels)).Select(candidate => candidate.Id));
        // 无标题且无语言不能凭缺失信息猜选。编码和声道可在严格候选中缩小范围；
        // 严格匹配失败时，只允许相同语言与 forced 的候选本身唯一。
        if (fingerprint.Language is null) return null;
        var exact = candidates.Where(candidate => Compatible(fingerprint.Codec, candidate.Fingerprint.Codec) &&
            Compatible(fingerprint.AudioChannels, candidate.Fingerprint.AudioChannels)).Select(candidate => candidate.Id).ToArray();
        return exact.Length == 1 ? exact[0] : exact.Length > 1 ? null : Unique(candidates.Select(candidate => candidate.Id));
    }

    private static bool Compatible(string? preferred, string? actual) => preferred is null || preferred == actual;
    private static string? Unique(IEnumerable<string> values)
    {
        var items = values.Take(2).ToArray();
        return items.Length == 1 ? items[0] : null;
    }

    public static string LanguageCodes(string preference, bool subtitle) => preference switch
    {
        "auto" => "",
        "off" when subtitle => "",
        "zh" when subtitle => "zh-CN,zh-Hans,chi,zho,chs,zh,cht,zh-Hant,zh-TW,eng,en",
        "zh" => "zh-CN,zh-Hans,chi,zho,chs,zh,cht,zh-Hant,zh-TW",
        "ja" => "ja,jpn",
        "en" => "en,eng",
        "ko" => "ko,kor",
        "fr" => "fr,fra,fre",
        "de" => "de,deu,ger",
        "es" => "es,spa",
        "ru" => "ru,rus",
        _ => throw Invalid(),
    };

    internal static bool IsPreference(string? value, bool subtitle) => value is "auto" or "zh" or "ja" or "en" or "ko" or "fr" or "de" or "es" or "ru" || subtitle && value == "off";

    internal static string? NormalizeLanguage(string? value)
    {
        var safe = SafeText(value, 32);
        if (safe is null || !safe.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')) return null;
        var language = safe.Split('-')[0];
        return language switch
        {
            "zh" or "zho" or "chi" or "chs" or "cht" => "zh",
            "ja" or "jpn" => "ja",
            "en" or "eng" => "en",
            "ko" or "kor" => "ko",
            "fr" or "fra" or "fre" => "fr",
            "de" or "deu" or "ger" => "de",
            "es" or "spa" => "es",
            "ru" or "rus" => "ru",
            "und" or "unk" => null,
            _ => safe,
        };
    }

    internal static string? SafeText(string? value, int maximum = 96)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) ||
            value.IndexOfAny(['/', '\\', ':', '?', '|']) >= 0) return null;
        var builder = new StringBuilder();
        var space = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character)) { space = builder.Length > 0; continue; }
            if (space) { builder.Append(' '); space = false; }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static AppException Invalid() => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "首选语言无效。", false));
}
