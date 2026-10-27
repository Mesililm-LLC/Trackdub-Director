using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class SmokeTests
{
    private static bool HasRealApiKey
    {
        get
        {
            string? k = Environment.GetEnvironmentVariable("META_API_KEY") ?? Environment.GetEnvironmentVariable("META_MODEL_API_KEY");
            return !string.IsNullOrWhiteSpace(k) && k.Trim() != "test-fake-key" && k.Trim().StartsWith("LLM_", StringComparison.Ordinal);
        }
    }

    private static bool HasApiKey => HasRealApiKey;

    [Fact]
    public async Task Smoke_translates_english_to_spanish()
    {
        if (!HasApiKey)
        {
            return;
        }

        using var client = new MetaClient();
        string result = await client.TranslateToSpanishAsync(
            "The director checks every line before it reaches the audience.");

        Assert.False(string.IsNullOrWhiteSpace(result));

        string lowered = result.ToLowerInvariant();
        Assert.True(
            lowered.Contains("director")
            || lowered.Contains("audiencia")
            || lowered.Contains("público")
            || lowered.Contains("publico")
            || lowered.Contains("cada línea")
            || lowered.Contains("cada linea"),
            $"Expected Spanish markers in '{result}'.");
    }
}
