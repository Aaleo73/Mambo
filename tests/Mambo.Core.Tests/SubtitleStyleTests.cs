using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class SubtitleStyleTests
{
    [Fact]
    public void NormalizeRepairsFieldsIndependently()
    {
        var repaired = SubtitleStyle.Normalize(new() { FontFamily = "../font", FontSize = double.NaN,
            TextColor = "#12345678", OutlineSize = 3, BottomMargin = 181, OverrideAssStyle = true });
        Assert.Equal(new() { OutlineSize = 3, OverrideAssStyle = true }, repaired);
        Assert.Equal(new SubtitleStyleSettings(), SubtitleStyle.Normalize(null));
    }

    [Theory]
    [InlineData(17, 1, 0)]
    [InlineData(73, 1, 0)]
    [InlineData(38, -1, 0)]
    [InlineData(38, 7, 0)]
    [InlineData(38, 1, -1)]
    [InlineData(38, 1, 181)]
    [InlineData(double.NaN, 1, 0)]
    public void ValidateRejectsInvalidRange(double font, double outline, double margin) =>
        Assert.Throws<AppException>(() => SubtitleStyle.Validate(new() { FontSize = font, OutlineSize = outline, BottomMargin = margin }));

    [Fact]
    public void PropertiesUseTextStyleOptionsAndExplicitAssOverride()
    {
        var values = SubtitleStyle.Properties(new());
        Assert.Equal(new MpvValue.Text("MiSans"), values["sub-font"]);
        Assert.Equal(new MpvValue.Number(38), values["sub-font-size"]);
        Assert.Equal(new MpvValue.Text("#FFFFFF"), values["sub-color"]);
        Assert.Equal(new MpvValue.Text("#000000"), values["sub-border-color"]);
        Assert.Equal(new MpvValue.Number(1.65), values["sub-border-size"]);
        Assert.Equal(new MpvValue.Number(34), values["sub-margin-y"]);
        Assert.Equal(new MpvValue.Text("no"), values["sub-ass-override"]);
        Assert.Equal(new MpvValue.Text("force"), SubtitleStyle.Properties(new() { OverrideAssStyle = true })["sub-ass-override"]);
        Assert.DoesNotContain("sub-scale", values.Keys);
        Assert.DoesNotContain("sub-pos", values.Keys);
    }

    [Fact]
    public async Task InvalidStyleCommandPreservesPersistedSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "mambo-style-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        try
        {
            using var settings = new SettingsStore(paths, new InlineScheduler());
            var previous = settings.Current;
            var json = await File.ReadAllTextAsync(paths.Settings, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<AppException>(() => settings.UpdateAsync(value => value with
            { SubtitleStyle = value.SubtitleStyle with { FontSize = 200 }, Volume = 37 }, TestContext.Current.CancellationToken));
            Assert.Equal(previous, settings.Current);
            Assert.Equal(json, await File.ReadAllTextAsync(paths.Settings, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class InlineScheduler : IUiScheduler { public bool TryEnqueue(Action action) { action(); return true; } }

    [Theory]
    [InlineData("auto", false, "")]
    [InlineData("auto", true, "")]
    [InlineData("off", true, "")]
    [InlineData("zh", true, "zh,zho,chi,chs,cht,zh-Hans,zh-Hant,en,eng")]
    [InlineData("ja", false, "ja,jpn")]
    [InlineData("fr", false, "fr,fra,fre")]
    public void LanguageCodesUseMpvLanguageLists(string preference, bool subtitle, string expected) =>
        Assert.Equal(expected, TrackSelection.LanguageCodes(preference, subtitle));
}
