using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Director (scene) context for one segment. Every field is nullable: absence is an
/// honest null, never a guessed default. Stage A's only evidence source is sam-3.1
/// segmentation, which supplies <see cref="Regions"/> and nothing else — speaker is
/// carried through only when the caller already knows it, and emotion/tone,
/// on-screen text, and scene summary stay null until a later stage can evidence them.
/// </summary>
public sealed record DirectorContext(
    [property: JsonPropertyName("speaker_id")] string? SpeakerId,
    [property: JsonPropertyName("speaker_label")] string? SpeakerLabel,
    [property: JsonPropertyName("emotion_tone_label")] string? EmotionToneLabel,
    [property: JsonPropertyName("on_screen_text")] string[]? OnScreenText,
    [property: JsonPropertyName("scene_summary")] string? SceneSummary,
    [property: JsonPropertyName("regions")] SceneRegion[]? Regions = null);

/// <summary>
/// One segmented region for a caller-supplied concept phrase, parsed from the
/// probe-verified sam-3.1 output_text tokens. Box coordinates are pixels in the
/// frame of size FrameWidth x FrameHeight. MaskData is the opaque payload after
/// "data=" in the mask token, preserved verbatim and not decoded in Stage A;
/// null when the response carried a box with no mask token.
/// </summary>
public sealed record SceneRegion(
    [property: JsonPropertyName("concept")] string Concept,
    [property: JsonPropertyName("x1")] int X1,
    [property: JsonPropertyName("y1")] int Y1,
    [property: JsonPropertyName("x2")] int X2,
    [property: JsonPropertyName("y2")] int Y2,
    [property: JsonPropertyName("frame_width")] int FrameWidth,
    [property: JsonPropertyName("frame_height")] int FrameHeight,
    [property: JsonPropertyName("mask_x")] int? MaskX = null,
    [property: JsonPropertyName("mask_y")] int? MaskY = null,
    [property: JsonPropertyName("mask_data")] string? MaskData = null);

/// <summary>
/// Parsed: at least one concept produced a region. Empty: no region was found, or
/// no frame/concepts were supplied so nothing was queried (Regions null then, []
/// when queried and missed). Failed: malformed model output or transport/API
/// failure — Context is null, never a defaulted all-null context.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SceneContextStatus
{
    Parsed,
    Empty,
    Failed,
}

/// <summary>
/// Input for the scene-context lane. FrameImageDataUrl is a data:image/ or https://
/// URL for one representative frame; Concepts are the phrases to segment (sent to
/// sam-3.1 verbatim). Speaker fields are caller-known only; they are never derived.
/// </summary>
public sealed record SceneSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("frame_image_data_url")] string? FrameImageDataUrl = null,
    [property: JsonPropertyName("concepts")] string[]? Concepts = null,
    [property: JsonPropertyName("speaker_id")] string? SpeakerId = null,
    [property: JsonPropertyName("speaker_label")] string? SpeakerLabel = null);

public sealed record SceneSegmentResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] SceneContextStatus Status,
    [property: JsonPropertyName("context")] DirectorContext? Context,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("provenance")] TranslationProvenance Provenance,
    [property: JsonPropertyName("usage")] TranslationUsage Usage,
    [property: JsonPropertyName("images_processed")] int ImagesProcessed,
    [property: JsonPropertyName("raw_content")] string? RawContent = null);

public sealed record SceneContextBatch(
    [property: JsonPropertyName("segments")] SceneSegmentResult[] Segments,
    [property: JsonPropertyName("usage_total")] TranslationUsageSummary UsageTotal,
    [property: JsonPropertyName("images_processed_total")] int ImagesProcessedTotal);
