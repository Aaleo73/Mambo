using Mambo.Core.Subtitles;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class SubtitleFilenameParserTests
{
    [Theory]
    [InlineData("Show.Name.S02E03.1080p.WEB-DL.chs.ass", "SHOWNAME", 2, 3)]
    [InlineData("Show_Name.2x03.en.SRT", "SHOWNAME", 2, 3)]
    [InlineData("Show-S02E03-1080p-x265.ass", "SHOW", 2, 3)]
    [InlineData("Chi.S01E02.ass", "CHI", 1, 2)]
    [InlineData("测试剧.第二季 第三集.ass", "测试剧", 2, 3)]
    [InlineData("第2季第03集.srt", "", 2, 3)]
    [InlineData("S00E01.ass", "", 0, 1)]
    [InlineData("Show S12 E1001.srt", "SHOW", 12, 1001)]
    [InlineData("第三季 第八百零一集.ass", "", 3, 801)]
    [InlineData("Ｓ０１Ｅ０２.ass", "", 1, 2)]
    public void ParsesExplicitSeasonAndEpisode(string fileName, string series, int season, int episode)
    {
        var parsed = SubtitleFilenameParser.Parse(fileName);
        Assert.False(parsed.IsAmbiguous);
        Assert.Equal(series, SubtitleFilenameParser.NormalizeTitle(parsed.SeriesName));
        Assert.Equal(season, parsed.SeasonNumber);
        Assert.Equal(episode, parsed.EpisodeNumber);
    }

    [Theory]
    [InlineData("第03集.ass", 3)]
    [InlineData("第三话.srt", 3)]
    [InlineData("03.srt", 3)]
    [InlineData("03.chs.ass", 3)]
    [InlineData("801.ass", 801)]
    [InlineData("1001.chs.ass", 1001)]
    [InlineData("第一千零一集.ass", 1001)]
    public void EpisodeOnlyNeverInventsASeason(string fileName, int episode)
    {
        var parsed = SubtitleFilenameParser.Parse(fileName);
        Assert.False(parsed.IsAmbiguous);
        Assert.Null(parsed.SeasonNumber);
        Assert.Equal(episode, parsed.EpisodeNumber);
    }

    [Theory]
    [InlineData("2024.srt")]
    [InlineData("1080p.ass")]
    [InlineData("1920x1080.ass")]
    [InlineData("a12bc345.srt")]
    [InlineData("[ABCDEF12].ass")]
    [InlineData("S01E02E03.srt")]
    [InlineData("S01E02-E03.srt")]
    [InlineData("S01E02+E03.srt")]
    [InlineData("S01E02 S02.srt")]
    [InlineData("S01E02 S02E03.ass")]
    [InlineData("S01E02 第三集.ass")]
    [InlineData("01-03.ass")]
    [InlineData("S01.ass")]
    [InlineData("第一季.ass")]
    [InlineData("../01.srt")]
    public void RejectsConflictsRangesAndMetadataNumbers(string fileName) => Assert.True(SubtitleFilenameParser.Parse(fileName).IsAmbiguous);

    [Theory]
    [InlineData("字幕.srt")]
    [InlineData("subtitle.srt")]
    [InlineData("简中.ass")]
    public void GenericSingleFileNamesRemainUnnumbered(string fileName)
    {
        var parsed = SubtitleFilenameParser.Parse(fileName);
        Assert.True(parsed.IsUnnumbered);
        Assert.Equal("", parsed.SeriesName);
    }

    [Fact]
    public void UnknownReleaseGroupAndTranslatedNameAreNotFuzzilyRemoved()
    {
        Assert.NotEqual(SubtitleFilenameParser.NormalizeTitle("测试剧"), SubtitleFilenameParser.NormalizeTitle("Test Show"));
        Assert.Equal("GROUPSHOW", SubtitleFilenameParser.NormalizeTitle(SubtitleFilenameParser.Parse("[Group] Show.S01E01.ass").SeriesName));
    }
}
