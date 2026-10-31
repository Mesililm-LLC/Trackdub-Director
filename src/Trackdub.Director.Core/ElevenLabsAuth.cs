namespace Trackdub.Director.Core;

/// <summary>
/// Resolves the ElevenLabs API key from the environment only, mirroring
/// <see cref="MetaAuth"/>: keys are never read from files and never logged.
/// </summary>
public static class ElevenLabsAuth
{
    public const string ApiKeyEnv = "ELEVENLABS_API_KEY";

    public const string DefaultBaseUrl = "https://api.elevenlabs.io/v1";

    public static bool HasApiKey()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnv));
    }

    public static string GetApiKey()
    {
        string? raw = Environment.GetEnvironmentVariable(ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new TtsApiException(
                $"No ElevenLabs API key found. Set {ApiKeyEnv} in your environment (see .env.example). Keys must never be committed to this repo.");
        }

        return raw.Trim();
    }

    public static string GetBaseUrl()
    {
        string? raw = Environment.GetEnvironmentVariable("ELEVENLABS_API_BASE_URL");
        return string.IsNullOrWhiteSpace(raw) ? DefaultBaseUrl : raw.Trim().TrimEnd('/');
    }
}
