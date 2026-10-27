using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

public sealed record QaSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QaVerdict
{
    Pass,
    Fail,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QaAttemptKind
{
    Initial,
    Revision,
}

public sealed record QaAttemptReport(
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("kind")] QaAttemptKind Kind,
    [property: JsonPropertyName("translation")] string Translation,
    [property: JsonPropertyName("estimated_seconds")] double EstimatedSeconds,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("max_chars")] int MaxChars,
    [property: JsonPropertyName("verdict")] QaVerdict Verdict,
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("model_id")] string ModelId);

public sealed record QaSegmentReport(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("first_attempt")] QaAttemptReport FirstAttempt,
    [property: JsonPropertyName("revisions")] QaAttemptReport[] Revisions,
    [property: JsonPropertyName("final_verdict")] QaVerdict FinalVerdict,
    [property: JsonPropertyName("final_translation")] string FinalTranslation,
    [property: JsonPropertyName("token_usage")] QaTokenUsage TokenUsage);

public sealed record QaTokenUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public sealed record QaReportOptions(
    [property: JsonPropertyName("max_attempts")] int MaxAttempts,
    [property: JsonPropertyName("tolerance_fraction")] double ToleranceFraction,
    [property: JsonPropertyName("target_cps")] double TargetCharactersPerSecond,
    [property: JsonPropertyName("source_cps")] double SourceCharactersPerSecond,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort);

public sealed record QaTotals(
    [property: JsonPropertyName("total_segments")] int TotalSegments,
    [property: JsonPropertyName("first_pass_count")] int FirstPassCount,
    [property: JsonPropertyName("final_pass_count")] int FinalPassCount,
    [property: JsonPropertyName("total_revisions")] int TotalRevisions,
    [property: JsonPropertyName("first_pass_rate")] double FirstPassRate,
    [property: JsonPropertyName("final_pass_rate")] double FinalPassRate,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("prompt_tokens")] int TotalPromptTokens,
    [property: JsonPropertyName("completion_tokens")] int TotalCompletionTokens);

public sealed record QaReport(
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("qa_options")] QaReportOptions QaOptions,
    [property: JsonPropertyName("segments")] QaSegmentReport[] Segments,
    [property: JsonPropertyName("totals")] QaTotals Totals);
