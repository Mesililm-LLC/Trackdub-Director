using System.Buffers.Binary;
using System.Text;

namespace Trackdub.Director.Core;

/// <summary>
/// Duration math over synthesized audio bytes. WAV payloads are measured from
/// their header (data-chunk bytes over byte rate); raw PCM payloads use the
/// provider's declared sample parameters. Never an estimate.
/// </summary>
public static class AudioDuration
{
    private const int WavHeaderSize = 44;

    public static double Seconds(byte[] audioBytes, PcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        ArgumentNullException.ThrowIfNull(format);
        if (audioBytes.Length == 0)
        {
            throw new ArgumentException("Audio payload is empty.", nameof(audioBytes));
        }

        if (IsWav(audioBytes))
        {
            return WavSeconds(audioBytes);
        }

        ValidatePcm(format);
        double frames = (double)audioBytes.Length / format.BytesPerFrame;
        return frames / format.SampleRate;
    }

    /// <summary>Wraps raw PCM samples in a minimal canonical WAV header for storage/playback.</summary>
    public static byte[] ToWav(byte[] audioBytes, PcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        ArgumentNullException.ThrowIfNull(format);
        if (IsWav(audioBytes))
        {
            return audioBytes;
        }

        ValidatePcm(format);
        int blockAlign = format.BytesPerFrame;
        int byteRate = format.SampleRate * blockAlign;
        using var stream = new MemoryStream(WavHeaderSize + audioBytes.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + audioBytes.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)format.BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(audioBytes.Length);
        writer.Write(audioBytes);
        writer.Flush();
        return stream.ToArray();
    }

    private static void ValidatePcm(PcmFormat format)
    {
        if (!format.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Declared PCM parameters must be positive with whole-byte samples.");
        }
    }

    private static bool IsWav(byte[] bytes)
    {
        return bytes.Length >= 12 && HasTag(bytes.AsSpan(0, 4), "RIFF"u8) && HasTag(bytes.AsSpan(8, 4), "WAVE"u8);
    }

    private static bool HasTag(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> tag)
    {
        return bytes.SequenceEqual(tag);
    }

    private static double WavSeconds(byte[] wav)
    {
        int byteRate = 0;
        long dataBytes = -1;
        int offset = 12;
        while (offset + 8 <= wav.Length)
        {
            string chunkId = Encoding.ASCII.GetString(wav, offset, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            if (chunkSize < 0)
            {
                // A negative size would move the offset backwards and loop forever.
                throw new InvalidOperationException("WAV chunk size is negative.");
            }

            int payload = offset + 8;
            if (chunkId == "fmt " && payload + 12 <= wav.Length)
            {
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(payload + 8, 4));
            }
            else if (chunkId == "data")
            {
                dataBytes = Math.Min(chunkSize, wav.Length - payload);
                break;
            }

            // Chunks are word-aligned.
            offset = payload + chunkSize + (chunkSize % 2);
        }

        if (byteRate <= 0 || dataBytes < 0)
        {
            throw new InvalidOperationException("WAV payload is missing a usable fmt or data chunk.");
        }

        return (double)dataBytes / byteRate;
    }
}

/// <summary>PCM sample parameters declared by a TTS provider.</summary>
public sealed class PcmFormat
{
    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required int BitsPerSample { get; init; }

    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    public bool IsValid =>
        SampleRate > 0 && Channels > 0 && BitsPerSample > 0 && BitsPerSample % 8 == 0
        && (long)Channels * (BitsPerSample / 8) <= int.MaxValue
        && (long)SampleRate * ((long)Channels * (BitsPerSample / 8)) <= int.MaxValue;
}
