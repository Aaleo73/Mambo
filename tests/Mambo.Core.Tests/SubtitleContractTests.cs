using Mambo.Core.Contracts;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class SubtitleContractTests
{
    [Fact]
    public void DefaultsRetainExistingChinesePreferenceAndTextAppearance()
    {
        var settings = new AppSettings();
        Assert.Equal("auto", settings.PreferredAudioLanguage);
        Assert.Equal("zh", settings.PreferredSubtitleLanguage);
        Assert.Equal("MiSans", settings.SubtitleStyle.FontFamily);
        Assert.Equal(38, settings.SubtitleStyle.FontSize);
        Assert.Equal("#FFFFFF", settings.SubtitleStyle.TextColor);
        Assert.Equal(1.65, settings.SubtitleStyle.OutlineSize);
        Assert.Equal(34, settings.SubtitleStyle.BottomMargin);
        Assert.False(settings.SubtitleStyle.OverrideAssStyle);
    }

    [Fact]
    public void TemporarySourcePathIsRedactedFromDiagnosticText()
    {
        var file = new LocalSubtitleFile("private-subtitle.srt");
        Assert.Equal("private-subtitle.srt", file.Path);
        Assert.DoesNotContain(file.Path, file.ToString());
    }
}
