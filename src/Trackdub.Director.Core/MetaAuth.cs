namespace Trackdub.Director.Core;

/// <summary>
/// Resolves Meta Model API credentials and endpoint configuration from environment
/// variables only. Mirrors the Python probe contract.
/// </summary>
public static class MetaAuth
{
    public const string PrimaryApiKeyEnv = "META_API_KEY";
    public const string FallbackApiKeyEnv = "META_MODEL_API_KEY";
    public const string BaseUrlEnv = "META_MODEL_API_BASE_URL";
    public const string ModelEnv = "META_MODEL";
    public const string ReasoningEffortEnv = "META_REASONING_EFFORT";

    public const string DefaultBaseUrl = "https://api.meta.ai/v1";
    public const string DefaultModel = "muse-spark-1.1";
    public const string DefaultReasoningEffort = "low";

    public static string GetApiKey()
    {
        string? primary = Environment.GetEnvironmentVariable(PrimaryApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary.Trim();
        }

        string? fallback = Environment.GetEnvironmentVariable(FallbackApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback.Trim();
        }

        throw new MetaApiException(
            $"No Meta Model API key found. Set {PrimaryApiKeyEnv} in your environment (see .env.example). Keys must never be committed to this repo.");
    }

    public static string GetBaseUrl()
    {
        string? raw = Environment.GetEnvironmentVariable(BaseUrlEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultBaseUrl;
        }

        return raw.Trim().TrimEnd('/');
    }

    public static string GetModel()
    {
        string? raw = Environment.GetEnvironmentVariable(ModelEnv);
        return string.IsNullOrWhiteSpace(raw) ? DefaultModel : raw.Trim();
    }

    public static string? GetReasoningEffort()
    {
        string? raw = Environment.GetEnvironmentVariable(ReasoningEffortEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultReasoningEffort;
        }

        return raw.Trim().ToLowerInvariant();
    }
}
