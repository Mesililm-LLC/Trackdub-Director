namespace Trackdub.Director.Core;

/// <summary>
/// Video-rate sign localisation from a single Muse Image repaint. The repaint
/// was made on one reference frame; it can stand in for every frame of a shot
/// only if the box region stays the same across that shot. This class is the
/// gate for that assumption, and it checks only what it measures: each frame's
/// box region must keep its mean luma SSIM against the reference at or above
/// the threshold, and no 8x8 block may shift its mean brightness by more than
/// <see cref="MaxBlockLumaShift"/>. The caller composites only when every frame
/// of the shot passes. Compositing pastes the localised box interior into a copy of the
/// frame, so pixels outside the box come from the frame itself; the outside-box
/// diff is still counted per frame (0 by construction).
/// </summary>
public sealed class StaticShotCompositor
{
    public const double DefaultMinSsim = 0.95;

    /// <summary>
    /// Largest mean-luma change (0-255 levels) any single 8x8 block may show
    /// against the reference. The SSIM mean averages a small occlusion away;
    /// an occluder still moves its blocks' brightness, while a sub-pixel edge
    /// shift barely does. Per-block SSIM is not used here because it drops to
    /// about 0.2 on letter edges of an unchanged sign. On the coffee-shop shot
    /// the largest shift measured was 13.6; 30 leaves about 2x headroom. An
    /// occluder close in brightness to what it covers can still pass.
    /// </summary>
    public const double MaxBlockLumaShift = 30;

    /// <summary>SSIM block edge in pixels. Blocks tile the box; a trailing partial strip is covered by one block anchored at the far edge.</summary>
    public const int BlockSize = 8;

    public const string MetricDescription = "luma SSIM over 8x8 blocks covering the whole box region (trailing strip measured by an edge-anchored block), frame vs reference frame; gate on the block mean SSIM and on the largest single-block mean-luma shift";

    private const double C1 = (0.01 * 255) * (0.01 * 255);
    private const double C2 = (0.03 * 255) * (0.03 * 255);

    private readonly RasterImage _reference;
    private readonly RasterImage _patch;

    private StaticShotCompositor(RasterImage reference, RasterImage patch, LocalisationBox box, double minSsim)
    {
        _reference = reference;
        _patch = patch;
        Box = box;
        MinSsim = minSsim;
    }

    public LocalisationBox Box { get; }

    public double MinSsim { get; }

    /// <summary>Outcome of <see cref="Create"/>: exactly one of the two is set.</summary>
    public sealed record Creation(StaticShotCompositor? Compositor, string? Error);

    /// <summary>
    /// Validates the inputs and returns the reason when they cannot support a
    /// composite: mismatched sizes, a box outside the frame or smaller than one
    /// SSIM block, a localised frame that differs from the reference outside
    /// the box (so it was not made from this reference), or one that is
    /// unchanged inside the box (nothing was localised).
    /// </summary>
    public static Creation Create(RasterImage reference, RasterImage localised, LocalisationBox box, double minSsim)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(localised);
        ArgumentNullException.ThrowIfNull(box);
        string? error = ValidateThreshold(minSsim)
            ?? ValidateSizes(reference, localised)
            ?? ValidateBox(reference, box)
            ?? ValidateProvenance(reference, localised, box);
        if (error is not null)
        {
            return new Creation(null, error);
        }

