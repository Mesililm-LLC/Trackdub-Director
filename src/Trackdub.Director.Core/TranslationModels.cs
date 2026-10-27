using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Input file: array of segments, each with an id and text.
/// Example: [{ "id": "seg-001", "text": "Hello, world." }]
/// </summary>
public sealed record TranslationSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// Output file: batch result with per-segment provenance and usage.
/// </summary>
public sealed record TranslationBatchOutput(
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("segments")] TranslatedSegmentOutput[] Segments,
    [property: JsonPropertyName("usage_total")] TranslationUsageSummary UsageTotal);

public sealed record TranslatedSegmentOutput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("translation")] string Translation,
    [property: JsonPropertyName("provenance")] TranslationProvenance Provenance,
    [property: JsonPropertyName("usage")] TranslationUsage Usage);

public sealed record TranslationProvenance(
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("endpoint_host")] string EndpointHost,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("prompt_template_version")] string PromptTemplateVersion);

public sealed record TranslationUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public sealed record TranslationUsageSummary(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
