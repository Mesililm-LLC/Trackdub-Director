namespace Trackdub.Director.Core;

/// <summary>One edited raster plus the model usage that produced it.</summary>
public sealed record MuseImageEditResult(
    RasterImage Image,
    string Model,
    MetaClient.TokenUsage? Usage,
    string? ResponseId);

/// <summary>
/// Image-edit seam for visual localisation. The service works in decoded
/// rasters; implementations own wire encoding/decoding so tests can fake the
/// model with deterministic rasters and asserted call counts.
/// </summary>
public interface IMuseImageClient
{
    /// <summary>Edit one crop raster per the instruction. Throws when the model returns no usable image.</summary>
    Task<MuseImageEditResult> EditAsync(RasterImage image, string instruction, CancellationToken cancellationToken = default);
}
