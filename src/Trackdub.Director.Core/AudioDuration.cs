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
    public static double Seconds(byte[] audioBytes, PcmFormat pcm)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        if (audioBytes.Length == 0)
        {
            throw new ArgumentException("Audio payload is empty.", nameof(audioBytes));
        }

        if (IsWav(audioBytes))
        {
            return WavSeconds(audioBytes);
        }

        pcm.Validate();
        return (double)audioBytes.Length / pcm.BytesPerFrame / pcm.SampleRate;
    }

    /// <summary>Wraps raw PCM samples in a minimal canonical WAV header for storage/playback.</summary>
    public static byte[] ToWav(byte[] audioBytes, PcmFormat pcm)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        if (IsWav(audioBytes))
        {
            return audioBytes;
        }

        pcm.Validate();
        using var stream = new MemoryStream(44 + audioBytes.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + audioBytes.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)pcm.Channels);
        writer.Write(pcm.SampleRate);
        writer.Write(pcm.SampleRate * pcm.BytesPerFrame);
        writer.Write((short)pcm.BytesPerFrame);
        writer.Write((short)pcm.BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(audioBytes.Length);
        writer.Write(audioBytes);
        writer.Flush();
        return stream.ToArray();
    }

    private static bool IsWav(byte[] bytes)
    {
        return bytes.Length >= 12 && HasTag(bytes, 0, "RIFF") && HasTag(bytes, 8, "WAVE");
    }

    private static bool HasTag(byte[] bytes, int offset, string tag)
    {
        return Encoding.ASCII.GetString(bytes, offset, tag.Length) == tag;
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
