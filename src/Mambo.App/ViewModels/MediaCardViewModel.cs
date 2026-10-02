using System.Globalization;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;

namespace Mambo.App.ViewModels;

public enum CardContext
{
    /// <summary>首页各库"最新"：副标题为年份或集号。</summary>
    Latest,
    /// <summary>首页"最近播放"与最近播放页：单集用剧名作标题，副标题为「S01E02 · 集名」。</summary>
    ContinueWatching,
    /// <summary>资料库网格：副标题为「年份 · 进度%」。</summary>
    Library,
    /// <summary>搜索结果。</summary>
    Search,
}

/// <summary>卡片的只读投影；不把带 init 属性的 Contracts record 交给 XAML。</summary>
public sealed partial class MediaCardViewModel
{
    private const long TicksPerSecond = 10_000_000;

    internal MediaCardViewModel(MediaItem item, CardContext context, bool landscape)
    {
        Item = item;
        Id = item.Id;
        var episode = item.Kind == MediaKind.Episode;
        Title = context == CardContext.ContinueWatching && episode && !string.IsNullOrEmpty(item.SeriesName) ? item.SeriesName! : item.Name;
        Subtitle = context switch
        {
            CardContext.ContinueWatching => episode ? EpisodeLine(item) : Year(item),
            CardContext.Library => JoinParts(Year(item), ProgressPercent(item)),
            CardContext.Latest when episode => EpisodeCode(item),
            _ => Year(item),
        };
        Image = landscape ? ImagePicker.Landscape(item) : ImagePicker.First(item, ImageKind.Primary);
        RatingText = item.CommunityRating is > 0 and var rating ? rating.ToString("0.0", CultureInfo.InvariantCulture) : "";
        var fraction = Progress(item);
        ProgressFraction = fraction;
        Played = item.UserData.Played;
    }

    internal MediaItem Item { get; }
    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public object? Image { get; }
    public string RatingText { get; }
    public bool HasRating => RatingText.Length > 0;
    public bool Played { get; }
    public double ProgressFraction { get; }
    public bool HasProgress => ProgressFraction > 0;
    public GridLength ProgressDone => new(Math.Max(ProgressFraction, 0.0001), GridUnitType.Star);
    public GridLength ProgressRest => new(Math.Max(1 - ProgressFraction, 0.0001), GridUnitType.Star);

    internal static double Progress(MediaItem item)
    {
        var position = item.UserData.PlaybackPositionTicks;
        var duration = item.RunTimeTicks ?? 0;
        return position > 0 && duration > 0 && !item.UserData.Played ? Math.Clamp((double)position / duration, 0, 1) : 0;
    }

    internal static string EpisodeCode(MediaItem item) =>
        item.ParentIndexNumber is { } season && item.IndexNumber is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{number:00}")
            : item.IndexNumber is { } only ? string.Create(CultureInfo.InvariantCulture, $"第 {only} 集") : "";

    internal static string EpisodeLine(MediaItem item) => JoinParts(EpisodeCode(item), item.Name);

    internal static string Year(MediaItem item) =>
        item.ProductionYear is { } year ? year.ToString(CultureInfo.InvariantCulture) : "";

    internal static string JoinParts(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string ProgressPercent(MediaItem item)
    {
        var fraction = Progress(item);
        return fraction > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fraction * 100:0}%") : "";
    }

    internal static string RemainingText(MediaItem item)
    {
        var duration = item.RunTimeTicks ?? 0;
        var remaining = Math.Max(0, duration - item.UserData.PlaybackPositionTicks) / TicksPerSecond;
        var minutes = (int)Math.Ceiling(remaining / 60.0);
        return minutes >= 60 ? $"剩余 {minutes / 60} 小时 {minutes % 60} 分钟" : $"剩余 {Math.Max(1, minutes)} 分钟";
    }
}

/// <summary>按卡片回退顺序选图：同一 Kind 里契约已经把自身的图排在父级之前。</summary>
public static class ImagePicker
{
    public static ImageRef? First(MediaItem item, ImageKind kind) =>
        item?.Images.FirstOrDefault(i => i.Kind == kind);

    public static ImageRef? First(MediaItem item, params ImageKind[] order)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(order);
        foreach (var kind in order)
            if (First(item, kind) is { } image) return image;
        return null;
    }

    /// <summary>横版卡片：单集用自己的剧照（Primary），其他条目用 Thumb → Backdrop → Primary。</summary>
    public static ImageRef? Landscape(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == MediaKind.Episode && item.Images.FirstOrDefault(i => i.Kind == ImageKind.Primary && i.ItemId == item.Id) is { } still)
            return still;
        return First(item, ImageKind.Thumb, ImageKind.Backdrop, ImageKind.Primary);
    }

    public static ImageRef? Backdrop(MediaItem item) => First(item, ImageKind.Backdrop, ImageKind.Thumb, ImageKind.Primary);
}
