using System.Globalization;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

/// <summary>hero 幻灯片：背景优先 backdrop，其次 thumb、primary；有 logo 显示 logo，否则显示标题。</summary>
public sealed class HeroSlideViewModel
{
    internal HeroSlideViewModel(MediaItem item)
    {
        Id = item.Id;
        Title = item.Kind == MediaKind.Episode && !string.IsNullOrEmpty(item.SeriesName) ? item.SeriesName! : item.Name;
        Overview = item.Overview ?? "";
        RatingText = item.CommunityRating is > 0 and var rating ? rating.ToString("0.0", CultureInfo.InvariantCulture) : "";
        Year = item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "";
        Genres = string.Join("/", item.Genres.Take(3));
        OfficialRating = item.OfficialRating ?? "";
        Backdrop = ImagePicker.Backdrop(item);
        Logo = ImagePicker.First(item, ImageKind.Logo);
    }

    public string Id { get; }
    public string Title { get; }
    public string Overview { get; }
    public string RatingText { get; }
    public string Year { get; }
    public string Genres { get; }
    public string OfficialRating { get; }
    internal ImageRef? Backdrop { get; }
    internal ImageRef? Logo { get; }

    internal bool HasSameContent(HeroSlideViewModel other) =>
        Id == other.Id && Title == other.Title && Overview == other.Overview && RatingText == other.RatingText &&
        Year == other.Year && Genres == other.Genres && OfficialRating == other.OfficialRating && Backdrop == other.Backdrop && Logo == other.Logo;
}
