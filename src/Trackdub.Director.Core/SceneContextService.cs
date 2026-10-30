using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Trackdub.Director.Core;

/// <summary>
/// Scene-context lane (Director Mode Stages A + B1). For each segment, segments
/// the caller-supplied concept phrases in the segment's frame via
/// <see cref="MetaClient.SegmentConceptAsync"/>. When the caller opts in with
/// VisionEnrichment, a vision pass over the full frame via
/// <see cref="MetaClient.DescribeFrameAsync"/> (template scene-vision-v1) fills
/// the enrichment fields of the same <see cref="DirectorContext"/>; SAM regions
/// found first ride along as text context. Neither lane overwrites the other's
/// fields and fields no lane can evidence stay null.
/// </summary>
public sealed class SceneContextService
{
    /// <summary>Template "scene-v1": the concept phrase is sent verbatim as input_text.</summary>
    public const string PromptTemplateVersion = "scene-v1";

    /// <summary>Template "scene-vision-v1": the Stage B1 vision ask, ported verbatim from the probe that verified it. Do not reword without a new probe.</summary>
    public const string VisionPromptTemplateVersion = "scene-vision-v1";

    /// <summary>Verbatim scene-vision-v1 prompt text (single text part, sent exactly as written).</summary>
    public const string VisionPromptText = "Look at this video frame. Return strict JSON only, with no markdown fences and no commentary, using exactly these keys: scene_summary (one sentence describing the scene), emotion_tone_label (short label for the visible speaker's emotion or tone, or null if no speaker is visible), speaker_description (short phrase describing the visible speaker, or null if no speaker is visible), on_screen_text (array of exact text strings physically present in the scene only, such as signs and posters; do not include burned-in subtitles or captions, and do not include watermarks or logos), burned_in_text (array of exact text strings overlaid on the video only, namely burned-in subtitles or captions and watermarks or logos; do not include text physically present in the scene). Use an empty array when no text of that kind is visible. Transcribe text exactly as shown. Do not guess.";

    private readonly MetaClient _client;

    public SceneContextService(MetaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public SceneContextService()
        : this(new MetaClient())
    {
    }

    public async Task<SceneContextBatch> RunAsync(
        IReadOnlyList<SceneSegmentInput> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        var run = new SceneRunContext(GetEndpointHost(), MetaAuth.GetSceneModel(), MetaAuth.GetVisionModel());
        var results = new List<SceneSegmentResult>(segments.Count);

        foreach (SceneSegmentInput segment in segments)
        {
            results.Add(await ProcessSegmentAsync(segment, run, cancellationToken).ConfigureAwait(false));
        }

        return BuildBatch(results);
    }

    private static SceneContextBatch BuildBatch(List<SceneSegmentResult> results)
    {
        int prompt = 0;
        int completion = 0;
        int tokens = 0;
        int images = 0;
        int visionPrompt = 0;
        int visionCompletion = 0;
        int visionTokens = 0;
        int visionReasoning = 0;
        int visionCached = 0;
        bool anyVision = false;
        bool anyReasoning = false;
        bool anyCached = false;

        foreach (SceneSegmentResult result in results)
        {
            prompt += result.Usage.PromptTokens;
            completion += result.Usage.CompletionTokens;
            tokens += result.Usage.TotalTokens;
            images += result.ImagesProcessed;
            if (result.VisionUsage is not null)
            {
                anyVision = true;
                visionPrompt += result.VisionUsage.PromptTokens;
                visionCompletion += result.VisionUsage.CompletionTokens;
                visionTokens += result.VisionUsage.TotalTokens;
                if (result.VisionUsage.ReasoningTokens.HasValue)
                {
                    anyReasoning = true;
                    visionReasoning += result.VisionUsage.ReasoningTokens.Value;
                }

                if (result.VisionUsage.CachedTokens.HasValue)
                {
                    anyCached = true;
                    visionCached += result.VisionUsage.CachedTokens.Value;
                }
            }
        }

        VisionUsage? visionTotal = anyVision
            ? new VisionUsage(visionPrompt, visionCompletion, visionTokens, anyReasoning ? visionReasoning : null, anyCached ? visionCached : null)
            : null;
        return new SceneContextBatch(results.ToArray(), new TranslationUsageSummary(prompt, completion, tokens), images, visionTotal);
    }

    private async Task<SceneSegmentResult> ProcessSegmentAsync(
        SceneSegmentInput segment,
        SceneRunContext run,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.Id);

        var work = new SegmentWork(
            segment,
            run,
            NullIfBlank(segment.SpeakerId),
            NullIfBlank(segment.SpeakerLabel),
            NormalizeConcepts(segment.Concepts),
            NullIfBlank(segment.FrameId),
            segment.TimecodeSeconds,
            NullIfBlank(segment.FrameSha256),
            segment.VisionEnrichment == true);

        var accumulator = new SegmentationAccumulator(work.Run.ModelId);

        // Frame provenance is verified locally before any live call: a hash
        // mismatch fails the segment with zero model spend.
        string? frameError = ValidateFrameHash(work, accumulator);
        if (frameError is not null)
        {
            return accumulator.Failure(work, frameError);
        }

        if (string.IsNullOrWhiteSpace(segment.FrameImageDataUrl))
        {
            return accumulator.Empty(work);
        }

        SceneSegmentResult? segmentFailure = await AccumulateConceptsAsync(accumulator, work, cancellationToken).ConfigureAwait(false);
        if (segmentFailure is not null)
        {
            return segmentFailure;
        }

        if (work.VisionRequested)
        {
            SceneSegmentResult? visionFailure = await AccumulateVisionAsync(accumulator, work, cancellationToken).ConfigureAwait(false);
            if (visionFailure is not null)
            {
                return visionFailure;
            }
        }
        else if (work.Concepts.Length == 0)
        {
            // Frame but nothing to query: honest Empty with Regions null (unknown, not "none found").
            return accumulator.Empty(work);
        }

        return accumulator.Completed(work);
    }

