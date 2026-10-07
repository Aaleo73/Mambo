using System.Text.Json.Nodes;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Session;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class TrackPreferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TrackChoice Audio => new(false, TrackSelection.Fingerprint(new("1", TrackKind.Audio, "日语") { Language = "jpn" }));

    [Fact]
    public void MatchingUsesStableFeaturesAndRejectsAmbiguity()
    {
        var before = new TrackInfo("1", TrackKind.Audio, "日语") { Language = "jpn" };
        var after = before with { Id = "7", Language = "ja" };
        Assert.Equal("7", TrackSelection.Match(TrackSelection.Fingerprint(before), [after]));
        Assert.Null(TrackSelection.Match(TrackSelection.Fingerprint(before), [after, after with { Id = "8" }]));
        Assert.Null(TrackSelection.Match(TrackSelection.Fingerprint(before with { Title = "评论音轨" }), [after]));
        Assert.Null(TrackSelection.Match(TrackSelection.Fingerprint(before), [after with { IsForced = true }]));
        Assert.Equal("7", TrackSelection.Match(TrackSelection.Fingerprint(before with { Codec = "aac" }), [after with { Codec = "flac" }]));
        Assert.Null(TrackSelection.Match(TrackSelection.Fingerprint(before), [after with { Kind = TrackKind.Subtitle }]));
    }

    [Fact]
    public async Task RestartCrossSeasonAccountAndExactSubtitleRemainIndependent()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        var secret = Secret();
        accounts.Set(new(secret));
        var episode = new PlaybackEntry("episode-1", "单集") { SeriesId = "series", SeasonId = "season-1" };
        var next = episode with { ItemId = "episode-2", SeasonId = "season-2" };
        var local = new TrackChoice(false, LocalSubtitleId: Guid.NewGuid().ToString("N"));
        using (var settings = new SettingsStore(directory.Paths, new InlineScheduler()))
        {
            var preferences = new TrackPreferences(settings, accounts);
            var epoch = preferences.Capture();
            await preferences.SaveAsync(accounts.Current!, episode, TrackKind.Audio, Audio, epoch, false, Token);
            await preferences.SaveAsync(accounts.Current!, episode, TrackKind.Subtitle, new(true), epoch, false, Token);
            await preferences.SaveAsync(accounts.Current!, episode, TrackKind.Subtitle, local, epoch, true, Token);
            Assert.Equal(Audio, preferences.Get(accounts.Current!, next, TrackKind.Audio, epoch));
            Assert.Equal(new(true), preferences.Get(accounts.Current!, next, TrackKind.Subtitle, epoch));
            Assert.Null(preferences.Get(accounts.Current!, next, TrackKind.Subtitle, epoch, true));
            Assert.Equal(local, preferences.Get(accounts.Current!, episode, TrackKind.Subtitle, epoch, true));
            var old = accounts.Current!;
            accounts.Set(new(secret with { UserId = Guid.NewGuid().ToString("N") }));
            Assert.Null(preferences.Get(accounts.Current!, episode, TrackKind.Audio, epoch));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferences.SaveAsync(old, episode, TrackKind.Audio, Audio, epoch, false, Token));
            accounts.Set(new(secret with { ServerId = Guid.NewGuid().ToString("N") }));
            Assert.Null(preferences.Get(accounts.Current!, episode, TrackKind.Audio, epoch));
            accounts.Set(new(secret));
        }
        using var reopened = new SettingsStore(directory.Paths, new InlineScheduler());
        var restored = new TrackPreferences(reopened, accounts);
        var captured = restored.Capture();
        Assert.Equal(Audio, restored.Get(accounts.Current!, next, TrackKind.Audio, captured));
        Assert.Equal(local, restored.Get(accounts.Current!, episode, TrackKind.Subtitle, captured, true));
        await restored.ClearExactSubtitleAsync(accounts.Current!, episode, captured, Token);
        Assert.Null(restored.Get(accounts.Current!, episode, TrackKind.Subtitle, captured, true));
        Assert.Equal(new(true), restored.Get(accounts.Current!, episode, TrackKind.Subtitle, captured));
        var json = await File.ReadAllTextAsync(directory.Paths.Settings, Token);
        Assert.DoesNotContain(secret.ServerAddress, json);
        Assert.DoesNotContain(secret.AccessToken, json);
        Assert.DoesNotContain(secret.UserId, json);
    }

    [Fact]
    public async Task LanguageRevisionRejectsOldSessionEvenAfterLanguageChangesBack()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new TrackPreferences(settings, accounts);
        var entry = new PlaybackEntry("movie", "影片");
        var epoch = preferences.Capture();
        await preferences.SaveAsync(accounts.Current!, entry, TrackKind.Audio, Audio, epoch, false, Token);
        await preferences.SaveAsync(accounts.Current!, entry, TrackKind.Subtitle, new(true), epoch, false, Token);
        await settings.UpdateAsync(value => value with { PreferredAudioLanguage = "ja" }, Token);
        Assert.Null(preferences.Get(accounts.Current!, entry, TrackKind.Audio, preferences.Capture()));
        Assert.Equal(new(true), preferences.Get(accounts.Current!, entry, TrackKind.Subtitle, epoch));
        await settings.UpdateAsync(value => value with { PreferredAudioLanguage = "auto" }, Token);
        await preferences.SaveAsync(accounts.Current!, entry, TrackKind.Audio, Audio, epoch, false, Token);
        Assert.Null(preferences.Get(accounts.Current!, entry, TrackKind.Audio, preferences.Capture()));
        Assert.Equal(2, preferences.Capture().AudioRevision);
        await settings.UpdateAsync(value => value with { PreferredSubtitleLanguage = "en" }, Token);
        await preferences.SaveAsync(accounts.Current!, entry, TrackKind.Subtitle, new(true), epoch, false, Token);
        Assert.Null(preferences.Get(accounts.Current!, entry, TrackKind.Subtitle, preferences.Capture()));
        Assert.Equal(1, preferences.Capture().SubtitleRevision);
    }

    [Fact]
    public async Task ChoiceWaitingForSettingsWriteRechecksEpochInsideLock()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new TrackPreferences(settings, accounts);
        var epoch = preferences.Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var update = Task.Run(() => settings.UpdateAsync(value =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), Token)) throw new TimeoutException();
            return value with { PreferredAudioLanguage = "ja" };
        }, Token), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var save = preferences.SaveAsync(accounts.Current!, new("movie", "影片"), TrackKind.Audio, Audio, epoch, false, Token);
        release.Set();
        await Task.WhenAll(update, save);
        Assert.Null(preferences.Get(accounts.Current!, new("movie", "影片"), TrackKind.Audio, preferences.Capture()));
    }

    [Fact]
    public async Task CapacityCountsContentRatherThanTrackKindsAndRefreshesOrdering()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new TrackPreferences(settings, accounts);
        var epoch = preferences.Capture();
        for (var index = 0; index < 500; index++)
            await preferences.SaveAsync(accounts.Current!, new("item-" + index, "影片"), TrackKind.Audio, Audio, epoch, false, Token);
        await preferences.SaveAsync(accounts.Current!, new("item-0", "影片"), TrackKind.Subtitle, new(true), epoch, false, Token);
        await preferences.SaveAsync(accounts.Current!, new("new", "影片"), TrackKind.Audio, Audio, epoch, false, Token);
        Assert.Equal(Audio, preferences.Get(accounts.Current!, new("item-0", "影片"), TrackKind.Audio, epoch));
        Assert.Null(preferences.Get(accounts.Current!, new("item-1", "影片"), TrackKind.Audio, epoch));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(directory.Paths.Settings, Token))!;
        Assert.Equal(500, json["TrackPreferences"]!.AsObject().Count);
    }

    [Fact]
    public async Task ConcurrentSettingsAndChoicesPreserveAllFieldsAndRejectInvalidCommands()
    {
        using var directory = new SettingsDirectory();
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        var preferences = new TrackPreferences(settings, accounts);
        var epoch = preferences.Capture();
        var entry = new PlaybackEntry("movie", "影片");
        await Task.WhenAll(preferences.SaveAsync(accounts.Current!, entry, TrackKind.Audio, Audio, epoch, false, Token),
            preferences.SaveAsync(accounts.Current!, entry, TrackKind.Subtitle, new(true), epoch, false, Token),
            settings.UpdateAsync(value => value with { Volume = 37 }, Token));
        Assert.Equal(37, settings.Current.Volume);
        Assert.Equal(Audio, preferences.Get(accounts.Current!, entry, TrackKind.Audio, epoch));
        Assert.Equal(new(true), preferences.Get(accounts.Current!, entry, TrackKind.Subtitle, epoch));
        await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with { PreferredAudioLanguage = "unknown" }, Token));
        await Assert.ThrowsAsync<AppException>(() => preferences.SaveAsync(accounts.Current!, entry, TrackKind.Subtitle, new(false, LocalSubtitleId: "../subtitle.ass"), epoch, true, Token));
        await Assert.ThrowsAsync<AppException>(() => preferences.SaveAsync(accounts.Current!, entry, TrackKind.Subtitle, new(false, LocalSubtitleId: "opaque"), epoch, false, Token));
        Assert.Equal(epoch, preferences.Capture());
    }

    [Fact]
    public async Task OldSettingsUseLanguageAndStyleDefaultsAndUnsafeTitlesNeverReachDisk()
    {
        using var directory = new SettingsDirectory();
        var device = Guid.NewGuid();
        await File.WriteAllTextAsync(directory.Paths.Settings,
            $$$"""{"Version":1,"Settings":{"DeviceId":"{{{device}}}","Volume":41}}""", Token);
        using var settings = new SettingsStore(directory.Paths, new InlineScheduler());
        Assert.Equal(device, settings.Current.DeviceId);
        Assert.Equal(41, settings.Current.Volume);
        Assert.Equal("auto", settings.Current.PreferredAudioLanguage);
        Assert.Equal("zh", settings.Current.PreferredSubtitleLanguage);
        Assert.Equal(new SubtitleStyleSettings(), settings.Current.SubtitleStyle);
        using var accounts = new AccountContext();
        accounts.Set(new(Secret()));
        var preferences = new TrackPreferences(settings, accounts);
        var fingerprint = TrackSelection.Fingerprint(new("native-id-unique", TrackKind.Subtitle, "字幕")
        { Title = "D:\\private\\subtitle.ass", Language = "jpn" });
        await preferences.SaveAsync(accounts.Current!, new("movie", "影片"), TrackKind.Subtitle, new(false, fingerprint), preferences.Capture(), false, Token);
        var json = await File.ReadAllTextAsync(directory.Paths.Settings, Token);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("native-id-unique", json);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"broken\"")]
    public async Task InvalidOptionalSettingsRepairWithoutDiscardingOtherValues(string broken)
    {
        using var directory = new SettingsDirectory();
        Guid device;
        using (var settings = new SettingsStore(directory.Paths, new InlineScheduler()))
        {
            device = settings.Current.DeviceId;
            await settings.UpdateAsync(value => value with { Volume = 37 }, Token);
        }
        var json = JsonNode.Parse(await File.ReadAllTextAsync(directory.Paths.Settings, Token))!;
        json["Settings"]!["PreferredAudioLanguage"] = "unrecognized";
        json["Settings"]!["PreferredSubtitleLanguage"] = JsonNode.Parse(broken);
        json["Settings"]!["SubtitleStyle"] = new JsonObject { ["FontSize"] = "bad", ["BottomMargin"] = 80, ["TextColor"] = "#123456" };
        json["TrackPreferences"] = new JsonObject { ["bad-key"] = JsonNode.Parse(broken) };
        await File.WriteAllTextAsync(directory.Paths.Settings, json.ToJsonString(), Token);
        using var repaired = new SettingsStore(directory.Paths, new InlineScheduler());
        Assert.Equal(device, repaired.Current.DeviceId);
        Assert.Equal(37, repaired.Current.Volume);
        Assert.Equal("auto", repaired.Current.PreferredAudioLanguage);
        Assert.Equal("zh", repaired.Current.PreferredSubtitleLanguage);
        Assert.Equal(38, repaired.Current.SubtitleStyle.FontSize);
        Assert.Equal(80, repaired.Current.SubtitleStyle.BottomMargin);
        Assert.Equal("#123456", repaired.Current.SubtitleStyle.TextColor);
    }

    [Fact]
    public void FingerprintsDiscardPathAndUnsafeMetadata()
    {
        var track = new TrackInfo("native-id", TrackKind.Subtitle, "日语")
        { Title = "D:\\private\\subtitle.ass", Language = "jpn", Codec = "https://metadata.invalid", AudioChannels = "secret\nvalue" };
        var fingerprint = TrackSelection.Fingerprint(track);
        Assert.Null(fingerprint.Title);
        Assert.Null(fingerprint.Codec);
        Assert.Null(fingerprint.AudioChannels);
        Assert.Equal("ja", fingerprint.Language);
    }

    private static SessionSecret Secret() => new("https://" + Guid.NewGuid().ToString("N") + ".invalid",
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "测试用户", Guid.NewGuid().ToString("N"));
    private sealed class InlineScheduler : IUiScheduler { public bool TryEnqueue(Action action) { action(); return true; } }
    private sealed class SettingsDirectory : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "mambo-tracks-" + Guid.NewGuid().ToString("N")));
        public SettingsDirectory() => Directory.CreateDirectory(Paths.Root);
        public void Dispose() => Directory.Delete(Paths.Root, true);
    }
}
