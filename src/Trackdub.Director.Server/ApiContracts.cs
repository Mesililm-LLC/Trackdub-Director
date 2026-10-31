using System.Text.Json.Serialization;
using Trackdub.Director.Core;

// Request and response shapes for the HTTP API.

internal sealed record StatusResponse(
    [property: JsonPropertyName("hasKey")] bool HasKey,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("reasoningEffort")] string ReasoningEffort);

internal sealed record ModelDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);

internal sealed record TranslateRequest(
    IReadOnlyList<TranslationSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage);

internal sealed record QaApiRequest(
    IReadOnlyList<QaSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage,
    QaOptionsDto? Options);

internal sealed record AdaptationApiRequest(
    IReadOnlyList<DirectorAdaptationSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage);

internal sealed record AuditionApiRequest(
    IReadOnlyList<AuditionSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage);

internal sealed record QaOptionsDto(
    int? MaxAttempts,
    double? ToleranceFraction,
    double? TargetCharactersPerSecond,
    string? ReasoningEffort);
