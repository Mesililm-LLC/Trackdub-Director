namespace Trackdub.Director.Core;

/// <summary>
/// Decoded RGB24 raster (row-major, top-down, 3 bytes per pixel). All visual
/// localisation pixel work happens in this space so composite correctness is
/// independent of any particular encoded format.
/// </summary>
public sealed record RasterImage(int Width, int Height, byte[] Rgb)
{
    public static RasterImage Create(int width, int height, byte[] rgb)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Raster dimensions must be positive.");
        }

        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length != checked(width * height * 3))
        {
            throw new ArgumentException("RGB payload length does not match the raster dimensions.", nameof(rgb));
        }

        return new RasterImage(width, height, rgb);
    }

    public RasterImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Crop rectangle must lie inside the raster.");
        }

        byte[] rgb = new byte[width * height * 3];
        for (int row = 0; row < height; row++)
        {
            int src = ((y + row) * Width + x) * 3;
            Array.Copy(Rgb, src, rgb, row * width * 3, width * 3);
        }

        return new RasterImage(width, height, rgb);
    }

    /// <summary>Returns a copy with <paramref name="patch"/> pasted at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public RasterImage Paste(RasterImage patch, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (x < 0 || y < 0 || x + patch.Width > Width || y + patch.Height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Patch must lie inside the raster.");
        }

        byte[] rgb = (byte[])Rgb.Clone();
        for (int row = 0; row < patch.Height; row++)
        {
            int dst = ((y + row) * Width + x) * 3;
            Array.Copy(patch.Rgb, row * patch.Width * 3, rgb, dst, patch.Width * 3);
        }

        return new RasterImage(Width, Height, rgb);
    }

    /// <summary>Bilinear resize. Model outputs arrive at unrelated resolutions, so the edited crop is always resampled back to the crop rectangle before compositing.</summary>
    public RasterImage ResizeBilinear(int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "Target dimensions must be positive.");
        }

        if (targetWidth == Width && targetHeight == Height)
        {
            return this;
        }

        byte[] rgb = new byte[targetWidth * targetHeight * 3];
        double scaleX = (double)Width / targetWidth;
        double scaleY = (double)Height / targetHeight;
        for (int dy = 0; dy < targetHeight; dy++)
        {
            double srcY = Math.Min((dy + 0.5) * scaleY - 0.5, Height - 1);
            int y0 = Math.Max(0, (int)Math.Floor(srcY));
            int y1 = Math.Min(Height - 1, y0 + 1);
            double fy = Math.Clamp(srcY - y0, 0, 1);
            for (int dx = 0; dx < targetWidth; dx++)
            {
                double srcX = Math.Min((dx + 0.5) * scaleX - 0.5, Width - 1);
                int x0 = Math.Max(0, (int)Math.Floor(srcX));
                int x1 = Math.Min(Width - 1, x0 + 1);
                double fx = Math.Clamp(srcX - x0, 0, 1);
                int dst = (dy * targetWidth + dx) * 3;
                for (int c = 0; c < 3; c++)
                {
                    double top = Rgb[(y0 * Width + x0) * 3 + c] * (1 - fx) + Rgb[(y0 * Width + x1) * 3 + c] * fx;
                    double bottom = Rgb[(y1 * Width + x0) * 3 + c] * (1 - fx) + Rgb[(y1 * Width + x1) * 3 + c] * fx;
                    rgb[dst + c] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        }

        return new RasterImage(targetWidth, targetHeight, rgb);
    }

    /// <summary>Counts pixels outside the box whose RGB value differs from <paramref name="other"/> (same dimensions required).</summary>
    public int CountDiffOutsideBox(RasterImage other, int boxX1, int boxY1, int boxX2, int boxY2)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Width != Width || other.Height != Height)
        {
            throw new ArgumentException("Rasters must share dimensions to diff.", nameof(other));
        }

        int diff = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (x >= boxX1 && x < boxX2 && y >= boxY1 && y < boxY2)
                {
                    continue;
                }

                int i = (y * Width + x) * 3;
                if (Rgb[i] != other.Rgb[i] || Rgb[i + 1] != other.Rgb[i + 1] || Rgb[i + 2] != other.Rgb[i + 2])
                {
                    diff++;
                }
            }
        }

        return diff;
    }
}
