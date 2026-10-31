using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// One labeled meaning-gate case: a source line, a candidate translation, and
/// the verdict a correct gate should return. <see cref="ErrorType"/> names the
/// seeded defect for expected failures (null for clean candidates).
/// </summary>
public sealed record MeaningEvalCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source_text")] string SourceText,
    [property: JsonPropertyName("candidate")] string Candidate,
    [property: JsonPropertyName("expected")] MeaningVerdict Expected,
    [property: JsonPropertyName("error_type")] string? ErrorType = null,
    [property: JsonPropertyName("note")] string? Note = null);

/// <summary>How one case scored. <see cref="MeaningEvalOutcome.ApiError"/> never counts as correct or incorrect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeaningEvalOutcome
{
    /// <summary>Seeded error, gate failed it.</summary>
    Caught,

    /// <summary>Seeded error, gate passed it.</summary>
    Missed,

    /// <summary>Clean candidate, gate passed it.</summary>
    CleanPass,

    /// <summary>Clean candidate, gate failed it.</summary>
    FalseAlarm,

    /// <summary>The gate could not run (transport or API failure).</summary>
    ApiError,
}

public sealed record MeaningEvalCaseResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source_text")] string SourceText,
    [property: JsonPropertyName("candidate")] string Candidate,
    [property: JsonPropertyName("expected")] MeaningVerdict Expected,
    [property: JsonPropertyName("error_type")] string? ErrorType,
    [property: JsonPropertyName("outcome")] MeaningEvalOutcome Outcome,
    [property: JsonPropertyName("gate")] MeaningCheckResult Gate);

public sealed record MeaningEvalErrorTypeScore(
    [property: JsonPropertyName("error_type")] string ErrorType,
    [property: JsonPropertyName("cases")] int Cases,
    [property: JsonPropertyName("caught")] int Caught);

/// <summary>
/// Scores over cases the gate actually judged. Recall is caught / seeded errors
/// judged; the false-positive rate is false alarms / clean candidates judged.
/// Either rate is null when its denominator is 0.
/// </summary>
public sealed record MeaningEvalSummary(
    [property: JsonPropertyName("cases")] int Cases,
    [property: JsonPropertyName("seeded_errors")] int SeededErrors,
    [property: JsonPropertyName("clean")] int Clean,
    [property: JsonPropertyName("caught")] int Caught,
    [property: JsonPropertyName("missed")] int Missed,
    [property: JsonPropertyName("false_alarms")] int FalseAlarms,
    [property: JsonPropertyName("api_errors")] int ApiErrors,
    [property: JsonPropertyName("recall")] double? Recall,
    [property: JsonPropertyName("false_positive_rate")] double? FalsePositiveRate,
    [property: JsonPropertyName("by_error_type")] MeaningEvalErrorTypeScore[] ByErrorType,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public sealed record MeaningEvalReport(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("prompt_template_version")] string PromptTemplateVersion,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("summary")] MeaningEvalSummary Summary,
    [property: JsonPropertyName("results")] MeaningEvalCaseResult[] Results);
