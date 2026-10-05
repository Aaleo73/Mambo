using System.Collections.Immutable;
using System.Globalization;
using Mambo.Core.BulletChat;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>程序生成的演示弹幕：同一条目每次得到相同内容，不联网。</summary>
public sealed class FakeBulletChatProvider(FakeOperation operation) : IBulletChatProvider
{
    private static readonly string[] Phrases =
    [
        "来了来了", "前方高能", "哈哈哈哈哈哈", "这段配乐太好听了", "名场面", "泪目", "第一次看的举手", "二刷报到", "爷青回",
        "这里的作画经费在燃烧", "好家伙", "？？？", "完结撒花", "太真实了", "这是可以说的吗", "awsl", "下次一定",
        "注意看，这个镜头后面还会再出现一次，是很重要的伏笔", "我宣布这是本季最佳的一集，没有之一，不接受反驳", "Bullet chat 😀🎉", "666",
    ];
    private static readonly uint[] Colors = [0xFF7204, 0xFFD302, 0xA0EE00, 0x00CD00, 0x019899, 0x89D5FF, 0xCC0273, 0xFE0302];

    public async Task<BulletChatResolution> ResolveAsync(PlaybackEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var episode = new BulletChatEpisode("demo-" + entry.ItemId, "demo", entry.SeriesName ?? entry.Title, entry.EpisodeLabel ?? entry.Title);
        return new(episode, Generate(entry.ItemId, entry.DurationTicks));
    }

    public async Task<ImmutableArray<BulletChatAnime>> SearchAsync(string keyword, CancellationToken cancellationToken)
    {
        await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return [.. Enumerable.Range(1, 3).Select(season => new BulletChatAnime("demo-" + season.ToString(CultureInfo.InvariantCulture),
            season == 1 ? keyword : string.Create(CultureInfo.InvariantCulture, $"{keyword} 第{season}季")) { TypeLabel = "TV动画", Year = 2020 + season, EpisodeCount = 12 })];
    }

    public async Task<ImmutableArray<BulletChatEpisode>> GetEpisodesAsync(string animeId, CancellationToken cancellationToken)
    {
        await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return [.. Enumerable.Range(1, 12).Select(number => new BulletChatEpisode(string.Create(CultureInfo.InvariantCulture, $"{animeId}-{number}"), animeId, "演示作品",
            string.Create(CultureInfo.InvariantCulture, $"第{number}话")))];
    }

    public async Task<BulletChatResolution> SelectAsync(PlaybackEntry entry, BulletChatEpisode episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(episode);
        await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new(episode, Generate(episode.Id, entry.DurationTicks));
    }

    /// <summary>平时每秒一两条，每 90 秒有一段 10 秒的高密度；九成滚动，其余顶部和底部各半。</summary>
    public static ImmutableArray<BulletChatComment> Generate(string seed, long? durationTicks)
    {
        var seconds = (int)Math.Clamp((durationTicks ?? TimeSpan.FromMinutes(24).Ticks) / TimeSpan.TicksPerSecond, 60, 4 * 3600);
        var random = new Random(StableHash(seed));
        var builder = ImmutableArray.CreateBuilder<BulletChatComment>();
        for (var second = 1; second < seconds; second++)
        {
            var count = second % 90 < 10 ? 8 : random.Next(0, 3);
            for (var index = 0; index < count; index++)
            {
                var roll = random.Next(100);
                var mode = roll < 90 ? BulletChatMode.Scroll : roll < 95 ? BulletChatMode.Top : BulletChatMode.Bottom;
                var color = random.Next(100) < 80 ? 0xFFFFFFu : Colors[random.Next(Colors.Length)];
                builder.Add(new(second + random.NextDouble(), mode, color, Phrases[random.Next(Phrases.Length)]));
            }
        }
        return [.. builder.OrderBy(comment => comment.TimeSeconds)];
    }

    // string.GetHashCode 每个进程不同；演示内容要可重复。
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var character in text) hash = (hash ^ character) * 16777619;
            return hash;
        }
    }
}