    // Computes the frame hash locally and verifies it against the caller's
    // expected value. Returns an error string on any failure; the caller turns
    // it into a Failed result before any live call is made.
    private static string? ValidateFrameHash(SegmentWork work, SegmentationAccumulator accumulator)
    {
        string? frame = work.Segment.FrameImageDataUrl;
        if (!string.IsNullOrWhiteSpace(frame))
        {
            string? error = TryComputeFrameSha256(frame.Trim(), out string? computed);
            if (error is not null)
            {
                return error;
            }

            accumulator.ComputedFrameSha256 = computed;
        }

        if (work.ExpectedFrameSha256 is null)
        {
            return null;
        }

        string expected = work.ExpectedFrameSha256.ToLowerInvariant();
        if (accumulator.ComputedFrameSha256 is null)
        {
            return "Frame SHA-256 could not be verified: an expected frame_sha256 was supplied but the frame bytes are not available as a base64 data URL.";
        }

        return string.Equals(accumulator.ComputedFrameSha256, expected, StringComparison.Ordinal)
            ? null
            : $"Frame SHA-256 mismatch: expected {expected}, computed {accumulator.ComputedFrameSha256}.";
    }

    // Hashes the decoded bytes of a base64 data URL. https:// frames are not
    // fetched (the service never stores or downloads the image), so their hash
    // stays null; non-base64 data URLs likewise have no locally hashable bytes.
    private static string? TryComputeFrameSha256(string frameImageDataUrl, out string? computed)
    {
        computed = null;
        // Only data URLs carry local bytes; a remote URL that merely contains the
        // marker must never be "verified" against its own suffix.
        if (!frameImageDataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int marker = frameImageDataUrl.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(frameImageDataUrl[(marker + ";base64,".Length)..]);
        }
        catch (FormatException)
        {
            return "Frame data URL contains invalid base64; its SHA-256 could not be computed.";
        }

        computed = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return null;
    }

