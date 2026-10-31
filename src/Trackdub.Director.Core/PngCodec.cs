using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Trackdub.Director.Core;

/// <summary>
/// Minimal PNG encoder (filter-none rows, zlib via the BCL). Decoding is left
/// to <see cref="WicImageDecoder"/>, which handles every input format through
/// the operating system's codecs; this encoder exists so localised outputs
/// need no third-party package either.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var output = new MemoryStream();
        output.Write(Signature);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), image.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // truecolor RGB
        WriteChunk(output, "IHDR", ihdr);

        using var idatRaw = new MemoryStream();
        using (var zlib = new ZLibStream(idatRaw, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = image.Width * 3;
            for (int y = 0; y < image.Height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(image.Rgb, y * stride, stride);
            }
        }

        WriteChunk(output, "IDAT", idatRaw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc32(typeBytes, data);
        byte[] crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
