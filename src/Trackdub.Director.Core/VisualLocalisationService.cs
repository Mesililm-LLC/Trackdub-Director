namespace Trackdub.Director.Core;

/// <summary>
/// Frame-level visual localisation by crop-edit-composite. The sign box plus
/// padding is cropped, edited by the image model (template image-edit-v1: the
/// probe's working instruction, verbatim), resampled back to the crop size,
/// and only the exact box interior is pasted into a copy of the source — so
/// pixels outside the box provably come from the source, never from model
/// output. The outside-box diff is still computed and stored (0 by
/// construction). Any missing/undecodable/undersized edit fails the segment
/// with the reason recorded; an unmodified frame is never passed off as
/// localised.
/// </summary>
public sealed class VisualLocalisationService
{
    /// <summary>Template "image-edit-v1": the probe's working instruction with source/target filled in.</summary>
    public const string PromptTemplateVersion = "image-edit-v1";

    /// <summary>Context padding (pixels) around the box sent to the model; only the box interior is composited back.</summary>
    public const int CropPaddingPixels = 20;

    private readonly IMuseImageClient _client;

    public VisualLocalisationService(IMuseImageClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public VisualLocalisationService()
        : this(new MetaMuseImageClient())
    {
    }

    public static string BuildInstruction(string sourceText, string targetText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetText);
        return $"replace the text \"{sourceText.Trim()}\" on the shop door sign with \"{targetText.Trim()}\", preserve everything else in the image exactly";
    }

    /// <summary>
    /// Localise one decoded frame. <paramref name="imageCalls"/> reports how
    /// many model calls this segment consumed (0 when validation rejected the
    /// box before any call).
    /// </summary>
    public async Task<VisualLocalisationSegmentResult> LocaliseRasterAsync(
        string segmentId,
        RasterImage frame,
        LocalisationBox box,
        string sourceText,
        string targetText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(box);
        string endpointHost = GetEndpointHost();
        int imageCalls = 0;

        VisualLocalisationSegmentResult Failure(string error, string modelId, MetaClient.TokenUsage? usage = null) => new(
            segmentId,
            VisualLocalisationStatus.Failed,
            LocalisedImageDataUrl: null,
            box,
            sourceText,
            targetText,
            OutsideBoxDiff: null,
            Truncate(error),
            new VisualLocalisationProvenance(modelId, endpointHost, DateTime.UtcNow.ToString("O"), PromptTemplateVersion),
            ToUsage(usage),
            imageCalls);

        string? boxError = ValidateBox(box, frame);
        if (boxError is not null)
        {
            return Failure(boxError, MetaMuseImageClient.GetImageModel());
        }

        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(targetText))
        {
            return Failure("Source and target text are required.", MetaMuseImageClient.GetImageModel());
        }

        int cropX = Math.Max(0, box.X1 - CropPaddingPixels);
        int cropY = Math.Max(0, box.Y1 - CropPaddingPixels);
        int cropX2 = Math.Min(frame.Width, box.X2 + CropPaddingPixels);
        int cropY2 = Math.Min(frame.Height, box.Y2 + CropPaddingPixels);
        RasterImage crop = frame.Crop(cropX, cropY, cropX2 - cropX, cropY2 - cropY);

        MuseImageEditResult edit;
        imageCalls++;
        try
        {
            edit = await _client.EditAsync(crop, BuildInstruction(sourceText, targetText), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MetaApiException or HttpRequestException or InvalidOperationException)
        {
            return Failure(ex.Message, MetaMuseImageClient.GetImageModel());
        }

        int boxWidth = box.X2 - box.X1;
        int boxHeight = box.Y2 - box.Y1;
        if (edit.Image.Width < boxWidth || edit.Image.Height < boxHeight)
        {
            return Failure(
                $"Edited image is {edit.Image.Width}x{edit.Image.Height}, smaller than the target box {boxWidth}x{boxHeight}.",
                edit.Model,
                edit.Usage);
        }

        RasterImage resized = edit.Image.ResizeBilinear(crop.Width, crop.Height);
        RasterImage patch = resized.Crop(box.X1 - cropX, box.Y1 - cropY, boxWidth, boxHeight);
        if (patch.Rgb.AsSpan().SequenceEqual(frame.Crop(box.X1, box.Y1, boxWidth, boxHeight).Rgb))
        {
            return Failure("Edited image did not change the target box.", edit.Model, edit.Usage);
        }

        RasterImage localised = frame.Paste(patch, box.X1, box.Y1);
        int outsideDiff = localised.CountDiffOutsideBox(frame, box.X1, box.Y1, box.X2, box.Y2);

        byte[] png = PngCodec.Encode(localised);
        return new VisualLocalisationSegmentResult(
            segmentId,
            VisualLocalisationStatus.Localised,
            "data:image/png;base64," + Convert.ToBase64String(png),
            box,
            sourceText.Trim(),
            targetText.Trim(),
            outsideDiff,
            Error: null,
            new VisualLocalisationProvenance(edit.Model, endpointHost, DateTime.UtcNow.ToString("O"), PromptTemplateVersion),
            ToUsage(edit.Usage),
            imageCalls,
            png);
    }