    private async Task<SceneSegmentResult?> AccumulateVisionAsync(
        SegmentationAccumulator accumulator,
        SegmentWork work,
        CancellationToken cancellationToken)
    {
        accumulator.BeginVision(work);

        MetaClient.VisionCompletionResult response;
        try
        {
            response = await _client.DescribeFrameAsync(
                new MetaClient.VisionRequest(work.Segment.FrameImageDataUrl!, VisionPromptText, accumulator.RegionsContextText(), Model: work.Run.VisionModelId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MetaApiException ex) when (!IsMissingKey(ex))
        {
            return accumulator.Failure(work, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return accumulator.Failure(work, ex.Message);
        }

        accumulator.AddVision(response);
        VisionParseResult parsed = SceneVisionParser.Parse(response.Content);
        if (parsed.IsMalformed)
        {
            return accumulator.Failure(work, parsed.Error ?? "Malformed vision response.");
        }

        accumulator.SetEnrichment(parsed.Enrichment!);
        return null;
    }

    private async Task<SceneSegmentResult?> AccumulateConceptsAsync(SegmentationAccumulator accumulator, SegmentWork work, CancellationToken cancellationToken)
    {
        

        foreach (string concept in work.Concepts)
        {
            SceneSegmentResult? failure = await AccumulateConceptAsync(accumulator, work, concept, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private async Task<SceneSegmentResult?> AccumulateConceptAsync(
        SegmentationAccumulator accumulator,
        SegmentWork work,
        string concept,
        CancellationToken cancellationToken)
    {
        MetaClient.SegmentationResponseResult response;
        try
        {
            response = await _client.SegmentConceptAsync(
                new MetaClient.SegmentationRequest(work.Segment.FrameImageDataUrl!, concept),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MetaApiException ex) when (!IsMissingKey(ex))
        {
            return accumulator.Failure(work, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return accumulator.Failure(work, ex.Message);
        }

        accumulator.Add(response);
        SegmentationParseResult parsed = SceneSegmentationParser.Parse(response.OutputText, concept);
        if (parsed.IsMalformed)
        {
            return accumulator.Failure(work, parsed.Error ?? "Malformed segmentation response.");
        }

        accumulator.AddRegions(parsed.Regions);
        return null;
    }

    private static bool IsMissingKey(MetaApiException ex)
    {
        return ex.Message.Contains("No Meta Model API key", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] NormalizeConcepts(string[]? concepts)
    {
        return (concepts ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToArray();
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string GetEndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return uri.Host;
        }

        return baseUrl;
    }

    private static TranslationProvenance Provenance(SceneRunContext run, string modelId)
    {
        return new TranslationProvenance(modelId, run.EndpointHost, DateTime.UtcNow.ToString("O"), PromptTemplateVersion);
    }

    private sealed record SceneRunContext(string EndpointHost, string ModelId, string VisionModelId);

    private sealed record SegmentWork(
        SceneSegmentInput Segment,
        SceneRunContext Run,
        string? SpeakerId,
        string? SpeakerLabel,
        string[] Concepts,
        string? FrameId,
        double? TimecodeSeconds,
        string? ExpectedFrameSha256,
        bool VisionRequested);

    private sealed class SegmentationAccumulator
    {
        private readonly List<SceneRegion> _regions = [];
        private readonly List<string> _rawParts = [];
        private string _modelId;
        private int _promptTokens;
        private int _completionTokens;
        private int _totalTokens;
        private int _imagesProcessed;
        private bool _visionAttempted;
        private bool _visionSucceeded;
        private string _visionRequestedModel = string.Empty;
        private string _visionReturnedModel = string.Empty;
        private string _visionTimestampUtc = string.Empty;
        private VisionUsage? _visionUsage;
        private string? _visionRawContent;
        private VisionEnrichment? _enrichment;

        public string? ComputedFrameSha256 { get; set; }

        public SegmentationAccumulator(string modelId)
        {
            _modelId = modelId;
        }

        public void Add(MetaClient.SegmentationResponseResult response)
        {
            _modelId = response.Model;
            int prompt = response.Usage?.PromptTokens ?? 0;
            int completion = response.Usage?.CompletionTokens ?? 0;
            _promptTokens += prompt;
            _completionTokens += completion;
            _totalTokens += response.Usage?.TotalTokens ?? (prompt + completion);
            _imagesProcessed += response.ImagesProcessed;
            _rawParts.Add(response.OutputText);
        }

        public void AddRegions(IEnumerable<SceneRegion> regions)
        {
            _regions.AddRange(regions);
        }

        public SceneSegmentResult Failure(SegmentWork work, string error)
        {
            string trimmed = error.Length > 500 ? error[..500] : error;
            return new SceneSegmentResult(
                work.Segment.Id,
                SceneContextStatus.Failed,
                Context: null,
                trimmed,
                Provenance(work.Run, _modelId),
                Usage(),
                _imagesProcessed,
                RawOrNull(),
                work.FrameId,
                work.TimecodeSeconds,
                ComputedFrameSha256,
                VisionProvenanceOrNull(work),
                _visionUsage,
                _visionRawContent);
        }

        public SceneSegmentResult Completed(SegmentWork work)
        {
            var context = new DirectorContext(
                work.SpeakerId,
                work.SpeakerLabel,
                EmotionToneLabel: _enrichment?.EmotionToneLabel,
                OnScreenText: _enrichment?.OnScreenText,
                SceneSummary: _enrichment?.SceneSummary,
                Regions: work.Concepts.Length > 0 ? _regions.ToArray() : null,
                SpeakerDescription: _enrichment?.SpeakerDescription,
                BurnedInText: _enrichment?.BurnedInText);

            return new SceneSegmentResult(
                work.Segment.Id,
                _regions.Count > 0 || _visionSucceeded ? SceneContextStatus.Parsed : SceneContextStatus.Empty,
                context,
                Error: null,
                Provenance(work.Run, _modelId),
                Usage(),
                _imagesProcessed,
                RawOrNull(),
                work.FrameId,
                work.TimecodeSeconds,
                ComputedFrameSha256,
                VisionProvenanceOrNull(work),
                _visionUsage,
                _visionRawContent);
        }

        public SceneSegmentResult Empty(SegmentWork work)
        {
            return new SceneSegmentResult(
                work.Segment.Id,
                SceneContextStatus.Empty,
                new DirectorContext(work.SpeakerId, work.SpeakerLabel, null, null, null, Regions: null),
                Error: null,
                Provenance(work.Run, work.Run.ModelId),
                new TranslationUsage(0, 0, 0),
                ImagesProcessed: 0,
                RawContent: null,
                work.FrameId,
                work.TimecodeSeconds,
                ComputedFrameSha256);
        }

        public void BeginVision(SegmentWork work)
        {
            _visionAttempted = true;
            _visionRequestedModel = work.Run.VisionModelId;
            _visionReturnedModel = work.Run.VisionModelId;
            _visionTimestampUtc = DateTime.UtcNow.ToString("O");
        }

        public void AddVision(MetaClient.VisionCompletionResult response)
        {
            _visionRequestedModel = response.RequestedModel;
            _visionReturnedModel = response.Model;
            _visionRawContent = response.Content;
            _visionUsage = response.Usage is null
                ? null
                : new VisionUsage(
                    response.Usage.PromptTokens,
                    response.Usage.CompletionTokens,
                    response.Usage.TotalTokens,
                    response.Usage.ReasoningTokens,
                    response.Usage.CachedTokens);
        }

        public void SetEnrichment(VisionEnrichment enrichment)
        {
            _enrichment = enrichment;
            _visionSucceeded = true;
        }

        // SAM regions ride along as plain text context only; mask payloads are
        // never sent to the vision model and regions never alter the template.
        public string? RegionsContextText()
        {
            if (_regions.Count == 0)
            {
                return null;
            }

            const int maxRegions = 20;
            string joined = string.Join("; ", _regions.Take(maxRegions).Select(r => FormattableString.Invariant(
                $"{r.Concept} box ({r.X1},{r.Y1})-({r.X2},{r.Y2}) in {r.FrameWidth}x{r.FrameHeight}")));
            string suffix = _regions.Count > maxRegions
                ? FormattableString.Invariant($"; and {_regions.Count - maxRegions} more (not shown)")
                : string.Empty;
            return $"Segmentation regions already found in this frame: {joined}{suffix}.";
        }

        private VisionProvenance? VisionProvenanceOrNull(SegmentWork work)
        {
            return _visionAttempted
                ? new VisionProvenance(_visionRequestedModel, _visionReturnedModel, work.Run.EndpointHost, _visionTimestampUtc, VisionPromptTemplateVersion)
                : null;
        }

        private TranslationUsage Usage()
        {
            return new TranslationUsage(_promptTokens, _completionTokens, _totalTokens);
        }

        private string? RawOrNull()
        {
            return _rawParts.Count > 0 ? string.Join("\n", _rawParts) : null;
        }
    }
}

public sealed record SegmentationParseResult(bool IsMalformed, SceneRegion[] Regions, string? Error);

/// <summary>
/// Parser for the probe-verified sam-3.1 output_text token format, e.g.
/// "&lt;0f&gt;0&lt;|box;x1=10;y1=10;x2=29;y2=49;w=64;h=64|&gt;&lt;|mask;x=0;y=0;data=40,20,...|&gt;".
/// Empty text is a concept miss (no regions, not malformed). Non-empty text with
/// no parseable box token, or a box/mask token with missing or invalid fields,
/// is malformed — callers must report it as Failed, never default it.
/// </summary>
public static class SceneSegmentationParser
{
    private static readonly Regex BoxToken = new("<\\|box;([^|]*)\\|>", RegexOptions.Compiled);
    private static readonly Regex MaskToken = new("<\\|mask;([^|]*)\\|>", RegexOptions.Compiled);

    public static SegmentationParseResult Parse(string? rawText, string concept)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(concept);

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new SegmentationParseResult(false, [], null);
        }

        return new ParserRun(new TokenDocument(rawText.Trim(), concept.Trim())).Parse();
    }

    private sealed record TokenDocument(string Raw, string Concept);

    private sealed record TokenBody(string Value, int Limit = 200);

    private readonly record struct BoxBounds(int X1, int Y1, int X2, int Y2, int W, int H);

    private readonly record struct MaskFields(int? X, int? Y, string? Data, string? Error);

    private readonly record struct TokenPosition(int Position, int MatchIndex);

    private sealed class ParserRun
    {
        private readonly TokenDocument _document;
        private readonly MatchCollection _boxes;
        private readonly MatchCollection _masks;
        private readonly List<TokenPosition> _boxPositions;
        private readonly List<TokenPosition> _maskPositions;
        private readonly HashSet<int> _associatedMasks = new();

        public ParserRun(TokenDocument document)
        {
            _document = document;
            _boxes = BoxToken.Matches(document.Raw);
            _masks = MaskToken.Matches(document.Raw);
            _boxPositions = PositionsOf(_boxes);
            _maskPositions = PositionsOf(_masks);
        }

        public SegmentationParseResult Parse()
        {
            if (_boxes.Count == 0)
            {
                return Malformed($"Segmentation response for concept '{_document.Concept}' contained no box token: {Truncate(new TokenBody(_document.Raw))}");
            }

            var regions = new List<SceneRegion>(_boxes.Count);
            for (int i = 0; i < _boxPositions.Count; i++)
            {
                string? regionError = TryAddRegion(i, regions);
                if (regionError is not null)
                {
                    return Malformed(regionError);
                }
            }

            string? maskError = ValidateUnassociatedMasks();
            if (maskError is not null)
            {
                return Malformed(maskError);
            }

            return new SegmentationParseResult(false, regions.ToArray(), null);
        }

        // Adds the region for box i. Every mask inside the box's span is claimed; the first one supplies the region's mask.
        private string? TryAddRegion(int boxIndex, List<SceneRegion> regions)
        {
            string? boxError = TryParseBox(new TokenBody(_boxes[_boxPositions[boxIndex].MatchIndex].Groups[1].Value), out BoxBounds box);
            if (boxError is not null)
            {
                return $"Malformed box token for concept '{_document.Concept}': {boxError}";
            }

            IReadOnlyList<int> maskIndices = MaskIndicesWithin(boxIndex);
            _associatedMasks.UnionWith(maskIndices);

            // Every mask inside the span must be well formed, not just the first one that supplies the region's mask.
            MaskFields mask = default;
            for (int i = 0; i < maskIndices.Count; i++)
            {
                string? maskError = TryParseMask(new TokenBody(_masks[maskIndices[i]].Groups[1].Value), out MaskFields parsed);
                if (maskError is not null)
                {
                    return $"Malformed mask token for concept '{_document.Concept}': {maskError}";
                }

                if (i == 0)
                {
                    mask = parsed;
                }
            }

            regions.Add(new SceneRegion(_document.Concept, box.X1, box.Y1, box.X2, box.Y2, box.W, box.H, mask.X, mask.Y, mask.Data));
            return null;
        }

        // Masks that sit inside no box's span are still validated, so a malformed one is reported rather than dropped.
        private string? ValidateUnassociatedMasks()
        {
            foreach (TokenPosition mask in _maskPositions)
            {
                if (_associatedMasks.Contains(mask.MatchIndex))
                {
                    continue;
                }

                string? maskError = TryParseMask(new TokenBody(_masks[mask.MatchIndex].Groups[1].Value), out _);
                if (maskError is not null)
                {
                    return $"Malformed mask token for concept '{_document.Concept}': {maskError}";
                }
            }

            return null;
        }

        // Indices of the masks positioned after box i and before the next box.
        private IReadOnlyList<int> MaskIndicesWithin(int boxIndex)
        {
            int boxPos = _boxPositions[boxIndex].Position;
            int nextBoxPos = boxIndex + 1 < _boxPositions.Count ? _boxPositions[boxIndex + 1].Position : int.MaxValue;
            return _maskPositions
                .Where(m => m.Position > boxPos && m.Position < nextBoxPos)
                .Select(m => m.MatchIndex)
                .ToArray();
        }

        private static List<TokenPosition> PositionsOf(MatchCollection matches)
        {
            var positions = new List<TokenPosition>(matches.Count);
            for (int i = 0; i < matches.Count; i++)
            {
                positions.Add(new TokenPosition(matches[i].Index, i));
            }

            return positions;
        }

        private static string? TryParseBox(TokenBody body, out BoxBounds box)
        {
            box = default;
            Dictionary<string, string> fields = ParseFields(body);
            string[] required = ["x1", "y1", "x2", "y2", "w", "h"];
            var values = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string name in required)
            {
                if (!fields.TryGetValue(name, out string? rawValue)
                    || !int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    return $"missing or non-integer '{name}' in '{Truncate(body)}'";
                }

                values[name] = value;
            }

            var candidate = new BoxBounds(values["x1"], values["y1"], values["x2"], values["y2"], values["w"], values["h"]);
            if (!IsValid(candidate))
            {
                return $"invalid box geometry in '{Truncate(body)}'";
            }

            box = candidate;
            return null;
        }

        private static bool IsValid(BoxBounds box)
        {
            return box.W > 0
                && box.H > 0
                && box.X1 >= 0
                && box.Y1 >= 0
                && box.X2 >= box.X1
                && box.Y2 >= box.Y1
                && box.X2 <= box.W
                && box.Y2 <= box.H;
        }

        private static string? TryParseMask(TokenBody body, out MaskFields mask)
        {
            mask = default;

            int dataIndex = FindDataField(body.Value);
            if (dataIndex < 0)
            {
                return $"missing 'data=' in '{Truncate(body)}'";
            }

            // Mask data is opaque and may itself contain ';' or '=' — take the remainder verbatim.
            string data = body.Value[(dataIndex + "data=".Length)..];
            if (string.IsNullOrEmpty(data))
            {
                return "empty mask data";
            }

            Dictionary<string, string> fields = ParseFields(new TokenBody(body.Value[..dataIndex]));
            if (!TryParseCoordinate(fields, "x", out int? x))
            {
                return $"non-integer mask 'x' in '{Truncate(body)}'";
            }

            if (!TryParseCoordinate(fields, "y", out int? y))
            {
                return $"non-integer mask 'y' in '{Truncate(body)}'";
            }

            mask = new MaskFields(x, y, data, null);
            return null;
        }

        // Finds the "data=" field itself, not a key that merely ends in it (such as "metadata=").
        private static int FindDataField(string value)
        {
            for (int i = value.IndexOf("data=", StringComparison.Ordinal); i >= 0; i = value.IndexOf("data=", i + 1, StringComparison.Ordinal))
            {
                if (i == 0 || value[i - 1] == ';')
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TryParseCoordinate(Dictionary<string, string> fields, string name, out int? value)
        {
            value = null;
            if (!fields.TryGetValue(name, out string? raw))
            {
                return true;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        private static Dictionary<string, string> ParseFields(TokenBody body)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in body.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    fields[part[..eq].Trim()] = part[(eq + 1)..].Trim();
                }
            }

            return fields;
        }

        private static string Truncate(TokenBody body)
        {
            return body.Value.Length <= body.Limit ? body.Value : body.Value[..(body.Limit - 3)] + "...";
        }

        private static SegmentationParseResult Malformed(string error)
        {
            return new SegmentationParseResult(true, [], error);
        }
    }
}
