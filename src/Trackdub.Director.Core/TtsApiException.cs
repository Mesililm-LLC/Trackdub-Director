namespace Trackdub.Director.Core;

public sealed class TtsApiException : Exception
{
    // A rejected or missing key fails the whole request, so it is not recorded per take.
    public bool IsAuthFailure =>
        Message.Contains("HTTP 401", StringComparison.Ordinal)
        || Message.Contains("HTTP 403", StringComparison.Ordinal)
        || Message.Contains("No ElevenLabs API key", StringComparison.Ordinal);

    public TtsApiException(string message)
        : base(message)
    {
    }

    public TtsApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
