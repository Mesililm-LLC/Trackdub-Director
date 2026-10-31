using System.Diagnostics.CodeAnalysis;
using Trackdub.Director.Core;

// Checks request content and builds QA options. Each validator returns the error to send, or null.
internal static class RequestValidation
{
    private static readonly SegmentRules<AuditionSegmentInput> AuditionRules = new()
    {
        IsIncomplete = IsIncompleteAuditionSegment,
        IdOf = s => s.Id,
        IncompleteMessage = "Each segment must have a non-empty 'id' and 'text'.",
        MaxSegments = MaxAuditionSegments,
    };

    private static readonly SegmentRules<DirectorAdaptationSegmentInput> AdaptationRules = new()
    {
        IsIncomplete = s => s is null || string.IsNullOrWhiteSpace(s.Id),
        IdOf = s => s.Id,
        IncompleteMessage = "Each segment must have a non-empty 'id'.",
        MaxSegments = MaxAdaptationSegments,
    };

    internal static string? ValidateAuditionSegments(IReadOnlyList<AuditionSegmentInput> segments)
    {
        string? error = ValidateSegments(segments, AuditionRules);
        if (error is not null)
        {
            return error;
        }

        return segments.Any(s => s.Text.Length > MaxAuditionTextLength)
            ? $"Each segment text must be at most {MaxAuditionTextLength} characters."
            : null;
    }

    private static bool IsIncompleteAuditionSegment(AuditionSegmentInput segment)
    {
        return segment is null || string.IsNullOrWhiteSpace(segment.Id) || string.IsNullOrWhiteSpace(segment.Text);
    }

    private sealed class SegmentRules<T>
    {
        public required Func<T, bool> IsIncomplete { get; init; }

        public required Func<T, string> IdOf { get; init; }

        public required string IncompleteMessage { get; init; }

        public required int MaxSegments { get; init; }
    }

    // Shared shape for the segment validators: completeness, unique ids, then count.
    private static string? ValidateSegments<T>(IReadOnlyList<T> segments, SegmentRules<T> rules)
        where T : class
    {
        if (segments.Any(rules.IsIncomplete))
        {
            return rules.IncompleteMessage;
        }

        if (HasDuplicateIds(segments.Select(s => rules.IdOf(s).Trim())))
        {
            return "Duplicate segment ids are not allowed.";
        }

        if (segments.Count > rules.MaxSegments)
        {
            return $"At most {rules.MaxSegments} segments are allowed per request.";
        }

        return null;
    }

    private static bool HasDuplicateIds(IEnumerable<string> ids)
    {
        List<string> list = ids.ToList();
        return list.Distinct(StringComparer.Ordinal).Count() != list.Count;
    }

    // Builds QaOptions with request overrides > env defaults, same precedence the CLI uses.
    internal static bool TryBuildQaOptions(QaApiRequest req, string to, [NotNullWhen(true)] out QaOptions? options, [NotNullWhen(false)] out string? error)
    {
        options = null;
        error = null;

        QaOptions env = QaOptions.FromEnvironment();
        int maxAttempts = req.Options?.MaxAttempts ?? env.MaxAttempts;
        double tolerance = req.Options?.ToleranceFraction ?? env.ToleranceFraction;
        double? cps = req.Options?.TargetCharactersPerSecond;
        string? reasoningEffort = req.Options?.ReasoningEffort ?? env.ReasoningEffort;

        error = ValidateQaOptionValues(maxAttempts, tolerance, cps);
        if (error is not null)
        {
            return false;
        }

        if (reasoningEffort is not null)
        {
            reasoningEffort = NormalizeReasoningEffort(reasoningEffort);
            if (reasoningEffort is null)
            {
                error = "reasoningEffort must be low, medium, or high.";
                return false;
            }
        }

        options = new QaOptions
        {
            MaxAttempts = maxAttempts,
            ToleranceFraction = tolerance,
            CharactersPerSecondByLanguage = BuildCpsOverrides(env, to, cps),
            ReasoningEffort = reasoningEffort,
        };
        return true;
    }

    internal static string? ValidateQaOptionValues(int maxAttempts, double tolerance, double? cps)
    {
        if (maxAttempts < 0)
        {
            return "maxAttempts must be >= 0.";
        }

        if (tolerance < 0)
        {
            return "toleranceFraction must be >= 0.";
        }

        if (cps is <= 0.0)
        {
            return "targetCharactersPerSecond must be > 0.";
        }

        return null;
    }

    // Copies the env CPS defaults, then applies the request's CPS for the target language when one was given.
    internal static Dictionary<string, double> BuildCpsOverrides(QaOptions env, string to, double? cps)
    {
        var overrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in env.CharactersPerSecondByLanguage)
        {
            overrides[kv.Key] = kv.Value;
        }

        if (cps is { } value)
        {
            overrides[to.ToLowerInvariant()] = value;
        }

        return overrides;
    }

    // Lowercases and trims a reasoning effort, or returns null when it is not one of the supported levels.
    internal static string? NormalizeReasoningEffort(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" ? normalized : null;
    }

    private const int MaxSceneSegments = 100;

    private const int MaxConceptsPerSegment = 8;

    private const int MaxAdaptationSegments = 100;

    private const int MaxAuditionSegments = 100;

    private const int MaxVisualSegments = 100;

    // Bounds per-segment request size and the number of Meta and TTS calls one line can cost.
    private const int MaxAuditionTextLength = 1000;

    // Each validator returns the error message to send back, or null when the request is acceptable.
    internal static string? ValidateSceneSegments(SceneSegmentInput[] segments)
    {
        if (segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)))
        {
            return "Each segment must have a non-empty 'id'.";
        }

        if (segments.Length > MaxSceneSegments)
        {
            return $"At most {MaxSceneSegments} segments are allowed per request.";
        }

        if (segments.Any(s => (s.Concepts?.Count(c => !string.IsNullOrWhiteSpace(c)) ?? 0) > MaxConceptsPerSegment))
        {
            return $"At most {MaxConceptsPerSegment} concepts are allowed per segment.";
        }

        return null;
    }

    internal static string? ValidateAdaptationSegments(IReadOnlyList<DirectorAdaptationSegmentInput> segments)
    {
        return ValidateSegments(segments, AdaptationRules);
    }

    internal static string? ValidateVisualSegments(VisualLocalisationSegmentInput[] segments)
    {
        if (segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)))
        {
            return "Each segment must have a non-empty 'id'.";
        }

        return segments.Length > MaxVisualSegments
            ? $"At most {MaxVisualSegments} segments are allowed per request."
            : null;
    }

    // Trims the caller's language, or falls back to the default when it is missing or blank.
    internal static string ResolveLanguage(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
