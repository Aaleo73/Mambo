using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mambo.Core.Subtitles;

/// <summary>保守识别明确季集号；无法判断的命名不变成猜测的媒体关联。</summary>
public static partial class SubtitleFilenameParser
{
    public static ParsedSubtitleFilename Parse(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (fileName.Length > 512 || fileName.Any(char.IsControl) || fileName.IndexOfAny(['/', '\\']) >= 0)
            return Ambiguous();
        var stem = Path.GetFileNameWithoutExtension(fileName).Normalize(NormalizationForm.FormKC);
        var matches = SeasonEpisode().Matches(stem).Cast<Match>()
            .Concat(NumberPair().Matches(stem).Cast<Match>())
            .Concat(ChineseEpisode().Matches(stem).Cast<Match>()).OrderBy(match => match.Index).ToArray();
        if (matches.Length > 1) return Ambiguous();
        if (matches.Length == 1)
        {
            var match = matches[0];
            var tail = Metadata().Replace(stem[(match.Index + match.Length)..], " ");
            if (!TryNumber(match.Groups["episode"].Value, out var episode) ||
                match.Groups["season"].Success && !TryNumber(match.Groups["season"].Value, out _) ||
                RangeTail().IsMatch(tail) || IncompleteMarker().IsMatch(tail)) return Ambiguous();
            int? season = match.Groups["season"].Success && TryNumber(match.Groups["season"].Value, out var parsed) ? parsed : null;
            // 裸 NxM 也用于分辨率，不将这些常见尺寸解释为数百季的剧集。
            if (match.Value.Contains('x', StringComparison.OrdinalIgnoreCase) && season is >= 100) return Ambiguous();
            // 季集号之前的连接分隔符可以修剪，剧名内部的符号必须保留。
            var title = stem[..match.Index];
            if (title.EndsWith('-')) title = title[..^1];
            return new(TitleSeparators().Replace(title, " ").Trim(), season, episode, false);
        }
        // 残缺的季集、多集范围和校验码不能绕过到“单文件用于当前片”。
        // 先移除明确语言/编码等元数据，但保留分辨率，避免 .chs 等后缀遮住歧义检查。
        var identifier = Metadata().Replace(stem, static match => ResolutionOnly().IsMatch(match.Value) ? match.Value : " ")
            .Trim(' ', '.', '_', '-');
        if (IncompleteMarker().IsMatch(stem) || BareRange().IsMatch(identifier) || Checksum().IsMatch(identifier) ||
            ResolutionOnly().IsMatch(identifier)) return Ambiguous();
        var cleaned = CleanTitle(stem);
        var numeric = Separators().Replace(cleaned, " ").Trim();
        if (int.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number is >= 0 and <= 999999 && number is not (>= 1900 and <= 2099)
                ? new("", null, number, false) : Ambiguous();
        return new(NeutralName().IsMatch(cleaned) ? "" : cleaned, null, null, false);
    }

    public static string NormalizeTitle(string title) => new(title.Normalize(NormalizationForm.FormKC)
        .Where(character => !char.IsWhiteSpace(character) && character is not '.' and not '_').Select(char.ToUpperInvariant).ToArray());

    private static string CleanTitle(string text) => TitleSeparators().Replace(Metadata().Replace(text, " "), " ").Trim();
    private static ParsedSubtitleFilename Ambiguous() => new("", null, null, true);

    private static bool TryNumber(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return value is >= 0 and <= 999999;
        value = 0;
        if (text.Length == 0) return false;
        const string digits = "零一二三四五六七八九";
        if (!text.Any(character => character is '十' or '百' or '千' or '万'))
        {
            foreach (var character in text)
            {
                var digit = character is '〇' ? 0 : character is '两' ? 2 : digits.IndexOf(character);
                if (digit < 0 || value > 99999) return false;
                value = value * 10 + digit;
            }
            return true;
        }
        var pending = 0; var section = 0; var previousUnit = 10000;
        foreach (var character in text)
        {
            var digit = character is '〇' ? 0 : character is '两' ? 2 : digits.IndexOf(character);
            if (digit >= 0) { if (pending != 0) return false; pending = digit; continue; }
            var unit = character switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 0 };
            if (unit == 0) return false;
            if (unit == 10000)
            {
                value += (section + pending) * unit; section = 0; pending = 0; previousUnit = 10000;
            }
            else
            {
                if (unit >= previousUnit) return false;
                section += (pending == 0 ? 1 : pending) * unit; pending = 0; previousUnit = unit;
            }
        }
        value += section + pending;
        return value is >= 0 and <= 999999;
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])S(?<season>[0-9]{1,6})[ ._-]*E(?<episode>[0-9]{1,6})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisode();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<season>[0-9]{1,6})[xX](?<episode>[0-9]{1,6})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPair();
    [GeneratedRegex(@"(?:第(?<season>[0-9零〇一二两三四五六七八九十百千万]+)季[ ._-]*)?第(?<episode>[0-9零〇一二两三四五六七八九十百千万]+)[集话]", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseEpisode();
    [GeneratedRegex(@"^\s*(?:E[0-9]|[-+&,~～至到][ ._-]*(?:E|第)?[0-9零〇一二两三四五六七八九十百千万])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RangeTail();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:S[0-9]+|E[0-9]+)(?![\p{L}\p{N}])|第[0-9零〇一二两三四五六七八九十百千万]+季", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IncompleteMarker();
    [GeneratedRegex(@"(?:^|[ ._\[(])\d+\s*[-+&,、~～至到]\s*\d+(?:$|[ ._\])])", RegexOptions.CultureInvariant)]
    private static partial Regex BareRange();
    [GeneratedRegex(@"^[\[(]?[0-9a-f]{8,64}[\])]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Checksum();
    [GeneratedRegex(@"^[ ._\[\]()-]*(?:\d{3,5}[pi]|[248]k)[ ._\[\]()-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionOnly();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:chs|cht|chi|zho|zh(?:[-_]?(?:cn|tw|hans|hant))?|eng|en|jpn|ja|kor|ko|简中|繁中|简体|繁体|中文字幕|中文|[248]k|\d{3,5}[pi]|h[ ._-]?26[45]|x26[45]|hevc|avc|web[ ._-]?dl|webrip|bluray|aac|flac)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Metadata();
    [GeneratedRegex(@"[\s._\-\[\]()]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
    [GeneratedRegex(@"[\s._]+", RegexOptions.CultureInvariant)]
    private static partial Regex TitleSeparators();
    [GeneratedRegex(@"^(?:字幕|subtitle|subtitles)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeutralName();
}
