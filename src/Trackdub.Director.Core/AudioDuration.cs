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
    public static double Seconds(byte[] audioBytes, int pcmSampleRate, int pcmChannels, int pcmBitsPerSample)
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

        ValidatePcm(pcmSampleRate, pcmChannels, pcmBitsPerSample);
        int bytesPerFrame = pcmChannels * (pcmBitsPerSample / 8);
        double frames = (double)audioBytes.Length / bytesPerFrame;
        return frames / pcmSampleRate;
    }

    /// <summary>Wraps raw PCM samples in a minimal canonical WAV header for storage/playback.</summary>
    public static byte[] ToWav(byte[] audioBytes, int pcmSampleRate, int pcmChannels, int pcmBitsPerSample)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        if (IsWav(audioBytes))
        {
            return audioBytes;
        }

        ValidatePcm(pcmSampleRate, pcmChannels, pcmBitsPerSample);
        int blockAlign = pcmChannels * (pcmBitsPerSample / 8);
        int byteRate = pcmSampleRate * blockAlign;
        using var stream = new MemoryStream(44 + audioBytes.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + audioBytes.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)pcmChannels);
        writer.Write(pcmSampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)pcmBitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(audioBytes.Length);
        writer.Write(audioBytes);
        writer.Flush();
        return stream.ToArray();
    }

    private static void ValidatePcm(int sampleRate, int channels, int bitsPerSample)
    {
        if (sampleRate <= 0 || channels <= 0 || bitsPerSample <= 0 || bitsPerSample % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Declared PCM parameters must be positive with whole-byte samples.");
        }
    }

    private static bool IsWav(byte[] bytes)
    {
        return bytes.Length >= 12
            && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E';
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
