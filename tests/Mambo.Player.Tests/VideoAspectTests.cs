using Mambo.Player.LibMpv;
using Xunit;

namespace Mambo.Player.Tests;

public sealed class VideoAspectTests
{
    [Theory]
    [InlineData(1920, 1080, 0, 16.0 / 9)]
    [InlineData(1920, 1080, 90, 9.0 / 16)]
    [InlineData(1920, 1080, 180, 16.0 / 9)]
    [InlineData(1920, 1080, 270, 9.0 / 16)]
    [InlineData(1920, 1080, -90, 9.0 / 16)]
    [InlineData(1920, 1080, 450, 9.0 / 16)]
    [InlineData(1080, 1920, 90, 16.0 / 9)]
    [InlineData(1440, 1080, 0, 4.0 / 3)]
    public void UsesDisplayDimensionsAndRotation(long width, long height, long rotation, double expected)
    {
        var parameters = new MpvValue.Map(new Dictionary<string, MpvValue?>
        {
            ["dw"] = new MpvValue.WholeNumber(width), ["dh"] = new MpvValue.WholeNumber(height),
            ["rotate"] = new MpvValue.WholeNumber(rotation),
            // 存储像素比例可能不同，不能用 w/h 覆盖显示尺寸。
            ["w"] = new MpvValue.WholeNumber(720), ["h"] = new MpvValue.WholeNumber(576),
        });
        Assert.Equal(expected, LibMpvEngine.ParseVideoAspect(parameters)!.Value, 12);
    }

    [Fact]
    public void NumericDimensionsWithoutRotationAreAccepted()
    {
        Assert.Equal(2.39, LibMpvEngine.ParseVideoAspect(Parameters(new MpvValue.Number(2390), new MpvValue.Number(1000)))!.Value, 12);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(-1, 1080)]
    [InlineData(1920, 0)]
    [InlineData(1920, -1)]
    [InlineData(double.NaN, 1080)]
    [InlineData(1920, double.PositiveInfinity)]
    [InlineData(double.MaxValue, double.Epsilon)]
    [InlineData(double.Epsilon, double.MaxValue)]
    public void InvalidDimensionsHaveNoAspect(double width, double height)
    {
        Assert.Null(LibMpvEngine.ParseVideoAspect(Parameters(new MpvValue.Number(width), new MpvValue.Number(height))));
    }

    [Fact]
    public void MissingOrWrongTypesHaveNoAspect()
    {
        Assert.Null(LibMpvEngine.ParseVideoAspect(null));
        Assert.Null(LibMpvEngine.ParseVideoAspect(new MpvValue.Text("unavailable")));
        Assert.Null(LibMpvEngine.ParseVideoAspect(new MpvValue.Map(new Dictionary<string, MpvValue?>())));
        Assert.Null(LibMpvEngine.ParseVideoAspect(Parameters(null, new MpvValue.WholeNumber(1080))));
        Assert.Null(LibMpvEngine.ParseVideoAspect(Parameters(new MpvValue.WholeNumber(1920), null)));
        Assert.Null(LibMpvEngine.ParseVideoAspect(Parameters(new MpvValue.Text("1920"), new MpvValue.WholeNumber(1080))));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidRotationHasNoAspect(double rotation)
    {
        var parameters = new MpvValue.Map(new Dictionary<string, MpvValue?>
        {
            ["dw"] = new MpvValue.WholeNumber(1920), ["dh"] = new MpvValue.WholeNumber(1080),
            ["rotate"] = new MpvValue.Number(rotation),
        });
        Assert.Null(LibMpvEngine.ParseVideoAspect(parameters));
    }

    private static MpvValue.Map Parameters(MpvValue? width, MpvValue? height) =>
        new(new Dictionary<string, MpvValue?> { ["dw"] = width, ["dh"] = height });
}
