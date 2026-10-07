using Mambo.App.Video;
using Xunit;

namespace Mambo.Core.Tests;

/// <summary>圆角黑框里视频矩形的摆法：矩形整块落在圆角以内，而且不把影片压小。</summary>
public sealed class VideoFrameFitTests
{
    [Fact]
    public void WideFilmGivesUpOnlyItsTopAndBottomBars()
    {
        var frame = VideoFrameFit.Compute(1600, 1000, 18, 2.39);
        Assert.Equal(new VideoFrameFit.Frame(0, 18, 1600, 964, 18), frame);
    }

    [Fact]
    public void NarrowVideoGivesUpOnlyItsSideBars()
    {
        var frame = VideoFrameFit.Compute(1600, 900, 18, 4.0 / 3);
        Assert.Equal(new VideoFrameFit.Frame(18, 0, 1564, 900, 18), frame);
    }

    [Fact]
    public void VideoThatFillsTheFrameKeepsSquareCorners()
    {
        var frame = VideoFrameFit.Compute(1600, 900, 18, 16.0 / 9);
        Assert.Equal(new VideoFrameFit.Frame(0, 0, 1600, 900, 0), frame);
    }

    [Fact]
    public void ThinBarsLimitTheRadiusInsteadOfShrinkingTheVideo()
    {
        // 16:9 的影片在 1600×912 的黑框里上下各有 6 像素黑边。
        var frame = VideoFrameFit.Compute(1600, 912, 18, 16.0 / 9);
        Assert.Equal(new VideoFrameFit.Frame(0, 5, 1600, 902, 5), frame);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void EpisodeLayoutsShareThinBarRadiusAtEveryScale(double scale)
    {
        var expandedWidth = (int)Math.Round(1404 * scale);
        var collapsedWidth = (int)Math.Round(1600 * scale);
        var height = (int)Math.Round(912 * scale);
        var maximum = (int)Math.Floor(12 * scale);
        var radius = VideoFrameFit.ComputeSharedRadius(expandedWidth, collapsedWidth, height, maximum, 16.0 / 9);
        // 收起时上下只有 6 DIP 黑边；展开时黑边足够，但两者也必须使用相同的小圆角。
        Assert.InRange(radius, 1, maximum - 1);
        Assert.Equal(VideoFrameFit.Compute(collapsedWidth, height, maximum, 16.0 / 9).Radius, radius);
        foreach (var width in new[] { expandedWidth, collapsedWidth })
        {
            var frame = VideoFrameFit.Compute(width, height, radius, 16.0 / 9);
            Assert.Equal(radius, frame.Radius);
            AssertNaturalSizeFits(width, height, 16.0 / 9, frame);
        }
    }

    [Theory]
    [InlineData(1600, 1796, 912)] // 展开时黑边较薄，收起后左右黑边充足。
    [InlineData(1404, 1600, 912)] // 收起时黑边较薄。
    [InlineData(1600, 1796, 900)] // 展开时恰好铺满。
    [InlineData(1404, 1600, 900)] // 收起时恰好铺满。
    public void SharedRadiusDoesNotDependOnWhichLayoutIsActive(int expandedWidth, int collapsedWidth, int height)
    {
        var radius = VideoFrameFit.ComputeSharedRadius(expandedWidth, collapsedWidth, height, 12, 16.0 / 9);
        Assert.Equal(height == 900 ? 0 : 5, radius);
        Assert.Equal(radius, VideoFrameFit.ComputeSharedRadius(collapsedWidth, expandedWidth, height, 12, 16.0 / 9));
        foreach (var width in new[] { expandedWidth, collapsedWidth })
        {
            var frame = VideoFrameFit.Compute(width, height, radius, 16.0 / 9);
            Assert.Equal(radius, frame.Radius);
            AssertNaturalSizeFits(width, height, 16.0 / 9, frame);
        }
    }

    [Fact]
    public void BothLayoutsKeepMaximumRadiusWhenBarsAreSufficient()
    {
        // 真实诊断窗口：展开为上下黑边，收起为左右黑边。
        Assert.Equal(18, VideoFrameFit.ComputeSharedRadius(1898, 2192, 1183, 18, 16.0 / 9));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void RadiusFollowsTheDisplayScale(double scale)
    {
        var width = (int)Math.Round(1280 * scale);
        var height = (int)Math.Round(800 * scale);
        var radius = (int)Math.Floor(12 * scale);
        var frame = VideoFrameFit.Compute(width, height, radius, 16.0 / 9);
        Assert.Equal(new VideoFrameFit.Frame(0, radius, width, height - 2 * radius, radius), frame);
    }

    [Theory]
    [InlineData(0, 1.5)]
    [InlineData(18, 0)]
    [InlineData(18, -1)]
    [InlineData(18, double.NaN)]
    [InlineData(18, double.PositiveInfinity)]
    public void WithoutRadiusOrAspectTheVideoFillsTheFrame(int radius, double aspect)
    {
        Assert.Equal(new VideoFrameFit.Frame(0, 0, 1600, 900, 0), VideoFrameFit.Compute(1600, 900, radius, aspect));
    }

    [Fact]
    public void EmptyHostHasNoFrame()
    {
        Assert.Equal(default, VideoFrameFit.Compute(0, 900, 18, 1.5));
        Assert.Equal(default, VideoFrameFit.Compute(1600, 0, 18, 1.5));
    }

    [Fact]
    public void FrameStaysInsideTheRoundedCornersAndNeverShrinksTheVideo()
    {
        int[] widths = [640, 1100, 1476, 1600, 1921, 2560, 3440];
        int[] heights = [360, 720, 796, 900, 1081, 1440];
        int[] radii = [12, 15, 18, 21, 24];
        double[] aspects = [1, 4.0 / 3, 1.5, 16.0 / 9, 1.85, 2, 2.39, 9.0 / 16];
        foreach (var width in widths)
            foreach (var height in heights)
                foreach (var radius in radii)
                    foreach (var aspect in aspects.Append((double)width / height).Append((width - 9.0) / height).Append(width / (height - 9.0)))
                    {
                        var frame = VideoFrameFit.Compute(width, height, radius, aspect);
                        // 居中，且不超出黑框。
                        Assert.Equal(width, frame.X * 2 + frame.Width);
                        Assert.Equal(height, frame.Y * 2 + frame.Height);
                        Assert.InRange(frame.Radius, 0, radius);
                        // 只在一个方向让开，让开的量正好是圆角：角点落在圆弧与直边相接处，不会伸到圆角外。
                        Assert.True((frame.X == 0 && frame.Y == frame.Radius) || (frame.Y == 0 && frame.X == frame.Radius));
                        foreach (var x in new[] { frame.X, frame.X + frame.Width })
                            foreach (var y in new[] { frame.Y, frame.Y + frame.Height })
                            {
                                var dx = x - Math.Clamp(x, frame.Radius, width - frame.Radius);
                                var dy = y - Math.Clamp(y, frame.Radius, height - frame.Radius);
                                Assert.True((double)dx * dx + (double)dy * dy <= (double)frame.Radius * frame.Radius);
                            }
                        // 影片按原来的大小放得下。
                        var videoWidth = Math.Min(width, height * aspect);
                        var videoHeight = Math.Min(height, width / aspect);
                        AssertNaturalSizeFits(width, height, aspect, frame);
                        // 黑边够宽时用满圆角。
                        var bar = Math.Max(width - videoWidth, height - videoHeight) / 2;
                        if (bar >= radius + 0.5) Assert.Equal(radius, frame.Radius);
                    }
    }

    private static void AssertNaturalSizeFits(int width, int height, double aspect, VideoFrameFit.Frame frame) =>
        Assert.True(Math.Min(width, height * aspect) <= frame.Width + 1e-9
            && Math.Min(height, width / aspect) <= frame.Height + 1e-9);
}
