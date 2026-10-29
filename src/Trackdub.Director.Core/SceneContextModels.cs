using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Director (scene) context for one segment. Every field is nullable: absence is an
/// honest null, never a guessed default. Stage A (sam-3.1 segmentation) supplies
/// <see cref="Regions"/> only. Stage B1 vision enrichment (opt-in per segment, see
/// <see cref="SceneSegmentInput.VisionEnrichment"/>) may fill emotion/tone, scene
/// summary, speaker description, on-screen text, and burned-in text from the full
/// frame. Speaker identity is carried through only when the caller already knows
/// it; <see cref="SpeakerDescription"/> is appearance only and is never written to
/// <see cref="SpeakerId"/> or <see cref="SpeakerLabel"/>. OnScreenText holds text
/// physically present in the scene only; BurnedInText holds subtitle/caption and
/// watermark/logo text overlaid on the video. Neither lane overwrites the other's
/// fields: segmentation never writes enrichment fields and vision never writes
/// regions or speaker identity.
/// </summary>
public sealed record DirectorContext(
    [property: JsonPropertyName("speaker_id")] string? SpeakerId,
    [property: JsonPropertyName("speaker_label")] string? SpeakerLabel,
    [property: JsonPropertyName("emotion_tone_label")] string? EmotionToneLabel,
    [property: JsonPropertyName("on_screen_text")] string[]? OnScreenText,
    [property: JsonPropertyName("scene_summary")] string? SceneSummary,
    [property: JsonPropertyName("regions")] SceneRegion[]? Regions = null,
    [property: JsonPropertyName("speaker_description")] string? SpeakerDescription = null,
    [property: JsonPropertyName("burned_in_text")] string[]? BurnedInText = null);

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
/// Parsed: at least one concept produced a region, or vision enrichment was
/// requested and parsed. Empty: no region was found and no enrichment was
/// evidenced, or no frame/concepts were supplied so nothing was queried
/// (Regions null then, [] when queried and missed). Failed: malformed model
/// output, frame hash mismatch, or transport/API failure — Context is null,
/// never a defaulted all-null context.
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
/// FrameId and TimecodeSeconds identify the frame for provenance and pass through
/// to the result. FrameSha256, when supplied, is the caller's expected lowercase
/// hex SHA-256 of the frame bytes: the service computes the hash itself and a
/// mismatch (or an expected hash it cannot verify) fails the segment before any
/// live call. VisionEnrichment opts the segment into the Stage B1 vision pass
/// over the full frame; without it the segment follows Stage A behavior exactly.
/// The frame image itself is never stored on any result.
/// </summary>
public sealed record SceneSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("frame_image_data_url")] string? FrameImageDataUrl = null,
    [property: JsonPropertyName("concepts")] string[]? Concepts = null,
    [property: JsonPropertyName("speaker_id")] string? SpeakerId = null,
    [property: JsonPropertyName("speaker_label")] string? SpeakerLabel = null,
    [property: JsonPropertyName("frame_id")] string? FrameId = null,
    [property: JsonPropertyName("timecode_seconds")] double? TimecodeSeconds = null,
    [property: JsonPropertyName("frame_sha256")] string? FrameSha256 = null,
    [property: JsonPropertyName("vision_enrichment")] bool? VisionEnrichment = null);

/// <summary>
/// Provenance for the Stage B1 vision pass on one segment. ModelId is the model
/// the API reports as having served the call; RequestedModelId is what the lane
/// asked for (META_VISION_MODEL, default muse-spark-1.1). Present whenever a
/// vision call was attempted, including on Failed results; null when enrichment
/// was not requested or never attempted.
/// </summary>
public sealed record VisionProvenance(
    [property: JsonPropertyName("requested_model_id")] string RequestedModelId,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("endpoint_host")] string EndpointHost,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("prompt_template_version")] string PromptTemplateVersion);

/// <summary>
/// Full token usage for one vision call (or a batch total). ReasoningTokens and
/// CachedTokens mirror the API's usage detail objects and stay null when the API
/// does not provide them; they are never estimated.
/// </summary>
public sealed record VisionUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens = null,
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens = null);

/// <summary>
/// Per-segment scene result. Provenance/Usage/RawContent describe the Stage A
/// segmentation lane; VisionProvenance/VisionUsage/VisionRawContent describe the
/// Stage B1 vision lane and stay null when enrichment was not attempted.
/// FrameId/TimecodeSeconds pass through from the input; FrameSha256 is the hash
/// the service computed over the frame bytes (null when there is no frame or the
/// frame is not a base64 data URL whose bytes can be hashed locally).
/// </summary>
public sealed record SceneSegmentResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] SceneContextStatus Status,
    [property: JsonPropertyName("context")] DirectorContext? Context,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("provenance")] TranslationProvenance Provenance,
    [property: JsonPropertyName("usage")] TranslationUsage Usage,
    [property: JsonPropertyName("images_processed")] int ImagesProcessed,
    [property: JsonPropertyName("raw_content")] string? RawContent = null,
    [property: JsonPropertyName("frame_id")] string? FrameId = null,
    [property: JsonPropertyName("timecode_seconds")] double? TimecodeSeconds = null,
    [property: JsonPropertyName("frame_sha256")] string? FrameSha256 = null,
    [property: JsonPropertyName("vision_provenance")] VisionProvenance? VisionProvenance = null,
    [property: JsonPropertyName("vision_usage")] VisionUsage? VisionUsage = null,
    [property: JsonPropertyName("vision_raw_content")] string? VisionRawContent = null);

public sealed record SceneContextBatch(
    [property: JsonPropertyName("segments")] SceneSegmentResult[] Segments,
    [property: JsonPropertyName("usage_total")] TranslationUsageSummary UsageTotal,
    [property: JsonPropertyName("images_processed_total")] int ImagesProcessedTotal,
    [property: JsonPropertyName("vision_usage_total")] VisionUsage? VisionUsageTotal = null);
