using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mambo.Core.Contracts;

namespace Mambo.Core.BulletChat;

/// <summary>要匹配的播放条目：剧集带季号与集号，电影只有片名。</summary>
public sealed record BulletChatTarget(string Title, int? Season, int? Episode, int? Year)
{
    public bool IsMovie => Episode is null;

    /// <summary>第 0 季的特别篇和没有集号的剧集无法可靠匹配，返回 null。</summary>
    public static BulletChatTarget? From(PlaybackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrWhiteSpace(entry.SeriesName))
            return entry.EpisodeNumber is > 0 && entry.SeasonNumber != 0
                ? new(entry.SeriesName.Trim(), entry.SeasonNumber, entry.EpisodeNumber, entry.ProductionYear) : null;
        return string.IsNullOrWhiteSpace(entry.Title) ? null : new(entry.Title.Trim(), null, null, entry.ProductionYear);
    }
}

/// <summary>选中的剧集。Offset 是它在正片列表里的位置与集号的差，用来让同一季后续各集沿用。</summary>
public sealed record BulletChatPick(DandanEpisode Episode, int Offset);

/// <summary>自动匹配的纯函数部分：给候选作品打分、在剧集列表里取集。</summary>
public static partial class BulletChatMatcher
{
    public const double MinimumSimilarity = 0.75;

    /// <summary>按可信度从高到低排列可接受的候选；明确属于别的季、类型不符或标题不像的被淘汰。</summary>
    public static IReadOnlyList<DandanAnime> Rank(BulletChatTarget target, IReadOnlyList<DandanAnime>? candidates)
    {
        ArgumentNullException.ThrowIfNull(target);
        var wanted = SplitSeason(target.Title).Title;
        var scored = new List<(DandanAnime Anime, double Score)>();
        foreach (var anime in candidates ?? [])
        {
            if (anime is null || string.IsNullOrWhiteSpace(anime.AnimeTitle)) continue;
            if (Score(target, wanted, anime.AnimeTitle, anime.Type, anime.Year) is { } score) scored.Add((anime, score));
        }
        return [.. scored.OrderByDescending(item => item.Score).ThenBy(item => item.Anime.StartDate ?? "9999", StringComparer.Ordinal)
            .ThenBy(item => item.Anime.AnimeId).Select(item => item.Anime)];
    }

    /// <summary>文件名匹配接口返回的是一串猜测；只有第一名明显领先时才采用。</summary>
    public static DandanMatch? BestMatch(BulletChatTarget target, IReadOnlyList<DandanMatch>? matches)
    {
        ArgumentNullException.ThrowIfNull(target);
        var wanted = SplitSeason(target.Title).Title;
        var scored = new List<(DandanMatch Match, double Score)>();
        foreach (var match in matches ?? [])
        {
            if (match is null || match.EpisodeId <= 0 || string.IsNullOrWhiteSpace(match.AnimeTitle)) continue;
            if (Score(target, wanted, match.AnimeTitle, match.Type, null) is { } score) scored.Add((match, score));
        }
        if (scored.Count == 0) return null;
        scored.Sort((left, right) => right.Score.CompareTo(left.Score));
        return scored.Count == 1 || scored[0].Score - scored[1].Score >= 0.2 ? scored[0].Match : null;
    }

    private static double? Score(BulletChatTarget target, string wantedTitle, string candidateTitle, string? type, int? candidateYear)
    {
        var (title, season) = SplitSeason(candidateTitle);
        var similarity = Similarity(wantedTitle, title);
        if (similarity < MinimumSimilarity) return null;
        var movie = type is "movie" or "jpmovie";
        var score = similarity;
        if (target.IsMovie) score += movie ? 0.3 : -0.3;
        else
        {
            // 剧集的某一集不会对应到剧场版或音乐视频条目。
            if (movie || type is "musicvideo") return null;
            var wantedSeason = target.Season ?? 1;
            if (season is { } marked)
            {
                if (marked != wantedSeason) return null;
                score += 0.5;
            }
            else if (wantedSeason <= 1) score += 0.3;
            else
            {
                // 没有季标记的条目要靠年份判断；年份差得远就不可能是这一季。
                if (target.Year is { } year && candidateYear is { } start && Math.Abs(year - start) > 1) return null;
                // 第 N 季通常带后缀或副标题，与原名完全一致的多半是第一季。
                if (similarity > 0.999) score -= 0.25;
            }
        }
        if (target.Year is { } wantedYear && candidateYear is { } candidate)
            score += Math.Abs(wantedYear - candidate) switch { 0 => 0.3, 1 => 0.15, _ => -0.2 };
        return score;
    }

    /// <summary>正片：集号是正整数的剧集，按集号升序。特别篇、片头片尾（S1、C1）不在其中。</summary>
    public static IReadOnlyList<DandanEpisode> Regular(IReadOnlyList<DandanEpisode>? episodes) =>
        [.. (episodes ?? []).Where(episode => episode is not null && episode.EpisodeId > 0 && Number(episode) > 0)
            .OrderBy(Number).ThenBy(episode => episode.EpisodeId)];

    public static BulletChatPick? PickEpisode(BulletChatTarget target, IReadOnlyList<DandanEpisode>? episodes)
    {
        ArgumentNullException.ThrowIfNull(target);
        var regular = Regular(episodes);
        if (target.Episode is not { } wanted)
        {
            var only = regular.Count > 0 ? regular[0] : (episodes ?? []).FirstOrDefault(episode => episode is not null && episode.EpisodeId > 0);
            return only is null ? null : new(only, 0);
        }
        if (regular.Count == 0) return null;
        // 有的作品第二季续接第一季的编号（从第 29 话开始）；Emby 每季从 1 起算，此时按位置取。
        var continued = Number(regular[0]) > 1 && (target.Season ?? 1) >= 2;
        var index = continued && wanted <= regular.Count ? wanted - 1 : IndexOfNumber(regular, wanted);
        return index < 0 ? null : new(regular[index], index - (wanted - 1));
    }

