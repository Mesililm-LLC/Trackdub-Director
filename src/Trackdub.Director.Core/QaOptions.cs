namespace Trackdub.Director.Core;

public sealed class QaOptions
{
    /// <summary>
    /// Maximum revision attempts after the initial translation. Default 3 (up to 4 total model calls per segment).
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Fractional tolerance applied to the timing window. Estimate passes if estimate &lt;= window * (1 + tolerance).
    /// Default 0.10 (10%).
    /// </summary>
    public double ToleranceFraction { get; init; } = 0.10;

    /// <summary>
    /// Optional per-language characters-per-second overrides. Key is lowercased language code.
    /// When absent for a language, <see cref="DurationEstimator.DefaultCharactersPerSecond"/> is used.
    /// CLI exposes this via --cps-es, --cps-en, etc.; code may set it directly.
    /// </summary>
    public IReadOnlyDictionary<string, double> CharactersPerSecondByLanguage { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reasoning effort forwarded to the Meta API for translation/revision calls.
    /// Accepted values: low, medium, high. Default low (cuts the per-line reasoning_tokens overhead
    /// that otherwise dominates completion_tokens for this task). Controlled via META_REASONING_EFFORT.
    /// Set to null/empty to omit the parameter and use the model's default.
    /// </summary>
    public string? ReasoningEffort { get; init; } = "low";

    public static readonly QaOptions Default = new();

    public double GetCharactersPerSecond(string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        string key = languageCode.Trim().ToLowerInvariant();
        if (CharactersPerSecondByLanguage.TryGetValue(key, out double v) && v > 0)
        {
            return v;
        }

        return DurationEstimator.DefaultCharactersPerSecond;
    }

    public static QaOptions FromEnvironment()
    {
        string? reasoningRaw = Environment.GetEnvironmentVariable("META_REASONING_EFFORT");
        string? reasoning = string.IsNullOrWhiteSpace(reasoningRaw) ? "low" : reasoningRaw.Trim().ToLowerInvariant();
        if (reasoning is not ("low" or "medium" or "high"))
        {
            reasoning = "low";
        }

        // Optional per-language CPS overrides
        var cps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string code in new[] { "en", "es", "fr", "de", "pt", "it", "ja", "ko", "zh" })
        {
            string envName = $"QA_CPS_{code.ToUpperInvariant()}";
            string? raw = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
            {
                cps[code] = parsed;
            }
        }

        string? maxAttemptsRaw = Environment.GetEnvironmentVariable("QA_MAX_ATTEMPTS");
        int maxAttempts = 3;
        if (!string.IsNullOrWhiteSpace(maxAttemptsRaw) && int.TryParse(maxAttemptsRaw.Trim(), out int parsedAttempts) && parsedAttempts >= 0)
        {
            maxAttempts = parsedAttempts;
        }

        string? toleranceRaw = Environment.GetEnvironmentVariable("QA_TOLERANCE");
        double tolerance = 0.10;
        if (!string.IsNullOrWhiteSpace(toleranceRaw) && double.TryParse(toleranceRaw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedTol) && parsedTol >= 0)
        {
            tolerance = parsedTol;
        }

        return new QaOptions
        {
            MaxAttempts = maxAttempts,
            ToleranceFraction = tolerance,
            CharactersPerSecondByLanguage = cps,
            ReasoningEffort = reasoning,
        };
    }
}
