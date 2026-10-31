using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StaticShotStatus
{
    /// <summary>Gate passed and the composite was written.</summary>
    Composited,

    /// <summary>Gate passed; no output was requested (measure only).</summary>
    Passed,

    /// <summary>At least one frame failed the gate (or none were in the window); nothing was written.</summary>
    Refused,
}

/// <summary>Box-region comparison of one frame: mean SSIM over all 8x8 blocks and the largest single-block mean-luma shift (0-255).</summary>
public sealed record BoxSsim(double Mean, double MaxBlockLumaShift);

/// <summary>One frame's static-shot measurement against the reference frame.</summary>
public sealed record StaticShotFrame(
    [property: JsonPropertyName("frame_index")] int FrameIndex,
    [property: JsonPropertyName("time_seconds")] double TimeSeconds,
    [property: JsonPropertyName("ssim")] double Ssim,
    [property: JsonPropertyName("max_block_luma_shift")] double MaxBlockLumaShift,
    [property: JsonPropertyName("pass")] bool Pass);

/// <summary>Shot window in seconds on the video's own timeline (start inclusive, end exclusive).</summary>
public sealed record StaticShotWindow(
    [property: JsonPropertyName("start_seconds")] double StartSeconds,
    [property: JsonPropertyName("end_seconds")] double EndSeconds)
{
    public bool Contains(double timeSeconds) => timeSeconds >= StartSeconds && timeSeconds < EndSeconds;
}

/// <summary>
/// Report for one video-rate sign composite. A Refused report never has an
/// output file: the gate measures the whole shot before compositing anything.
/// </summary>
public sealed record StaticShotReport(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] StaticShotStatus Status,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("video")] string Video,
    [property: JsonPropertyName("reference_frame")] string ReferenceFrame,
    [property: JsonPropertyName("localised_frame")] string LocalisedFrame,
    [property: JsonPropertyName("box")] LocalisationBox Box,
    [property: JsonPropertyName("window")] StaticShotWindow Window,
    [property: JsonPropertyName("fps")] double Fps,
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("min_ssim_threshold")] double MinSsimThreshold,
    [property: JsonPropertyName("max_block_luma_shift_threshold")] double MaxBlockLumaShiftThreshold,
    [property: JsonPropertyName("frames_checked")] int FramesChecked,
    [property: JsonPropertyName("ssim_min")] double? SsimMin,
    [property: JsonPropertyName("ssim_min_frame")] int? SsimMinFrame,
    [property: JsonPropertyName("ssim_mean")] double? SsimMean,
    [property: JsonPropertyName("block_luma_shift_max")] double? BlockLumaShiftMax,
    [property: JsonPropertyName("block_luma_shift_max_frame")] int? BlockLumaShiftMaxFrame,
    [property: JsonPropertyName("first_failing_frame")] StaticShotFrame? FirstFailingFrame,
    [property: JsonPropertyName("frames_composited")] int FramesComposited,
    [property: JsonPropertyName("outside_box_diff_max")] int? OutsideBoxDiffMax,
    [property: JsonPropertyName("image_calls")] int ImageCalls,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("notes")] string[] Notes,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("frames")] StaticShotFrame[] Frames);
