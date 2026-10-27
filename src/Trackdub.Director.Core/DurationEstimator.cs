namespace Trackdub.Director.Core;

/// <summary>
/// Deterministic spoken-duration estimator: seconds = charCount / charactersPerSecond.
/// No model call. Per-language CPS is configurable via <see cref="QaOptions"/>.
/// </summary>
public static class DurationEstimator
{
    /// <summary>
    /// Default characters per second when no language-specific value is configured.
    /// 15 cps approximates ~180 wpm at ~5 chars/word, typical for dubbed speech.
    /// </summary>
    public const double DefaultCharactersPerSecond = 15.0;

    public static double EstimateSeconds(string text, double charactersPerSecond)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (charactersPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(charactersPerSecond), "Characters per second must be positive.");
        }

        if (text.Length == 0)
        {
            return 0;
        }

        return text.Length / charactersPerSecond;
    }

    public static double EstimateSeconds(string text, string languageCode, QaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        double cps = options?.GetCharactersPerSecond(languageCode) ?? DefaultCharactersPerSecond;
        return EstimateSeconds(text, cps);
    }

    /// <summary>
    /// Returns true if the estimated spoken duration fits within the window allowing tolerance.
    /// Fit condition: estimate &lt;= window * (1 + toleranceFraction).
    /// Tolerance 0 means the estimate must not exceed the window at all.
    /// </summary>
    public static bool Fits(double estimatedSeconds, double windowSeconds, double toleranceFraction)
    {
        if (windowSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }

        if (toleranceFraction < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceFraction));
        }

        double allowed = windowSeconds * (1.0 + toleranceFraction);
        return estimatedSeconds <= allowed;
    }

    public static int MaxCharsForWindow(double windowSeconds, double charactersPerSecond)
    {
        if (windowSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }

        if (charactersPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(charactersPerSecond));
        }

        return (int)Math.Floor(windowSeconds * charactersPerSecond);
    }
}
