using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Mambo.App.Images;
using Mambo.App.Shell;
using Mambo.App.Themes;
using Mambo.App.ViewModels;
using Mambo.App.Views.Controls;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    private static async Task RunImageResolutionOnlyAsync(MainWindow window, string reportPath, CancellationToken token)
    {
        var report = new MotionReport();
        try
        {
            await WaitAsync(() => window.Shell.IsLoaded && window.Shell.ActualWidth > 0, token);
            await RunImageResolutionAsync(window, report, token);
            report.Passed = true;
            report.Stage = "Completed";
        }
        catch (Exception error) { report.FailureKind = error.GetType().Name; }
        finally
        {
            var path = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(report, UiLabJsonContext.Default.MotionReport), CancellationToken.None);
            await window.CloseForSmokeAsync();
            window.Close();
        }
    }

    private static async Task RunImageResolutionAsync(MainWindow window, MotionReport report, CancellationToken token)
    {
        report.Stage = "ImageResolution";
        var services = window.Services;
        services.GetRequiredService<Navigator>().Navigate(Route.Settings);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        var library = services.GetRequiredService<ILibraryService>();
        using var libraries = library.ObserveLibraries(token);
        await libraries.RefreshAsync(token);
        using var items = library.ObserveLibrary(libraries.Current.First(item => item.Kind == LibraryKind.Movies).Id, new(), scopeToken: token);
        await items.RefreshAsync(token);
        using var images = new MotionImageScope(new FullSizeDiagnosticImages(services.GetRequiredService<IImageService>()));
        var image = new ImageRef(items.Items[0].Id, ImageKind.Primary, "image-resolution-scale");
        var normal = await images.Loader.LoadAsync(image, 150, 1, ImagePriority.Visible, token);
        var medium = await images.Loader.LoadAsync(image, 150, 1.5, ImagePriority.Visible, token);
        var high = await images.Loader.LoadAsync(image, 150, 2, ImagePriority.Visible, token);
        report.Measurements["ImageDecodePixelsAt100Percent"] = normal?.DecodePixelWidth ?? 0;
        report.Measurements["ImageDecodePixelsAt150Percent"] = medium?.DecodePixelWidth ?? 0;
        report.Measurements["ImageDecodePixelsAt200Percent"] = high?.DecodePixelWidth ?? 0;
        MotionCheck(report, "ImageCacheSeparatesPhysicalResolutions", normal is not null && medium is not null && high is not null &&
            !ReferenceEquals(normal, medium) && !ReferenceEquals(medium, high));
        MotionCheck(report, "ImageDecodingMatchesPhysicalPixels", normal!.DecodePixelWidth == 150 && medium!.DecodePixelWidth == 225 && high!.DecodePixelWidth == 300 &&
            normal.DecodePixelType == DecodePixelType.Physical && medium.DecodePixelType == DecodePixelType.Physical && high.DecodePixelType == DecodePixelType.Physical);
        var equivalent = await images.Loader.LoadAsync(image, 200, 1.5, ImagePriority.Visible, token);
        MotionCheck(report, "EquivalentPhysicalResolutionReusesImage", ReferenceEquals(high, equivalent));

        var pattern = image with { Tag = "image-resolution-stripes" };
        var lowPattern = await images.Loader.LoadAsync(pattern, 150, 1, ImagePriority.Visible, token);
        var highPattern = await images.Loader.LoadAsync(pattern, 150, 2, ImagePriority.Visible, token);
        var well = window.Shell.FindName("WellContent").As<Grid>();
        var lowContrast = await MeasureImageContrastAsync(well, lowPattern!, token);
        var highContrast = await MeasureImageContrastAsync(well, highPattern!, token);
        report.Measurements["UpscaledImageContrast"] = lowContrast;
        report.Measurements["PhysicalSizeImageContrast"] = highContrast;
        MotionCheck(report, "PhysicalResolutionPreservesRenderedDetail", highContrast > lowContrast * 1.2);

        var adaptiveImage = image with { Tag = "image-resolution-adaptive" };
        var replacementImage = new ImageRef(items.Items[1].Id, ImageKind.Primary, "image-resolution-replacement");
        var poster = new PosterCard
        {
            IsAdaptive = true,
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Item = new MediaCardViewModel(items.Items[0] with { Images = [adaptiveImage] }, CardContext.Library, false),
        };
        var mount = new Grid { IsHitTestVisible = false };
        mount.Children.Add(poster);
        Motion.SetEntranceSuppressed(mount, true);
        well.Children.Add(mount);
        try
        {
            var picture = poster.FindName("Picture").As<RemoteImage>();
            await MotionUntilAsync(() => picture.CurrentImage is not null && !picture.IsLoading, token);
            var original = picture.CurrentImage;
            var pending = 0;
            picture.Pending += (_, _) => pending++;
            images.Source.Hold(adaptiveImage);
            poster.Width = 240;
            await MotionUntilAsync(() => picture.IsLoading, token);
            MotionCheck(report, "ResizingKeepsImageAndCaptionVisible", ReferenceEquals(original, picture.CurrentImage) && pending == 0 &&
                poster.FindName("Copy").As<FrameworkElement>().Opacity == 1);
            images.Source.Release(adaptiveImage);
            await MotionUntilAsync(() => !picture.IsLoading && !ReferenceEquals(original, picture.CurrentImage), token);
            var enlarged = picture.CurrentImage.As<BitmapImage>();
            report.Measurements["AdaptiveImageDecodePixels"] = enlarged.DecodePixelWidth;
            report.Measurements["AdaptiveImageDisplayPixels"] = Math.Ceiling(picture.ActualWidth * picture.XamlRoot.RasterizationScale);
            MotionCheck(report, "AdaptiveCardUpgradesToDisplayResolution", enlarged.DecodePixelWidth >= Math.Ceiling(picture.ActualWidth * picture.XamlRoot.RasterizationScale) &&
                !picture.IsRevealing && pending == 0);

            var fetches = images.Loader.FetchStartedCount;
            poster.Width = 150;
            await AwaitNextRenderingAsync(token);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "ShrinkingCardKeepsSufficientImage", ReferenceEquals(enlarged, picture.CurrentImage) && images.Loader.FetchStartedCount == fetches);

            images.Source.Hold(adaptiveImage);
            poster.Width = 300;
            await MotionUntilAsync(() => picture.IsLoading, token);
            poster.Item = new MediaCardViewModel(items.Items[1] with { Images = [replacementImage] }, CardContext.Library, false);
            await MotionUntilAsync(() => !picture.IsLoading && picture.CurrentImage is not null && Equals(picture.Source, replacementImage), token);
            var replacement = picture.CurrentImage;
            images.Source.Release(adaptiveImage);
            await AwaitNextRenderingAsync(token);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "CancelledResolutionUpgradeCannotReplaceNewCard", ReferenceEquals(replacement, picture.CurrentImage));

            images.Source.Fail(replacementImage);
            var beforeFailedFetch = images.Loader.FetchStartedCount;
            poster.Width = 360;
            await MotionUntilAsync(() => picture.ActualWidth >= 360 && images.Loader.FetchStartedCount > beforeFailedFetch && !picture.IsLoading, token);
            MotionCheck(report, "FailedResolutionUpgradeKeepsExistingImage", ReferenceEquals(replacement, picture.CurrentImage) &&
                poster.FindName("Copy").As<FrameworkElement>().Opacity == 1);

            well.Children.Remove(mount);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "ImageResolutionUnloadReleasesBitmap", picture.CurrentImage is null && !picture.IsLoading);
        }
        finally
        {
            Motion.SetActive(mount, false);
            well.Children.Remove(mount);
        }
    }

    private static async Task<double> MeasureImageContrastAsync(Grid parent, BitmapImage source, CancellationToken token)
    {
        // 在相同的 300×300 物理像素区域渲染，直接读取细条纹的对比度，避免仅检查 DecodePixelWidth 属性。
        var view = new Image { Source = source, Width = 300 / parent.XamlRoot.RasterizationScale,
            Height = 300 / parent.XamlRoot.RasterizationScale, Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        parent.Children.Add(view);
        try
        {
            await AwaitNextRenderingAsync(token);
            var frame = new RenderTargetBitmap();
            await frame.RenderAsync(view, 300, 300).AsTask(token);
            var pixels = (await frame.GetPixelsAsync().AsTask(token)).ToArray();
            var values = Enumerable.Range(10, frame.PixelWidth - 20)
                .Select(x => (double)pixels[(frame.PixelHeight / 2 * frame.PixelWidth + x) * 4]).ToArray();
            var mean = values.Average();
            return Math.Sqrt(values.Average(value => (value - mean) * (value - mean)));
        }
        finally { parent.Children.Remove(view); }
    }

    // 模拟忽略 MaxWidth、始终返回大图的服务器，避免假图片尺寸恰好等于请求尺寸而掩盖解码问题。
    private sealed class FullSizeDiagnosticImages(IImageService inner) : IImageService
    {
        private readonly Lazy<byte[]> pattern = new(CreateStripePattern);
        public Task<ReadOnlyMemory<byte>> FetchAsync(ImageRef image, int pixelWidth, ImagePriority priority = ImagePriority.Visible,
            CancellationToken cancellationToken = default) => image.Tag == "image-resolution-stripes"
                ? Task.FromResult<ReadOnlyMemory<byte>>(pattern.Value) : inner.FetchAsync(image, 960, priority, cancellationToken);

        private static byte[] CreateStripePattern()
        {
            const int size = 960;
            var bytes = new byte[54 + size * size * 3];
            bytes[0] = (byte)'B';
            bytes[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), size);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), size);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    bytes.AsSpan(54 + (y * size + x) * 3, 3).Fill((byte)(x / 8 % 2 == 0 ? 0 : 255));
            return bytes;
        }
    }
}