    /// <summary>沿用记忆里的位置偏移取集；超出范围返回 null。</summary>
    public static DandanEpisode? PickByOffset(int wantedEpisode, int offset, IReadOnlyList<DandanEpisode>? episodes)
    {
        var regular = Regular(episodes);
        var index = (long)wantedEpisode - 1 + offset;
        return index >= 0 && index < regular.Count ? regular[(int)index] : null;
    }

    public static int IndexOfNumber(IReadOnlyList<DandanEpisode> regular, int number)
    {
        for (var index = 0; index < regular.Count; index++) if (Number(regular[index]) == number) return index;
        return -1;
    }

    /// <summary>集号超出条目集数时可以顺延的后续条目：同一作品、开播更晚，按开播时间排列。</summary>
    public static IReadOnlyList<DandanAnime> Sequels(DandanAnime chosen, IReadOnlyList<DandanAnime>? candidates)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var title = SplitSeason(chosen.AnimeTitle ?? "").Title;
        return [.. (candidates ?? []).Where(anime => anime is not null && anime.AnimeId != chosen.AnimeId && anime.Type is not ("movie" or "jpmovie" or "musicvideo") &&
                !string.IsNullOrWhiteSpace(anime.AnimeTitle) && string.CompareOrdinal(anime.StartDate ?? "", chosen.StartDate ?? "") > 0 &&
                Similarity(title, SplitSeason(anime.AnimeTitle!).Title) >= MinimumSimilarity)
            .OrderBy(anime => anime.StartDate, StringComparer.Ordinal).ThenBy(anime => anime.AnimeId)];
    }

    private static int Number(DandanEpisode episode) =>
        int.TryParse(episode.EpisodeNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <summary>拆出季标记：返回去掉标记后的标题和季号；没有标记时季号为 null。</summary>
    public static (string Title, int? Season) SplitSeason(string title)
    {
        var text = (title ?? "").Normalize(NormalizationForm.FormKC).Trim();
        foreach (var pattern in (Regex[])[OrdinalSeason(), EnglishSeason(), EnglishOrdinalSeason(), ShortSeason(), RomanSeason(), TrailingNumber()])
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            var season = SeasonNumber(match.Groups[1].Value);
            if (season is null or < 1 or > 99) continue;
            var rest = (text[..match.Index] + " " + text[(match.Index + match.Length)..]).Trim();
            // 整个标题就是一个数字或季标记时，不当作季号。
            if (rest.Length == 0) continue;
            return (rest, season);
        }
        return (text, null);
    }

    private static int? SeasonNumber(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return number;
        switch (value.ToUpperInvariant())
        {
            case "II": return 2;
            case "III": return 3;
            case "IV": return 4;
            case "V": return 5;
            case "VI": return 6;
        }
        // 一 … 九十九
        const string digits = "一二三四五六七八九";
        var total = 0;
        var tens = value.IndexOf('十', StringComparison.Ordinal);
        if (tens < 0) return value.Length == 1 && digits.IndexOf(value[0], StringComparison.Ordinal) is >= 0 and var unit ? unit + 1 : null;
        if (tens > 1 || value.Length > tens + 2) return null;
        total += tens == 0 ? 10 : (digits.IndexOf(value[0], StringComparison.Ordinal) is >= 0 and var ten ? (ten + 1) * 10 : -1000);
        if (value.Length > tens + 1) total += digits.IndexOf(value[tens + 1], StringComparison.Ordinal) is >= 0 and var one ? one + 1 : -1000;
        return total > 0 ? total : null;
    }

    /// <summary>忽略大小写、空白与标点后的 Jaro-Winkler 相似度，0–1。</summary>
    public static double Similarity(string left, string right)
    {
        var a = Fold(left);
        var b = Fold(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var matchedA = new bool[a.Length];
        var matchedB = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var end = Math.Min(b.Length - 1, i + window);
            for (var j = Math.Max(0, i - window); j <= end; j++)
            {
                if (matchedB[j] || a[i] != b[j]) continue;
                matchedA[i] = matchedB[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;
        var transpositions = 0;
        for (int i = 0, j = 0; i < a.Length; i++)
        {
            if (!matchedA[i]) continue;
            while (!matchedB[j]) j++;
            if (a[i] != b[j]) transpositions++;
            j++;
        }
        var jaro = ((double)matches / a.Length + (double)matches / b.Length + (matches - transpositions / 2.0) / matches) / 3;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in (value ?? "").Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    [GeneratedRegex(@"第\s*([0-9]{1,2}|[一二三四五六七八九十]{1,3})\s*[季期部]")] private static partial Regex OrdinalSeason();
    [GeneratedRegex(@"\bseason\s*([0-9]{1,2})\b", RegexOptions.IgnoreCase)] private static partial Regex EnglishSeason();
    [GeneratedRegex(@"\b([0-9]{1,2})(?:st|nd|rd|th)\s+season\b", RegexOptions.IgnoreCase)] private static partial Regex EnglishOrdinalSeason();
    [GeneratedRegex(@"(?<![A-Za-z0-9])S([0-9]{1,2})(?![A-Za-z0-9])")] private static partial Regex ShortSeason();
    [GeneratedRegex(@"\s(II|III|IV|V|VI)\s*$")] private static partial Regex RomanSeason();
    [GeneratedRegex(@"(?<=[^0-9\s])\s*([2-9])\s*$")] private static partial Regex TrailingNumber();
}
