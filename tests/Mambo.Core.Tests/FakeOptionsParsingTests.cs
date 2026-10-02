using Mambo.Core.Fakes;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class FakeOptionsParsingTests
{
    [Theory]
    [InlineData(null, null, 120, 0)]
    [InlineData("invalid", "NaN", 120, 0)]
    [InlineData("0", "Infinity", 0, 0)]
    [InlineData("-10", "-0.2", 0, 0)]
    [InlineData("20000", "2", 10000, 1)]
    [InlineData("400", "0.5", 400, 0.5)]
    public void EnvironmentInputsUseDiagnosticDefaultsAndBounds(string? delay, string? failure, int milliseconds, double rate)
    {
        var result = FakeOptions.FromEnvironment(delay, failure);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), result.Delay);
        Assert.Equal(rate, result.FailureRate);
        Assert.Equal(new FakeOptions().Seed, result.Seed);
    }
}
