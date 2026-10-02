using System.Buffers.Binary;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class FakeImageTests
{
    [Fact]
    public async Task GeneratedImagesHaveValidLayoutGradientAndRasterizedTitle()
    {
        var options = new FakeOptions { Delay = TimeSpan.Zero };
        var service = new FakeImageService(new DemoCatalog(), new FakeOperation(options));
        var image = new ImageRef("demo-series-001", ImageKind.Primary, "demo-v1");
        var bytes = (await service.FetchAsync(image, 240, cancellationToken: TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
        Assert.Equal(bytes.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(2)));
        Assert.Equal(240, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)));
        Assert.Equal(360, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(24, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(28)));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10));
        var stride = (240 * 3 + 3) & ~3;
        Assert.Equal(bytes.Length, offset + stride * 360);
        Assert.NotEqual(Pixel(bytes, 240, 360, 0, 0), Pixel(bytes, 240, 360, 239, 359));
        var titlePixels = 0;
        for (var y = 200; y < 310; y++)
            for (var x = 0; x < 240; x++)
                if (Pixel(bytes, 240, 360, x, y) == (250, 246, 242)) titlePixels++;
        Assert.True(titlePixels > 100, "标题应绘制成可解码的图像像素，而不是仅存储在元数据中。");
    }

    [Fact]
    public async Task IdentityAndRequestedSizeDetermineImageRegardlessOfPriorityOrServiceInstance()
    {
        var options = new FakeOptions { Delay = TimeSpan.Zero };
        var first = new FakeImageService(new DemoCatalog(), new FakeOperation(options));
        var second = new FakeImageService(new DemoCatalog(), new FakeOperation(options));
        var image = new ImageRef("demo-movie-0001", ImageKind.Backdrop, "demo-v1");
        var bytes = await first.FetchAsync(image, 321, ImagePriority.Hero, TestContext.Current.CancellationToken);
        var equivalent = await second.FetchAsync(image, 321, ImagePriority.Prefetch, TestContext.Current.CancellationToken);
        var other = await second.FetchAsync(image with { ItemId = "demo-movie-0002" }, 321,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(bytes.ToArray(), equivalent.ToArray());
        Assert.NotEqual(bytes.ToArray(), other.ToArray());
        Assert.Equal(321, BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[18..]));
        Assert.Equal(180, BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[22..]));
    }

    [Fact]
    public async Task CancellationAndConfiguredFailureReturnNoImage()
    {
        var clock = new FakeTimeProvider();
        var options = new FakeOptions { Delay = TimeSpan.FromSeconds(1), FailureRate = 1 };
        var service = new FakeImageService(new DemoCatalog(), new FakeOperation(options, clock));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = service.FetchAsync(new ImageRef("demo-movie-0001", ImageKind.Primary), 160,
            cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var failed = service.FetchAsync(new ImageRef("demo-movie-0001", ImageKind.Primary), 160,
            cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(options.Delay);
        var error = await Assert.ThrowsAsync<AppException>(() => failed);
        Assert.Equal("demo.unavailable", error.Error.Code);
    }

    private static (byte Red, byte Green, byte Blue) Pixel(byte[] pixels, int width, int height, int x, int y)
    {
        var stride = (width * 3 + 3) & ~3;
        var offset = 54 + (height - 1 - y) * stride + x * 3;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
