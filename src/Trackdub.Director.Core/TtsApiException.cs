namespace Trackdub.Director.Core;

public sealed class TtsApiException : Exception
{
    public TtsApiException(string message)
        : base(message)
    {
    }

    public TtsApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
