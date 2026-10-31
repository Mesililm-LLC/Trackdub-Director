using System.Buffers.Binary;
using System.Text;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Duration measurement for raw PCM payloads, which use the provider's declared sample layout.
/// </summary>
public sealed class AudioDurationTests
{
    [Fact]
    public void Seconds_measures_raw_pcm_from_the_declared_format()
    {
        // 16 kHz mono 16-bit: 32,000 bytes is one second of audio.
        var format = new PcmFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

        Assert.Equal(1.0, AudioDuration.Seconds(new byte[32000], format), precision: 6);
    }

    [Fact]
    public void Seconds_rejects_a_wav_with_a_negative_chunk_size()
    {
        // A pre-data chunk with size -8 at offset 12 would move the parser back to offset 12 forever.
        byte[] wav = new byte[20];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("JUNK").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), -8);
        var format = new PcmFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

        Assert.Throws<InvalidOperationException>(() => AudioDuration.Seconds(wav, format));
    }

    [Fact]
    public void PcmFormat_rejects_channel_counts_whose_derived_values_overflow()
    {
        var format = new PcmFormat { SampleRate = 16000, Channels = int.MaxValue, BitsPerSample = 16 };

        Assert.False(format.IsValid);
    }
}
