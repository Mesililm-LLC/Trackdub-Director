namespace Trackdub.Director.Core;

/// <summary>
/// Declared sample layout of raw PCM audio from a TTS provider. Only whole-byte samples are supported.
/// </summary>
public readonly record struct PcmFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    /// <summary>Throws when the declared layout cannot describe a whole number of bytes per frame.</summary>
    public void Validate()
    {
        if (SampleRate <= 0 || Channels <= 0 || BitsPerSample <= 0 || BitsPerSample % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), "Declared PCM parameters must be positive with whole-byte samples.");
        }
    }
}
