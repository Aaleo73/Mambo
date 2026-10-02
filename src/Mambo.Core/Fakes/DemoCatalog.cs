using System.Collections.Immutable;
using System.Globalization;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>只使用合成元数据的确定性目录，包含大资料库与多季连播场景。</summary>
public sealed class DemoCatalog
{
    public const string MoviesLibraryId = "demo-library-movies";
    public const string ShowsLibraryId = "demo-library-shows";
    public const string VideosLibraryId = "demo-library-videos";
    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly ImmutableDictionary<string, MediaItem> byId;
    public ImmutableArray<MediaLibrary> Libraries { get; } =
    [
        new(MoviesLibraryId, "电影", LibraryKind.Movies),
        new(ShowsLibraryId, "剧集", LibraryKind.TvShows),
        new(VideosLibraryId, "短片", LibraryKind.Mixed),
    ];
    public ImmutableArray<MediaItem> AllItems { get; }

    public DemoCatalog()
    {
        var items = ImmutableArray.CreateBuilder<MediaItem>();
        for (var index = 1; index <= 5000; index++)
        {
            var id = "demo-movie-" + index.ToString("0000", CultureInfo.InvariantCulture);
            items.Add(Create(id, "电影 " + index.ToString("0000", CultureInfo.InvariantCulture),
                MediaKind.Movie, MoviesLibraryId, index) with
            {
                RunTimeTicks = TimeSpan.FromMinutes(80 + index % 80).Ticks,
                UserData = new UserDataState(index % 7 == 0 ? TimeSpan.FromMinutes(12 + index % 40).Ticks : 0,
                    index % 13 == 0, index % 13 == 0 ? 1 : 0,
                    index % 7 == 0 || index % 13 == 0 ? Epoch.AddMinutes(index) : null),
            });
        }

        string[] seriesNames = ["星际回声", "山海旅途", "深海来信"];
        for (var series = 1; series <= seriesNames.Length; series++)
        {
            var seriesId = "demo-series-" + series.ToString("000", CultureInfo.InvariantCulture);
            var seriesName = seriesNames[series - 1];
            items.Add(Create(seriesId, seriesName, MediaKind.Series, ShowsLibraryId, series + 100));
            for (var season = 1; season <= 3; season++)
            {
                var seasonId = "demo-season-" + series.ToString("000", CultureInfo.InvariantCulture) +
                    "-" + season.ToString("00", CultureInfo.InvariantCulture);
                items.Add(Create(seasonId, $"第 {season} 季", MediaKind.Season, ShowsLibraryId, series * 100 + season) with
                {
                    SeriesId = seriesId, SeriesName = seriesName, IndexNumber = season,
                });
                for (var episode = 1; episode <= 12; episode++)
                {
                    var episodeId = "demo-episode-" + series.ToString("000", CultureInfo.InvariantCulture) +
                        "-" + season.ToString("00", CultureInfo.InvariantCulture) +
                        "-" + episode.ToString("00", CultureInfo.InvariantCulture);
                    var ordinal = series * 1000 + season * 100 + episode;
                    items.Add(Create(episodeId, "第 " + episode.ToString("00", CultureInfo.InvariantCulture) + " 集",
                        MediaKind.Episode, ShowsLibraryId, ordinal) with
                    {
                        SeriesId = seriesId, SeriesName = seriesName, SeasonId = seasonId,
                        ParentIndexNumber = season, IndexNumber = episode,
                        RunTimeTicks = TimeSpan.FromMinutes(42 + episode % 6).Ticks,
                        UserData = new UserDataState(season == 1 && episode is 3 or 4 ? TimeSpan.FromMinutes(15).Ticks : 0,
                            season == 1 && episode < 3, season == 1 && episode < 3 ? 1 : 0,
                            season == 1 && episode <= 4 ? Epoch.AddDays(10 + series).AddMinutes(episode) : null),
                    });
                }
            }
        }

        for (var index = 1; index <= 24; index++)
        {
            var id = "demo-video-" + index.ToString("00", CultureInfo.InvariantCulture);
            items.Add(Create(id, "短片 " + index.ToString("00", CultureInfo.InvariantCulture),
                MediaKind.Video, VideosLibraryId, index + 200) with
            {
                RunTimeTicks = TimeSpan.FromMinutes(5 + index % 15).Ticks,
            });
        }
        AllItems = items.ToImmutable();
        byId = AllItems.ToImmutableDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public MediaItem? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return byId.GetValueOrDefault(id);
    }

    private static MediaItem Create(string id, string name, MediaKind kind, string libraryId, int ordinal) =>
        new(id, name, kind)
        {
            LibraryId = libraryId,
            SortName = name,
            Overview = "这是一段用于界面开发与离线演示的合成简介。所有人物、条目和图片均为程序生成。",
            ProductionYear = 2000 + ordinal % 26,
            PremiereDate = Epoch.AddDays(ordinal % 365),
            CommunityRating = Math.Round(6 + ordinal % 40 / 10d, 1),
            OfficialRating = ordinal % 2 == 0 ? "PG" : "PG-13",
            Genres = (ordinal % 3) switch { 0 => ["科幻", "冒险"], 1 => ["剧情"], _ => ["纪录", "冒险"] },
            Images = [new(id, ImageKind.Primary, "demo-v1"), new(id, ImageKind.Backdrop, "demo-v1")],
            People =
            [
                new("demo-person-actor", "演示演员", PersonKind.Actor)
                {
                    Role = "主角", Image = new("demo-person-actor", ImageKind.Primary, "demo-v1"),
                },
                new("demo-person-director", "演示导演", PersonKind.Director)
                {
                    Image = new("demo-person-director", ImageKind.Primary, "demo-v1"),
                },
            ],
        };
}
