using Trackdub.Director.Core;

// Maps upstream failures to HTTP results and reports whether an API key is configured.
internal static class ApiResults
{
    internal static bool HasApiKey()
    {
        try
        {
            _ = MetaAuth.GetApiKey();
            return true;
        }
        catch (MetaApiException)
        {
            return false;
        }
    }

    internal static IResult MapMetaException(MetaApiException ex)
    {
        // Keys are never logged or echoed; the message already omits the key per MetaClient invariant.
        // 401 when no key is configured; otherwise 502 (upstream).
        string msg = ex.Message ?? "Meta Model API error";
        if (msg.Contains("No Meta Model API key", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = msg }, statusCode: 401);
        }

        if (msg.Contains("HTTP 401", StringComparison.Ordinal) || msg.Contains("HTTP 403", StringComparison.Ordinal))
        {
            return Results.Json(new { error = msg }, statusCode: 401);
        }

        return Results.Json(new { error = msg }, statusCode: 502);
    }

    internal static IResult MapTtsException(TtsApiException ex)
    {
        string msg = ex.Message ?? "ElevenLabs API error";
        return Results.Json(new { error = msg }, statusCode: IsAuthFailure(msg) ? 401 : 502);
    }

    private static bool IsAuthFailure(string msg)
    {
        return msg.Contains("No ElevenLabs API key", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("HTTP 401", StringComparison.Ordinal)
            || msg.Contains("HTTP 403", StringComparison.Ordinal);
    }
}
