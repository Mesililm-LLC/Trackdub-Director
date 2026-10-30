using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Per-segment opt-in for Director Mode Stage B2 register adaptation. TargetRegister
/// is carried as the raw caller-supplied string so an unsupported value fails the
/// segment closed with zero spend instead of breaking batch deserialization.
/// Valid values (case-insensitive): "formal", "informal".
/// </summary>
public sealed record DirectorAdaptationSpec(
    [property: JsonPropertyName("target_register")] string? TargetRegister = null);

/// <summary>
/// Input for the director-adaptation lane. Text is the source line and Translation is
/// the segment's existing translation (produced by an earlier lane or supplied by the
/// caller); this lane never translates from scratch. Context is the segment's
/// DirectorContext when one exists (Stage A/B1 output or caller-supplied); only its
/// non-null emotion/tone, scene summary, speaker description, and on-screen text
/// fields are sent to the prompt. Segments without DirectorAdaptation are untouched
/// and make no calls, the same opt-in pattern as vision enrichment.
/// </summary>
public sealed record DirectorAdaptationSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("translation")] string? Translation = null,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds = null,
    [property: JsonPropertyName("context")] DirectorContext? Context = null,
    [property: JsonPropertyName("director_adaptation")] DirectorAdaptationSpec? DirectorAdaptation = null);

/// <summary>
/// Passed: the adapted line passed timing AND the meaning gate returned Pass.
/// Failed: anything else — invalid register, missing source/translation, malformed
/// or failed adaptation call, timing Fail, or meaning Fail/Error. A failed
/// adaptation is reported with its verdicts, never silently accepted and never
/// revised in this stage. NotRequested: the segment did not opt in; no calls made.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DirectorAdaptationStatus
{
    Passed,
    Failed,
    NotRequested,
}

/// <summary>
/// Full token usage for one adaptation call (or a batch total). ReasoningTokens and
/// CachedTokens mirror the API's usage detail objects and stay null when the API
/// does not provide them; they are never estimated.
/// </summary>
public sealed record DirectorAdaptationUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens = null,
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens = null);

/// <summary>
/// Meaning-gate outcome for one adapted line, mapped from MeaningCheckResult.
/// Verdict NotEvaluated means the timing gate already failed so the meaning check
/// was deliberately skipped (zero meaning tokens), following the meaning gate's
/// existing conventions. Model/template/timestamp are null then.
/// </summary>
public sealed record DirectorMeaningResult(
    [property: JsonPropertyName("verdict")] MeaningVerdict Verdict,
    [property: JsonPropertyName("issues")] string[] Issues,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("model_id")] string? ModelId,
    [property: JsonPropertyName("prompt_template_version")] string? PromptTemplateVersion,
    [property: JsonPropertyName("timestamp_utc")] string? TimestampUtc,
    [property: JsonPropertyName("usage")] TranslationUsage Usage);

/// <summary>
/// Per-segment adaptation result. TargetRegister echoes what the caller asked for
/// (normalized to "formal"/"informal" when valid, the raw value when not).
/// ContextFieldsUsed is the exact list of DirectorContext fields actually supplied
/// to the prompt, in prompt order; empty when context was absent (a valid run) or
/// when no adaptation call was made. Changed is an exact (ordinal) comparison of
/// the adapted line against the input translation: false means the model returned
/// the line unchanged (a recorded outcome, not an error); null when no adapted
/// line was produced. Provenance/Usage describe the adaptation call
/// and are null when no call was attempted. Timing fields and Meaning are present
/// only when an adapted line was produced and re-verified.
/// </summary>
public sealed record DirectorAdaptationSegmentResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] DirectorAdaptationStatus Status,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("original_translation")] string? OriginalTranslation,
    [property: JsonPropertyName("adapted_translation")] string? AdaptedTranslation,
    [property: JsonPropertyName("changed")] bool? Changed,
    [property: JsonPropertyName("target_register")] string? TargetRegister,
    [property: JsonPropertyName("context_fields_used")] string[] ContextFieldsUsed,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("provenance")] TranslationProvenance? Provenance,
    [property: JsonPropertyName("usage")] DirectorAdaptationUsage? Usage,
    [property: JsonPropertyName("timing_verdict")] QaVerdict? TimingVerdict = null,
    [property: JsonPropertyName("estimated_seconds")] double? EstimatedSeconds = null,
    [property: JsonPropertyName("window_seconds")] double? WindowSeconds = null,
    [property: JsonPropertyName("meaning")] DirectorMeaningResult? Meaning = null,
    [property: JsonPropertyName("raw_content")] string? RawContent = null);

public sealed record DirectorAdaptationBatch(
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("segments")] DirectorAdaptationSegmentResult[] Segments,
    [property: JsonPropertyName("usage_total")] DirectorAdaptationUsage UsageTotal,
    [property: JsonPropertyName("meaning_usage_total")] TranslationUsageSummary MeaningUsageTotal);
