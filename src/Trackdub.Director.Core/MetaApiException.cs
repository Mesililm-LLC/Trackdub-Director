namespace Trackdub.Director.Core;

public sealed class MetaApiException : Exception
{
    public MetaApiException(string message)
        : base(message)
    {
    }

    public MetaApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