        RasterImage patch = localised.Crop(box.X1, box.Y1, box.X2 - box.X1, box.Y2 - box.Y1);
        return new Creation(new StaticShotCompositor(reference, patch, box, minSsim), null);
    }

    /// <summary>
    /// A frame passes only when the box mean clears <see cref="MinSsim"/> and no
    /// single block's brightness moved more than <see cref="MaxBlockLumaShift"/>,
    /// so a small occlusion that the mean would average away still fails.
    /// </summary>
    public bool Passes(BoxSsim ssim) => ssim.Mean >= MinSsim && ssim.MaxBlockLumaShift <= MaxBlockLumaShift;

    /// <summary>Box-region SSIM of <paramref name="frame"/> against the reference frame.</summary>
    public BoxSsim MeasureSsim(RasterImage frame)
    {
        RequireSameSize(frame);
        return ComputeBoxSsim(_reference, frame, Box);
    }

    /// <summary>Returns a copy of <paramref name="frame"/> with the localised box interior pasted in.</summary>
    public RasterImage Composite(RasterImage frame, out int outsideBoxDiff)
    {
        RequireSameSize(frame);
        RasterImage composited = frame.Paste(_patch, Box.X1, Box.Y1);
        outsideBoxDiff = composited.CountDiffOutsideBox(frame, Box.X1, Box.Y1, Box.X2, Box.Y2);
        return composited;
    }

    /// <summary>The first frame below threshold, or null when the whole shot passes.</summary>
    public static StaticShotFrame? FirstFailure(IEnumerable<StaticShotFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return frames.FirstOrDefault(f => !f.Pass);
    }

    /// <summary>
    /// Mean luma SSIM over 8x8 blocks covering every pixel of <paramref name="box"/>,
    /// plus the largest single-block mean-luma shift. When a box side is not a
    /// multiple of 8, one extra row or column of blocks is anchored at the far
    /// edge (overlapping its neighbour), so the strip that is composited is
    /// also measured.
    /// </summary>
    public static BoxSsim ComputeBoxSsim(RasterImage a, RasterImage b, LocalisationBox box)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(box);
        string? error = ValidateSizes(a, b) ?? ValidateBox(a, box);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(box));
        }

        int[] xs = BlockOrigins(box.X1, box.X2);
        int[] ys = BlockOrigins(box.Y1, box.Y2);
        double sum = 0;
        double maxShift = 0;
        foreach (int y in ys)
        {
            foreach (int x in xs)
            {
                (double ssim, double shift) = CompareBlock(a, b, x, y);
                sum += ssim;
                maxShift = Math.Max(maxShift, shift);
            }
        }

        return new BoxSsim(sum / (xs.Length * ys.Length), maxShift);
    }

    /// <summary>Block start positions covering [start, end): full steps of 8, plus one block ending exactly at <paramref name="end"/> when a remainder is left.</summary>
    private static int[] BlockOrigins(int start, int end)
    {
        int full = (end - start) / BlockSize;
        IEnumerable<int> origins = Enumerable.Range(0, full).Select(i => start + i * BlockSize);
        return (end - start) % BlockSize == 0 ? origins.ToArray() : origins.Append(end - BlockSize).ToArray();
    }

    /// <summary>One block's SSIM and its absolute mean-luma shift.</summary>
    private static (double Ssim, double LumaShift) CompareBlock(RasterImage a, RasterImage b, int x0, int y0)
    {
        double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        for (int y = y0; y < y0 + BlockSize; y++)
        {
            for (int x = x0; x < x0 + BlockSize; x++)
            {
                double la = Luma(a, x, y);
                double lb = Luma(b, x, y);
                sumA += la;
                sumB += lb;
                sumAA += la * la;
                sumBB += lb * lb;
                sumAB += la * lb;
            }
        }

        const double n = BlockSize * BlockSize;
        double muA = sumA / n;
        double muB = sumB / n;
        double varA = sumAA / n - muA * muA;
        double varB = sumBB / n - muB * muB;
        double cov = sumAB / n - muA * muB;
        double ssim = ((2 * muA * muB + C1) * (2 * cov + C2)) / ((muA * muA + muB * muB + C1) * (varA + varB + C2));
        return (ssim, Math.Abs(muA - muB));
    }

    private static double Luma(RasterImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 3;
        return 0.299 * image.Rgb[i] + 0.587 * image.Rgb[i + 1] + 0.114 * image.Rgb[i + 2];
    }

    private static string? ValidateThreshold(double minSsim) =>
        minSsim > 0 && minSsim <= 1 ? null : "Minimum SSIM must be in (0, 1].";

    private static string? ValidateSizes(RasterImage reference, RasterImage localised) =>
        reference.Width == localised.Width && reference.Height == localised.Height
            ? null
            : $"Localised frame is {localised.Width}x{localised.Height}; reference frame is {reference.Width}x{reference.Height}.";

    private static string? ValidateBox(RasterImage frame, LocalisationBox box) =>
        IsInside(frame, box) && SpansOneBlock(box)
            ? null
            : $"Box ({box.X1},{box.Y1})-({box.X2},{box.Y2}) must lie inside the {frame.Width}x{frame.Height} frame and span at least {BlockSize}x{BlockSize} pixels.";

    private static bool IsInside(RasterImage frame, LocalisationBox box) =>
        box.X1 >= 0 && box.Y1 >= 0 && box.X2 <= frame.Width && box.Y2 <= frame.Height;

    private static bool SpansOneBlock(LocalisationBox box) =>
        box.X2 - box.X1 >= BlockSize && box.Y2 - box.Y1 >= BlockSize;

    private static string? ValidateProvenance(RasterImage reference, RasterImage localised, LocalisationBox box)
    {
        int outside = localised.CountDiffOutsideBox(reference, box.X1, box.Y1, box.X2, box.Y2);
        if (outside != 0)
        {
            return $"Localised frame differs from the reference in {outside} pixels outside the box, so it was not composited from this reference.";
        }

        int w = box.X2 - box.X1;
        int h = box.Y2 - box.Y1;
        if (localised.Crop(box.X1, box.Y1, w, h).Rgb.AsSpan().SequenceEqual(reference.Crop(box.X1, box.Y1, w, h).Rgb))
        {
            return "Localised frame is identical to the reference inside the box; there is nothing to composite.";
        }

        return null;
    }

    private void RequireSameSize(RasterImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != _reference.Width || frame.Height != _reference.Height)
        {
            throw new ArgumentException($"Frame is {frame.Width}x{frame.Height}; reference is {_reference.Width}x{_reference.Height}.", nameof(frame));
        }
    }
}
