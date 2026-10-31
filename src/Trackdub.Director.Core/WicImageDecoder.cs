using System.Runtime.InteropServices;

namespace Trackdub.Director.Core;

/// <summary>
/// Image decoding through the Windows Imaging Component (JPEG, PNG, WEBP and
/// the other OS codecs) with no third-party packages. The repository ships no
/// managed image decoder, and Muse Image returns WEBP, which has no in-box
/// managed decoder; WIC is the platform codec on the demo machine. Pixels are
/// copied in the frame's native layout and channel-converted in managed code
/// (no format-converter COM object). Decoding fails closed: unsupported
/// platforms, payloads, and pixel formats throw with the reason.
/// </summary>
public static class WicImageDecoder
{
    private static readonly Guid ClsidWicImagingFactory = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
    private static readonly Guid Format24BppRgb = new("6fddc324-4e03-4bfe-b185-3d77768dc90d");
    private static readonly Guid Format24BppBgr = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    private static readonly Guid Format32BppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    private static readonly Guid Format32BppPbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    private static readonly Guid Format32BppRgba = new("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");
    private static readonly Guid Format32BppPrgba = new("3cc4a650-a527-4d37-a916-3142c7ebedba");
    private static readonly Guid Format8BppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc908");

    /// <summary>Decodes an image using WIC. This operation is available only on Windows.</summary>
    public static RasterImage Decode(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            throw new InvalidOperationException("Image payload is empty.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Image decoding requires Windows (WIC) on this build; no managed fallback codec ships in the repo.");
        }

        return DecodeWindows(imageBytes);
    }

#pragma warning disable CA1416 // Windows-only WIC interop, guarded by IsWindows above.
    private static RasterImage DecodeWindows(byte[] imageBytes)
    {
        IWicImagingFactory? factory = null;
        IWicBitmapDecoder? decoder = null;
        IWicBitmapFrameDecode? frame = null;
        IntPtr stream = IntPtr.Zero;
        GCHandle pinned = default;
        try
        {
            factory = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidWicImagingFactory)!) as IWicImagingFactory
                ?? throw new InvalidOperationException("WIC imaging factory is unavailable on this machine.");
            pinned = GCHandle.Alloc(imageBytes, GCHandleType.Pinned);
            stream = SHCreateMemStream(pinned.AddrOfPinnedObject(), (uint)imageBytes.Length);
            if (stream == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to wrap the image payload in a memory stream.");
            }

            Check(factory.CreateDecoderFromStream(stream, IntPtr.Zero, 2, out decoder), "create decoder");
            if (decoder is null)
            {
                throw new InvalidOperationException("Image decode failed: no decoder for this payload.");
            }

            Check(decoder.GetFrame(0, out frame), "get frame");
            if (frame is null)
            {
                throw new InvalidOperationException("Image decode failed: no frame in this payload.");
            }

            Check(frame.GetSize(out uint width, out uint height), "get size");
            Check(frame.GetPixelFormat(out Guid format), "get pixel format");
            if (width == 0 || height == 0 || width > 16384 || height > 16384)
            {
                throw new InvalidOperationException($"Decoded image has unusable dimensions {width}x{height}.");
            }

            const ulong maxPixels = 16_777_216;
            if ((ulong)width * height > maxPixels)
            {
                throw new InvalidOperationException($"Decoded image exceeds the {maxPixels} pixel limit.");
            }

            int bytesPerPixel = BytesPerPixel(format);
            uint stride = width * (uint)bytesPerPixel;
            byte[] raw = new byte[checked((int)(stride * height))];
            GCHandle rawPinned = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                Check(frame.CopyPixels(IntPtr.Zero, stride, (uint)raw.Length, rawPinned.AddrOfPinnedObject()), "copy pixels");
            }
            finally
            {
                rawPinned.Free();
            }

            return RasterImage.Create((int)width, (int)height, ToRgb24(raw, format));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            throw new InvalidOperationException($"Image payload could not be decoded: {ex.Message}", ex);
        }
        finally
        {
            Release(frame);
            Release(decoder);
            Release(factory);
            if (stream != IntPtr.Zero)
            {
                Marshal.Release(stream);
            }

            if (pinned.IsAllocated)
            {
                pinned.Free();
            }
        }
    }