    public async Task<VisualLocalisationBatch> RunAsync(
        IReadOnlyList<VisualLocalisationSegmentInput> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        var results = new List<VisualLocalisationSegmentResult>(segments.Count);
        foreach (VisualLocalisationSegmentInput segment in segments)
        {
            results.Add(await ProcessInputAsync(segment, cancellationToken).ConfigureAwait(false));
        }

        int prompt = results.Sum(r => r.Usage.PromptTokens);
        int completion = results.Sum(r => r.Usage.CompletionTokens);
        int total = results.Sum(r => r.Usage.TotalTokens);
        return new VisualLocalisationBatch(
            results.ToArray(),
            new TranslationUsageSummary(prompt, completion, total),
            results.Sum(r => r.ImageCalls));
    }

    private async Task<VisualLocalisationSegmentResult> ProcessInputAsync(
        VisualLocalisationSegmentInput segment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.Id);
        string endpointHost = GetEndpointHost();

        VisualLocalisationSegmentResult Failure(string error) => new(
            segment.Id,
            VisualLocalisationStatus.Failed,
            LocalisedImageDataUrl: null,
            segment.Box,
            segment.SourceText,
            segment.TargetText,
            OutsideBoxDiff: null,
            Truncate(error),
            new VisualLocalisationProvenance(MetaMuseImageClient.GetImageModel(), endpointHost, DateTime.UtcNow.ToString("O"), PromptTemplateVersion),
            new TranslationUsage(0, 0, 0),
            ImageCalls: 0);

        if (segment.Box is null || string.IsNullOrWhiteSpace(segment.SourceText) || string.IsNullOrWhiteSpace(segment.TargetText))
        {
            return Failure("Each segment needs a box plus source_text and target_text.");
        }

        if (!TryDecodeDataUrl(segment.FrameImageDataUrl, out byte[]? frameBytes, out string? decodeError))
        {
            return Failure(decodeError!);
        }

        RasterImage frame;
        try
        {
            frame = WicImageDecoder.Decode(frameBytes!);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        return await LocaliseRasterAsync(segment.Id, frame, segment.Box, segment.SourceText!, segment.TargetText!, cancellationToken).ConfigureAwait(false);
    }

    private static string? ValidateBox(LocalisationBox box, RasterImage frame)
    {
        if (box.X1 < 0 || box.Y1 < 0 || box.X2 <= box.X1 || box.Y2 <= box.Y1 || box.X2 > frame.Width || box.Y2 > frame.Height)
        {
            return $"Box ({box.X1},{box.Y1})-({box.X2},{box.Y2}) does not lie inside the {frame.Width}x{frame.Height} frame.";
        }

        return null;
    }

    private static bool TryDecodeDataUrl(string? dataUrl, out byte[]? bytes, out string? error)
    {
        bytes = null;
        error = null;
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            error = "Frame image is required (data:image data URL).";
            return false;
        }

        string trimmed = dataUrl.Trim();
        int marker = trimmed.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (!trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || marker < 0)
        {
            error = "Frame image must be a base64 data:image data URL.";
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(trimmed[(marker + ";base64,".Length)..]);
            return true;
        }
        catch (FormatException)
        {
            error = "Frame data URL contains invalid base64.";
            return false;
        }
    }

    private static TranslationUsage ToUsage(MetaClient.TokenUsage? usage)
    {
        return new TranslationUsage(
            usage?.PromptTokens ?? 0,
            usage?.CompletionTokens ?? 0,
            usage?.TotalTokens ?? ((usage?.PromptTokens ?? 0) + (usage?.CompletionTokens ?? 0)));
    }

    private static string GetEndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : baseUrl;
    }

    private static string Truncate(string message)
    {
        return message.Length > 500 ? message[..500] : message;
    }
}
