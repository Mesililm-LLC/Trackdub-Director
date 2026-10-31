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
}
