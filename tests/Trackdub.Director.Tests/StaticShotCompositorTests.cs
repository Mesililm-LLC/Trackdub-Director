using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class StaticShotCompositorTests
{
    private const int W = 64;
    private const int H = 48;
    private static readonly LocalisationBox Box = new(16, 8, 48, 40);

    /// <summary>Textured frame so SSIM has structure to compare (flat frames make every block trivially similar).</summary>
    private static RasterImage Textured(int seed = 0)
    {
        byte[] rgb = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 3;
                rgb[i] = (byte)((x * 7 + y * 13 + seed) % 256);
                rgb[i + 1] = (byte)((x * 11 + y * 3 + seed) % 256);
                rgb[i + 2] = (byte)((x * 5 + y * 17 + seed) % 256);
            }
        }

        return RasterImage.Create(W, H, rgb);
    }

    /// <summary>Adds deterministic ±2 per-channel noise, roughly what video compression leaves on a static shot.</summary>
    private static RasterImage WithNoise(RasterImage source, int seed)
    {
        const int amplitude = 2;
        var random = new Random(seed);
        byte[] rgb = (byte[])source.Rgb.Clone();
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)Math.Clamp(rgb[i] + random.Next(-amplitude, amplitude + 1), 0, 255);
        }

        return RasterImage.Create(W, H, rgb);
    }

    private static RasterImage FillRect(RasterImage source, LocalisationBox rect, byte value)
    {
        byte[] rgb = (byte[])source.Rgb.Clone();
        for (int y = rect.Y1; y < rect.Y2; y++)
        {
            for (int x = rect.X1; x < rect.X2; x++)
            {
                int i = (y * W + x) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = value;
            }
        }

        return RasterImage.Create(W, H, rgb);
    }

    private static RasterImage Localised(RasterImage reference) => FillRect(reference, Box, 200);

    private static StaticShotCompositor Create(RasterImage reference)
    {
        StaticShotCompositor.Creation creation = StaticShotCompositor.Create(reference, Localised(reference), Box, StaticShotCompositor.DefaultMinSsim);
        Assert.Null(creation.Error);
        return Assert.IsType<StaticShotCompositor>(creation.Compositor);
    }

    private static string RejectionOf(RasterImage reference, RasterImage localised, LocalisationBox box, double minSsim)
    {
        StaticShotCompositor.Creation creation = StaticShotCompositor.Create(reference, localised, box, minSsim);
        Assert.Null(creation.Compositor);
        return Assert.IsType<string>(creation.Error);
    }

    [Fact]
    public void IdenticalFrameScoresOne()
    {
        RasterImage reference = Textured();

        Assert.Equal(1.0, StaticShotCompositor.ComputeBoxSsim(reference, reference, Box).Mean, 6);
    }

    [Fact]
    public void SensorNoiseStillPassesTheGate()
    {
        RasterImage reference = Textured();
        StaticShotCompositor compositor = Create(reference);

        BoxSsim ssim = compositor.MeasureSsim(WithNoise(reference, seed: 1));

        Assert.True(compositor.Passes(ssim), $"SSIM {ssim} should pass under light noise.");
    }

    [Fact]
    public void SomethingCrossingTheSignFailsTheGate()
    {
        RasterImage reference = Textured();
        StaticShotCompositor compositor = Create(reference);

        BoxSsim ssim = compositor.MeasureSsim(FillRect(reference, new LocalisationBox(16, 8, 32, 40), 30));

        Assert.False(compositor.Passes(ssim), $"SSIM {ssim} should fail with half the box occluded.");
    }

    [Fact]
    public void ASmallOcclusionTheMeanAveragesAwayStillFailsTheGate()
    {
        RasterImage reference = Textured();
        var fullFrame = new LocalisationBox(0, 0, W, H);
        StaticShotCompositor compositor = StaticShotCompositor.Create(reference, FillRect(reference, fullFrame, 200), fullFrame, 0.95).Compositor!;

        BoxSsim ssim = compositor.MeasureSsim(FillRect(reference, new LocalisationBox(0, 0, 8, 8), 255));

        Assert.True(ssim.Mean >= 0.95, $"Mean SSIM {ssim.Mean} should hide one occluded block of 48.");
        Assert.True(ssim.MaxBlockLumaShift > StaticShotCompositor.MaxBlockLumaShift);
        Assert.False(compositor.Passes(ssim));
    }

    [Fact]
    public void AnOcclusionInTheTrailingPartialStripIsMeasured()
    {
        // 34 px wide: four full blocks plus a 2 px strip at x 32-33 that the composite also overwrites.
        RasterImage reference = Textured();
        var box = new LocalisationBox(0, 0, 34, 16);
        StaticShotCompositor compositor = StaticShotCompositor.Create(reference, FillRect(reference, box, 200), box, 0.95).Compositor!;

        BoxSsim ssim = compositor.MeasureSsim(FillRect(reference, new LocalisationBox(32, 0, 34, 16), 255));

        Assert.False(compositor.Passes(ssim), $"Strip occlusion must fail; shift {ssim.MaxBlockLumaShift}.");
    }

    [Fact]
    public void ComputeBoxSsimRejectsMismatchedImagesAndOutOfFrameBoxes()
    {
        RasterImage reference = Textured();
        RasterImage small = RasterImage.Create(32, 32, new byte[32 * 32 * 3]);

        Assert.Throws<ArgumentException>(() => StaticShotCompositor.ComputeBoxSsim(reference, small, Box));
        Assert.Throws<ArgumentException>(() => StaticShotCompositor.ComputeBoxSsim(reference, reference, new LocalisationBox(40, 8, 72, 40)));
    }

    [Fact]
    public void WindowIncludesItsStartAndExcludesItsEnd()
    {
        var window = new StaticShotWindow(2.36, 12.76);

        Assert.True(window.Contains(window.StartSeconds));
        Assert.False(window.Contains(window.EndSeconds));
        Assert.False(window.Contains(2.32));
    }

    [Fact]
    public void ACutToADifferentShotFailsTheGate()
    {
        RasterImage reference = Textured();
        StaticShotCompositor compositor = Create(reference);

        Assert.False(compositor.Passes(compositor.MeasureSsim(Textured(seed: 97))));
    }

    [Fact]
    public void FirstFailureReturnsTheEarliestFailingFrame()
    {
        StaticShotFrame[] frames =
        [
            new(0, 0.00, 0.99, 4.0, true),
            new(1, 0.04, 0.40, 90.0, false),
            new(2, 0.08, 0.30, 95.0, false),
        ];

        Assert.Equal(1, StaticShotCompositor.FirstFailure(frames)!.FrameIndex);
        Assert.Null(StaticShotCompositor.FirstFailure(frames.Take(1)));
    }

    [Fact]
    public void CompositeTakesTheBoxFromTheLocalisedFrameAndEverythingElseFromTheFrame()
    {
        RasterImage reference = Textured();
        StaticShotCompositor compositor = Create(reference);
        RasterImage frame = WithNoise(reference, seed: 5);

        RasterImage output = compositor.Composite(frame, out int outsideDiff);

        Assert.Equal(0, outsideDiff);
        Assert.Equal(0, output.CountDiffOutsideBox(frame, Box.X1, Box.Y1, Box.X2, Box.Y2));
        RasterImage boxPixels = output.Crop(Box.X1, Box.Y1, Box.X2 - Box.X1, Box.Y2 - Box.Y1);
        Assert.All(boxPixels.Rgb, b => Assert.Equal(200, b));
    }

    [Fact]
    public void RejectsALocalisedFrameThatWasNotMadeFromTheReference()
    {
        RasterImage reference = Textured();
        RasterImage foreign = Localised(Textured(seed: 3));

        Assert.Contains("outside the box", RejectionOf(reference, foreign, Box, 0.95));
    }

    [Fact]
    public void RejectsALocalisedFrameWithNothingChangedInTheBox()
    {
        RasterImage reference = Textured();

        Assert.Contains("identical", RejectionOf(reference, reference, Box, 0.95));
    }

    public static TheoryData<LocalisationBox> InvalidBoxes => new()
    {
        new LocalisationBox(-1, 8, 48, 40),
        new LocalisationBox(16, 8, 65, 40),
        new LocalisationBox(16, 8, 20, 40),
    };

    [Theory]
    [MemberData(nameof(InvalidBoxes))]
    public void RejectsBoxesOutsideTheFrameOrSmallerThanOneBlock(LocalisationBox box)
    {
        RasterImage reference = Textured();

        Assert.Contains("Box", RejectionOf(reference, Localised(reference), box, 0.95));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void RejectsThresholdsOutsideTheUnitInterval(double minSsim)
    {
        RasterImage reference = Textured();

        Assert.Contains("SSIM", RejectionOf(reference, Localised(reference), Box, minSsim));
    }

    [Fact]
    public void RejectsMismatchedFrameSizes()
    {
        RasterImage reference = Textured();
        RasterImage small = RasterImage.Create(32, 32, new byte[32 * 32 * 3]);

        Assert.Contains("32x32", RejectionOf(reference, small, Box, 0.95));
        Assert.Throws<ArgumentException>(() => Create(reference).MeasureSsim(small));
    }
}
