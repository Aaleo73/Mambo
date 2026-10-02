using System.Collections.Immutable;
using System.Text;
using Mambo.Core.Contracts;

namespace Mambo.Core.Networking;

/// <summary>只将可用于界面的元数据映射到契约，不传递 DTO、片源或认证字段。</summary>
public static class EmbyMapper
{
    public static MediaLibrary? Library(EmbyItem item)
    {
        var id = Identity(item.Id);
        if (id is null || item.Type?.Equals("CollectionFolder", StringComparison.OrdinalIgnoreCase) != true) return null;
        var kind = item.CollectionType?.ToLowerInvariant();
        if (kind is "books" or "boxsets" or "channels" or "folders" or "livetv" or "music" or "photos" or "playlists" or "audiobooks" or "podcasts") return null;
        return new(id, Text(item.Name) ?? "未命名媒体库", kind switch
        {
            "movies" => LibraryKind.Movies,
            "tvshows" => LibraryKind.TvShows,
            _ => LibraryKind.Mixed,
        });
    }

    public static MediaItem? Item(EmbyItem item, string? libraryId = null, MediaItem? series = null)
    {
        var id = Identity(item.Id);
        var kind = Kind(item.Type);
        if (id is null || kind is null) return null;
        return new(id, Text(item.Name) ?? "未命名媒体", kind.Value)
        {
            LibraryId = libraryId,
            SortName = Text(item.SortName), SeriesId = Identity(item.SeriesId),
            SeriesName = Text(item.SeriesName) ?? series?.Name, SeasonId = Identity(item.SeasonId),
            ParentIndexNumber = item.ParentIndexNumber, IndexNumber = item.IndexNumber,
            Overview = Text(item.Overview), ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate, DateCreatedUtc = item.DateCreated,
            CommunityRating = item.CommunityRating is { } rating && double.IsFinite(rating) ? rating : null,
            OfficialRating = Text(item.OfficialRating), RunTimeTicks = item.RunTimeTicks is >= 0 ? item.RunTimeTicks : null,
            Genres = Strings(item.Genres), Images = Images(item, series), People = People(item.People),
            UserData = new(Math.Max(0, item.UserData?.PlaybackPositionTicks ?? 0), item.UserData?.Played ?? false,
                item.UserData?.PlayCount is >= 0 ? item.UserData.PlayCount : null, item.UserData?.LastPlayedDate),
        };
    }

    public static ImmutableArray<ImageRef> Images(EmbyItem item, MediaItem? series = null)
    {
        var id = Identity(item.Id);
        if (id is null) return [];
        var images = ImmutableArray.CreateBuilder<ImageRef>();
        foreach (var kind in Enum.GetValues<ImageKind>())
        {
            if (kind == ImageKind.Backdrop && item.BackdropImageTags is { Length: > 0 } backdrops)
            {
                for (var index = 0; index < backdrops.Length; index++)
                    if (Text(backdrops[index]) is { } tag) images.Add(new(id, kind, tag, index));
            }
            else if (item.ImageTags?.FirstOrDefault(pair => pair.Key.Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase)).Value is { } ownTag && Text(ownTag) is not null)
                images.Add(new(id, kind, ownTag));

            if (kind == ImageKind.Primary && Identity(item.PrimaryImageItemId) is { } primary && Text(item.PrimaryImageTag) is { } primaryTag)
                images.Add(new(primary, kind, primaryTag));
            if (kind == ImageKind.Backdrop && Identity(item.ParentBackdropItemId) is { } parent && item.ParentBackdropImageTags is { } tags)
                for (var index = 0; index < tags.Length; index++)
                    if (Text(tags[index]) is { } tag) images.Add(new(parent, kind, tag, index));
            if (kind == ImageKind.Logo && Identity(item.ParentLogoItemId) is { } logo && Text(item.ParentLogoImageTag) is { } logoTag)
                images.Add(new(logo, kind, logoTag));
            if (series is not null)
                images.AddRange(series.Images.Where(image => image.Kind == kind));
        }
        return images.Distinct().ToImmutableArray();
    }

    public static ImmutableArray<string> Strings(IEnumerable<string>? values) => values is null ? [] :
        values.Select(Text).Where(value => value is not null).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    public static MediaKind? Kind(string? type) => type?.ToLowerInvariant() switch
    {
        "movie" => MediaKind.Movie, "series" => MediaKind.Series, "season" => MediaKind.Season,
        "episode" => MediaKind.Episode, "video" => MediaKind.Video, _ => null,
    };

    public static string? Identity(string? value) => string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
        Encoding.UTF8.GetByteCount(value) > 256 || value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal) ? null : value;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ImmutableArray<PersonInfo> People(EmbyPerson[]? people) => people is null ? [] :
        people.Where(person => Identity(person.Id) is not null).Select(person =>
            new PersonInfo(person.Id!, Text(person.Name) ?? "未命名演职人员", person.Type?.ToLowerInvariant() switch
            {
                "actor" => PersonKind.Actor, "director" => PersonKind.Director,
                "writer" => PersonKind.Writer, "producer" => PersonKind.Producer, _ => PersonKind.Crew,
            })
            {
                Role = Text(person.Role), Image = Text(person.PrimaryImageTag) is { } tag ?
                    new ImageRef(person.Id!, ImageKind.Primary, tag) : null,
            }).ToImmutableArray();
}
