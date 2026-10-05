using System.Text.Json.Nodes;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Mambo.Core.Session;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class VideoQualityPreferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SeriesOverridesSpanSeasonsWhileMoviesAndAccountsRemainIndependent()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        var first = Secret();
        accounts.Set(new(first));
        var episode = new PlaybackEntry("episode-1", "测试单集") { SeriesId = "series", SeasonId = "season-1" };
        var nextSeason = episode with { ItemId = "episode-2", SeasonId = "season-2" };
        var movie = new PlaybackEntry("movie", "测试影片");
        using (var settings = new SettingsStore(directory.Paths, new InlineScheduler()))
        {
            var preferences = new VideoQualityPreferences(settings, accounts);
            var account = accounts.Current!;
            Assert.Equal(VideoQualityMode.Standard, preferences.Get(account, movie));
            Assert.Equal(VideoQualityMode.Standard, preferences.Get(account, episode));
            await preferences.SaveAsync(account, episode, VideoQualityMode.Anime, Token);
            await preferences.SaveAsync(account, movie, VideoQualityMode.Clear, Token);
            Assert.Equal(VideoQualityMode.Anime, preferences.Get(account, nextSeason));
            Assert.Equal(VideoQualityMode.Clear, preferences.Get(account, movie));
            Assert.Equal(VideoQualityMode.Standard, preferences.Get(account, movie with { ItemId = "other-movie" }));
            accounts.Set(new(first with { UserId = Guid.NewGuid().ToString("N") }));
            Assert.Equal(VideoQualityMode.Standard, preferences.Get(accounts.Current!, movie));
            await preferences.SaveAsync(accounts.Current!, movie, VideoQualityMode.Anime, Token);
            accounts.Set(new(first with { ServerId = Guid.NewGuid().ToString("N") }));
            Assert.Equal(VideoQualityMode.Standard, preferences.Get(accounts.Current!, movie));
            accounts.Set(new(first));
            Assert.Equal(VideoQualityMode.Clear, preferences.Get(accounts.Current!, movie));
        }
        using var reopened = new SettingsStore(directory.Paths, new InlineScheduler());
        var restored = new VideoQualityPreferences(reopened, accounts);
        Assert.Equal(VideoQualityMode.Clear, restored.Get(accounts.Current!, movie));
        Assert.Equal(VideoQualityMode.Anime, restored.Get(accounts.Current!, nextSeason));
        var json = await File.ReadAllTextAsync(directory.Paths.Settings, Token);
        Assert.DoesNotContain(first.ServerAddress, json);
        Assert.DoesNotContain(first.AccessToken, json);
        Assert.DoesNotContain(first.UserId, json);
    }

    [Fact]
    public async Task MissingSeriesUsesItemAndCancelledAccountCannotWriteIntoNewAccount()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new VideoQualityPreferences(settings, accounts);
        var old = accounts.Current!;
        var first = new PlaybackEntry("episode-1", "没有剧标识的单集");
        await preferences.SaveAsync(old, first, VideoQualityMode.Anime, Token);
        Assert.Equal(VideoQualityMode.Standard, preferences.Get(old, first with { ItemId = "episode-2" }));
        accounts.Set(new(Secret()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferences.SaveAsync(old, first, VideoQualityMode.Clear, Token));
        Assert.Equal(VideoQualityMode.Standard, preferences.Get(accounts.Current!, first));
    }

    [Fact]
    public async Task ParallelPreferenceAndSettingsWritesPreserveUnrelatedValues()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new VideoQualityPreferences(settings, accounts);
        var entry = new PlaybackEntry("movie", "测试影片");
        var device = settings.Current.DeviceId;
        await Task.WhenAll(preferences.SaveAsync(accounts.Current!, entry, VideoQualityMode.Anime, Token),
            settings.UpdateAsync(value => value with { Volume = 37, ThemeMode = SettingsThemeMode.Dark,
                BulletChat = value.BulletChat with { Opacity = 0.4 } }, Token));
        Assert.Equal(device, settings.Current.DeviceId);
        Assert.Equal(37, settings.Current.Volume);
        Assert.Equal(SettingsThemeMode.Dark, settings.Current.ThemeMode);
        Assert.Equal(0.4, settings.Current.BulletChat.Opacity);
        Assert.Equal(VideoQualityMode.Anime, preferences.Get(accounts.Current!, entry));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("\"not-a-mode\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task CorruptQualityFieldsAreRepairedWithoutResettingOtherSettings(string invalid)
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        Guid device;
        using (var settings = new SettingsStore(directory.Paths, new InlineScheduler()))
        {
            device = settings.Current.DeviceId;
            await settings.UpdateAsync(value => value with { Volume = 37 }, Token);
        }
        var root = JsonNode.Parse(await File.ReadAllTextAsync(directory.Paths.Settings, Token))!.AsObject();
        // 早期版本写过的全局默认画质字段已移除，无论内容如何都只应被忽略。
        root["Settings"]!["DefaultVideoQualityMode"] = JsonNode.Parse(invalid);
        root["VideoQualityPreferences"] = new JsonObject
        {
            [accounts.Current!.Scope + "|item|bad"] = JsonNode.Parse(invalid),
            [accounts.Current.Scope + "|item|good"] = (int)VideoQualityMode.Clear,
            ["invalid-key"] = (int)VideoQualityMode.Anime,
        };
        await File.WriteAllTextAsync(directory.Paths.Settings, root.ToJsonString(), Token);
        using var reopened = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new VideoQualityPreferences(reopened, accounts);
        Assert.Equal(device, reopened.Current.DeviceId);
        Assert.Equal(37, reopened.Current.Volume);
        Assert.Equal(VideoQualityMode.Clear, preferences.Get(accounts.Current, new("good", "测试影片")));
        Assert.Equal(VideoQualityMode.Standard, preferences.Get(accounts.Current, new("bad", "测试影片")));
        var repaired = await File.ReadAllTextAsync(directory.Paths.Settings, Token);
        Assert.DoesNotContain("invalid-key", repaired);
        Assert.DoesNotContain("DefaultVideoQualityMode", repaired);
    }

    [Fact]
    public async Task OnlyTheMostRecentlyChosenContentIsKept()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        using (var settings = new SettingsStore(directory.Paths, new InlineScheduler()))
            await settings.UpdateAsync(value => value with { Volume = 37 }, Token);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(directory.Paths.Settings, Token))!.AsObject();
        var stored = new JsonObject();
        for (var index = 0; index < 503; index++) stored[accounts.Current!.Scope + "|item|old-" + index] = (int)VideoQualityMode.Clear;
        root["VideoQualityPreferences"] = stored;
        await File.WriteAllTextAsync(directory.Paths.Settings, root.ToJsonString(), Token);
        using var reopened = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new VideoQualityPreferences(reopened, accounts);
        var account = accounts.Current!;
        static PlaybackEntry Entry(string id) => new(id, "测试影片");
        Assert.Equal(VideoQualityMode.Standard, preferences.Get(account, Entry("old-2")));
        Assert.Equal(VideoQualityMode.Clear, preferences.Get(account, Entry("old-3")));
        // 重新选择会把这一项排到最新，随后新增的两项只挤掉更早的两项。
        await preferences.SaveAsync(account, Entry("old-3"), VideoQualityMode.Anime, Token);
        await preferences.SaveAsync(account, Entry("new-1"), VideoQualityMode.Anime, Token);
        await preferences.SaveAsync(account, Entry("new-2"), VideoQualityMode.Anime, Token);
        Assert.Equal(VideoQualityMode.Anime, preferences.Get(account, Entry("old-3")));
        Assert.Equal(VideoQualityMode.Standard, preferences.Get(account, Entry("old-4")));
        Assert.Equal(VideoQualityMode.Standard, preferences.Get(account, Entry("old-5")));
        Assert.Equal(VideoQualityMode.Clear, preferences.Get(account, Entry("old-6")));
        Assert.Equal(VideoQualityMode.Anime, preferences.Get(account, Entry("new-2")));
        Assert.Equal(37, reopened.Current.Volume);
    }

    [Fact]
    public async Task OldSettingsAndMissingPreferenceObjectUseStandardWithoutChangingDeviceId()
    {
        using var directory = new SettingsDirectory();
        var device = Guid.NewGuid();
        await File.WriteAllTextAsync(directory.Paths.Settings,
            $$"""{"Version":1,"Settings":{"DeviceId":"{{device}}","Volume":41},"VideoQualityPreferences":null}""", Token);
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        Assert.Equal(device, settings.Current.DeviceId);
        Assert.Equal(41, settings.Current.Volume);
        Assert.Equal(VideoQualityMode.Standard, new VideoQualityPreferences(settings, accounts).Get(accounts.Current!, new("movie", "测试影片")));
    }

    private static SessionSecret Secret() => new("https://" + Guid.NewGuid().ToString("N") + ".invalid",
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "测试用户", Guid.NewGuid().ToString("N"));
    private sealed class InlineScheduler : IUiScheduler { public bool TryEnqueue(Action action) { action(); return true; } }
    private sealed class SettingsDirectory : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "mambo-quality-" + Guid.NewGuid().ToString("N")));
        public SettingsDirectory() => Directory.CreateDirectory(Paths.Root);
        public void Dispose() => Directory.Delete(Paths.Root, true);
    }
}
