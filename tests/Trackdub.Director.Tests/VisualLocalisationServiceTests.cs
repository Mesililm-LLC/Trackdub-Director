using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class VisualLocalisationServiceTests
{
    private sealed class FakeImageClient : IMuseImageClient
    {
        private readonly Func<RasterImage, MuseImageEditResult> _edit;

        public FakeImageClient(Func<RasterImage, MuseImageEditResult> edit) => _edit = edit;

        public int Calls { get; private set; }

        public string? LastInstruction { get; private set; }

        public (int Width, int Height)? LastCropSize { get; private set; }

        public Task<MuseImageEditResult> EditAsync(RasterImage image, string instruction, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastInstruction = instruction;
            LastCropSize = (image.Width, image.Height);
            return Task.FromResult(_edit(image));
        }
    }

    private static RasterImage Solid(int width, int height, byte r, byte g, byte b)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }

        return RasterImage.Create(width, height, rgb);
    }

    private static (byte R, byte G, byte B) Pixel(RasterImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 3;
        return (image.Rgb[i], image.Rgb[i + 1], image.Rgb[i + 2]);
    }

    [Fact]
    public async Task Composite_places_edit_at_exact_box_with_outside_pixels_identical()
    {
        // Model returns a 2x solid-magenta raster regardless of crop size.
        var fake = new FakeImageClient(crop => new MuseImageEditResult(
            Solid(crop.Width * 2, crop.Height * 2, 255, 0, 255),
            "muse-image-1.0",
            new MetaClient.TokenUsage(100, 20, 120),
            "resp_test"));
        var service = new VisualLocalisationService(fake);
        RasterImage frame = Solid(100, 80, 10, 20, 30);
        var box = new LocalisationBox(30, 20, 60, 50);

        VisualLocalisationSegmentResult result = await service.LocaliseRasterAsync(
            "seg-1", frame, box, "COFFEE SHOP", "CAFETERÍA");

        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, result.ImageCalls);
        // Crop sent to the model is the box plus 20px padding: (10,0)-(80,70).
        Assert.Equal((70, 70), fake.LastCropSize);
        Assert.Equal(
            "replace the text \"COFFEE SHOP\" on the shop door sign with \"CAFETERÍA\", preserve everything else in the image exactly",
            fake.LastInstruction);
        Assert.Equal(VisualLocalisationStatus.Localised, result.Status);
        Assert.Equal(0, result.OutsideBoxDiff);
        Assert.NotNull(result.LocalisedPngBytes);

        RasterImage localised = WicImageDecoder.Decode(result.LocalisedPngBytes!);
        Assert.Equal((100, 80), (localised.Width, localised.Height));
        // Box interior (including corners) carries the edit; outside keeps source pixels.
        Assert.Equal(((byte)255, (byte)0, (byte)255), Pixel(localised, 30, 20));
        Assert.Equal(((byte)255, (byte)0, (byte)255), Pixel(localised, 59, 49));
        Assert.Equal(((byte)255, (byte)0, (byte)255), Pixel(localised, 45, 35));
        Assert.Equal(((byte)10, (byte)20, (byte)30), Pixel(localised, 29, 20));
        Assert.Equal(((byte)10, (byte)20, (byte)30), Pixel(localised, 60, 49));
        Assert.Equal(((byte)10, (byte)20, (byte)30), Pixel(localised, 0, 0));
        Assert.Equal(((byte)10, (byte)20, (byte)30), Pixel(localised, 99, 79));
        // Byte-level: every outside-box pixel equals the source raster.
        Assert.Equal(0, localised.CountDiffOutsideBox(frame, box.X1, box.Y1, box.X2, box.Y2));
    }

    [Fact]
    public async Task NoImage_response_fails_closed_without_localised_frame()
    {
        var fake = new FakeImageClient(_ => throw new MetaApiException("Meta Model API response carried no image (missing image_generation_call result)."));
        var service = new VisualLocalisationService(fake);

        VisualLocalisationSegmentResult result = await service.LocaliseRasterAsync(
            "seg-1", Solid(100, 80, 1, 2, 3), new LocalisationBox(10, 10, 40, 40), "COFFEE SHOP", "CAFETERÍA");

        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, result.ImageCalls);
        Assert.Equal(VisualLocalisationStatus.Failed, result.Status);
        Assert.Null(result.LocalisedImageDataUrl);
        Assert.Null(result.LocalisedPngBytes);
        Assert.Contains("no image", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Undersized_edit_fails_closed()
    {
        var fake = new FakeImageClient(_ => new MuseImageEditResult(
            Solid(10, 10, 9, 9, 9), "muse-image-1.0", new MetaClient.TokenUsage(1, 1, 2), "resp_test"));
        var service = new VisualLocalisationService(fake);

        VisualLocalisationSegmentResult result = await service.LocaliseRasterAsync(
            "seg-1", Solid(100, 80, 1, 2, 3), new LocalisationBox(10, 10, 40, 40), "COFFEE SHOP", "CAFETERÍA");

        Assert.Equal(1, fake.Calls);
        Assert.Equal(VisualLocalisationStatus.Failed, result.Status);
        Assert.Null(result.LocalisedImageDataUrl);
        Assert.Contains("smaller than the target box", result.Error ?? string.Empty);
    }

    [Theory]
    [InlineData(-1, 10, 40, 40)]
    [InlineData(10, 10, 10, 40)]
    [InlineData(10, 10, 40, 999)]
    [InlineData(90, 70, 120, 90)]
    public async Task Invalid_box_is_rejected_before_any_call(int x1, int y1, int x2, int y2)
    {
        var fake = new FakeImageClient(crop => new MuseImageEditResult(crop, "muse-image-1.0", null, null));
        var service = new VisualLocalisationService(fake);

        VisualLocalisationSegmentResult result = await service.LocaliseRasterAsync(
            "seg-1", Solid(100, 80, 1, 2, 3), new LocalisationBox(x1, y1, x2, y2), "COFFEE SHOP", "CAFETERÍA");

        Assert.Equal(0, fake.Calls);
        Assert.Equal(0, result.ImageCalls);
        Assert.Equal(VisualLocalisationStatus.Failed, result.Status);
        Assert.Contains("does not lie inside", result.Error ?? string.Empty);
    }

    [Fact]
    public void PngCodec_output_decodes_back_to_the_same_pixels()
    {
        RasterImage source = Solid(17, 9, 200, 100, 50);
        byte[] png = PngCodec.Encode(source);
        RasterImage decoded = WicImageDecoder.Decode(png);
        Assert.Equal((17, 9), (decoded.Width, decoded.Height));
        Assert.Equal(source.Rgb, decoded.Rgb);
    }
}