#pragma warning restore CA1416

    private static int BytesPerPixel(Guid format)
    {
        if (format == Format24BppRgb || format == Format24BppBgr)
        {
            return 3;
        }

        if (format == Format32BppRgba || format == Format32BppBgra || format == Format32BppPrgba || format == Format32BppPbgra)
        {
            return 4;
        }

        if (format == Format8BppGray)
        {
            return 1;
        }

        throw new InvalidOperationException(
            $"Unsupported decoded pixel format {format}; supported: 24bpp RGB/BGR, 32bpp RGBA/BGRA (straight or premultiplied), 8bpp gray. Indexed (1/2/4/8bpp) and 16-bit formats are not converted.");
    }

    private static byte[] ToRgb24(byte[] raw, Guid format)
    {
        if (format == Format24BppRgb)
        {
            return raw;
        }

        bool premultiplied = format == Format32BppPrgba || format == Format32BppPbgra;
        byte[] rgb;
        if (format == Format8BppGray)
        {
            rgb = new byte[raw.Length * 3];
            for (int i = 0; i < raw.Length; i++)
            {
                rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = raw[i];
            }

            return rgb;
        }

        bool rgbOrder = format == Format32BppRgba || format == Format32BppPrgba;
        int pixels = format == Format24BppBgr ? raw.Length / 3 : raw.Length / 4;
        rgb = new byte[pixels * 3];
        for (int p = 0; p < pixels; p++)
        {
            byte r, g, b;
            if (format == Format24BppBgr)
            {
                b = raw[p * 3];
                g = raw[p * 3 + 1];
                r = raw[p * 3 + 2];
            }
            else
            {
                int i = p * 4;
                r = rgbOrder ? raw[i] : raw[i + 2];
                g = raw[i + 1];
                b = rgbOrder ? raw[i + 2] : raw[i];
                byte a = raw[i + 3];
                if (premultiplied && a is > 0 and < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }
            }

            rgb[p * 3] = r;
            rgb[p * 3 + 1] = g;
            rgb[p * 3 + 2] = b;
        }

        return rgb;
    }

    private static void Check(int hr, string step)
    {
        if (hr != 0)
        {
            throw new InvalidOperationException($"Image decode failed at {step} (HRESULT 0x{hr:X8}).");
        }
    }

#pragma warning disable CA1416 // COM release; only reached after the IsWindows guard.
    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
#pragma warning restore CA1416

    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHCreateMemStream(IntPtr bytes, uint length);

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, IntPtr vendor, uint access, int metadataOptions, out IWicBitmapDecoder decoder);
        [PreserveSig] int CreateDecoderFromStream(IntPtr stream, IntPtr vendor, int metadataOptions, out IWicBitmapDecoder decoder);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapDecoder
    {
        [PreserveSig] int QueryCapability(IntPtr stream, out uint capability);
        [PreserveSig] int Initialize(IntPtr stream, int cacheOptions);
        [PreserveSig] int GetContainerFormat(out Guid format);
        [PreserveSig] int GetDecoderInfo(out IntPtr info);
        [PreserveSig] int CopyPalette(IntPtr palette);
        [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
        [PreserveSig] int GetPreview(out IntPtr source);
        [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
        [PreserveSig] int GetThumbnail(out IntPtr source);
        [PreserveSig] int GetFrameCount(out uint count);
        [PreserveSig] int GetFrame(uint index, out IWicBitmapFrameDecode frame);
    }

    [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapFrameDecode
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid format);
        [PreserveSig] int GetResolution(out double dpix, out double dpiy);
        [PreserveSig] int CopyPalette(IntPtr palette);
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
        [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
        [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
        [PreserveSig] int GetThumbnail(out IntPtr source);
    }
}
