using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>Pixel box with exclusive X2/Y2, matching <see cref="SceneRegion"/>.</summary>
public sealed record LocalisationBox(
    [property: JsonPropertyName("x1")] int X1,
    [property: JsonPropertyName("y1")] int Y1,
    [property: JsonPropertyName("x2")] int X2,
    [property: JsonPropertyName("y2")] int Y2);

public sealed record VisualLocalisationSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("frame_image_data_url")] string? FrameImageDataUrl = null,
    [property: JsonPropertyName("box")] LocalisationBox? Box = null,
    [property: JsonPropertyName("source_text")] string? SourceText = null,
    [property: JsonPropertyName("target_text")] string? TargetText = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisualLocalisationStatus
{
    Localised,
    Failed,
}

public sealed record VisualLocalisationProvenance(
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("endpoint_host")] string EndpointHost,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("prompt_template_version")] string PromptTemplateVersion);

public sealed record VisualLocalisationSegmentResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] VisualLocalisationStatus Status,
    [property: JsonPropertyName("localised_image_data_url")] string? LocalisedImageDataUrl,
    [property: JsonPropertyName("box")] LocalisationBox? Box,
    [property: JsonPropertyName("source_text")] string? SourceText,
    [property: JsonPropertyName("target_text")] string? TargetText,
    [property: JsonPropertyName("outside_box_diff")] int? OutsideBoxDiff,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("provenance")] VisualLocalisationProvenance Provenance,
    [property: JsonPropertyName("usage")] TranslationUsage Usage,
    [property: JsonPropertyName("image_calls")] int ImageCalls,
    [property: JsonIgnore] byte[]? LocalisedPngBytes = null);

public sealed record VisualLocalisationBatch(
    [property: JsonPropertyName("segments")] VisualLocalisationSegmentResult[] Segments,
    [property: JsonPropertyName("usage_total")] TranslationUsageSummary UsageTotal,
    [property: JsonPropertyName("image_calls_total")] int ImageCallsTotal);
