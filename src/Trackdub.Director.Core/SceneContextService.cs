using System.Globalization;
using System.Text.RegularExpressions;

namespace Trackdub.Director.Core;

/// <summary>
/// Scene-context lane (Director Mode Stage A). For each segment, segments the
/// caller-supplied concept phrases in the segment's frame via
/// <see cref="MetaClient.SegmentConceptAsync"/> and returns a
/// <see cref="DirectorContext"/> whose only evidence-backed content is the
/// parsed regions. Fields sam-3.1 cannot evidence stay null.
/// </summary>
public sealed class SceneContextService
{
    /// <summary>Template "scene-v1": the concept phrase is sent verbatim as input_text.</summary>
    public const string PromptTemplateVersion = "scene-v1";

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

        var run = new SceneRunContext(GetEndpointHost(), MetaAuth.GetSceneModel());
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

        foreach (SceneSegmentResult result in results)
        {
            prompt += result.Usage.PromptTokens;
            completion += result.Usage.CompletionTokens;
            tokens += result.Usage.TotalTokens;
            images += result.ImagesProcessed;
        }

        return new SceneContextBatch(results.ToArray(), new TranslationUsageSummary(prompt, completion, tokens), images);
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
            NormalizeConcepts(segment.Concepts));

        if (string.IsNullOrWhiteSpace(segment.FrameImageDataUrl) || work.Concepts.Length == 0)
        {
            // Nothing to query: honest Empty with Regions null (unknown, not "none found").
            return EmptyResult(work);
        }

        return await SegmentConceptsAsync(work, cancellationToken).ConfigureAwait(false);
    }

    private static SceneSegmentResult EmptyResult(SegmentWork work)
    {
        return new SceneSegmentResult(
            work.Segment.Id,
            SceneContextStatus.Empty,
            new DirectorContext(work.SpeakerId, work.SpeakerLabel, null, null, null, Regions: null),
            Error: null,
            Provenance(work.Run, work.Run.ModelId),
            new TranslationUsage(0, 0, 0),
            ImagesProcessed: 0,
            RawContent: null);
    }

    private async Task<SceneSegmentResult> SegmentConceptsAsync(SegmentWork work, CancellationToken cancellationToken)
    {
        var accumulator = new SegmentationAccumulator(work.Run.ModelId);

        foreach (string concept in work.Concepts)
        {
            SceneSegmentResult? failure = await AccumulateConceptAsync(accumulator, work, concept, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }
        }

        return accumulator.Parsed(work);
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

    private sealed record SceneRunContext(string EndpointHost, string ModelId);

    private sealed record SegmentWork(
        SceneSegmentInput Segment,
        SceneRunContext Run,
        string? SpeakerId,
        string? SpeakerLabel,
        string[] Concepts);

    private sealed class SegmentationAccumulator
    {
        private readonly List<SceneRegion> _regions = [];
        private readonly List<string> _rawParts = [];
        private string _modelId;
        private int _promptTokens;
        private int _completionTokens;
        private int _totalTokens;
        private int _imagesProcessed;

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
                RawOrNull());
        }

        public SceneSegmentResult Parsed(SegmentWork work)
        {
            var context = new DirectorContext(
                work.SpeakerId,
                work.SpeakerLabel,
                EmotionToneLabel: null,
                OnScreenText: null,
                SceneSummary: null,
                Regions: _regions.ToArray());

            return new SceneSegmentResult(
                work.Segment.Id,
                _regions.Count > 0 ? SceneContextStatus.Parsed : SceneContextStatus.Empty,
                context,
                Error: null,
                Provenance(work.Run, _modelId),
                Usage(),
                _imagesProcessed,
                string.Join("\n", _rawParts));
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

    private sealed class ParserRun
    {
        private readonly TokenDocument _document;

        public ParserRun(TokenDocument document)
        {
            _document = document;
        }

        public SegmentationParseResult Parse()
        {
            MatchCollection boxes = BoxToken.Matches(_document.Raw);
            if (boxes.Count == 0)
            {
                return Malformed($"Segmentation response for concept '{_document.Concept}' contained no box token: {Truncate(new TokenBody(_document.Raw))}");
            }

            MatchCollection masks = MaskToken.Matches(_document.Raw);
            var regions = new List<SceneRegion>(boxes.Count);
            // Build a position-ordered list of (tokenType, matchIndex) to associate each mask
            // with the box it follows, not by parallel array index.
            var boxPositions = new List<(int Position, int MatchIndex)>(boxes.Count);
            for (int i = 0; i < boxes.Count; i++)
            for (int i = 0; i < boxes.Count; i++)
                boxPositions.Add((boxes[i].Index, i));
                regions.Add(new SceneRegion(_document.Concept, box.X1, box.Y1, box.X2, box.Y2, box.W, box.H, mask.X, mask.Y, mask.Data));
            }
            var maskPositions = new List<(int Position, int MatchIndex)>(masks.Count);
            for (int i = 0; i < masks.Count; i++)
            {
                maskPositions.Add((masks[i].Index, i));
            }

            // For each box, find the mask that immediately follows it (before the next box).
            for (int i = 0; i < boxPositions.Count; i++)
            {
                int boxPos = boxPositions[i].Position;
                int nextBoxPos = i + 1 < boxPositions.Count ? boxPositions[i + 1].Position : int.MaxValue;

                string? boxError = TryParseBox(new TokenBody(boxes[boxPositions[i].MatchIndex].Groups[1].Value), out BoxBounds box);
                if (boxError is not null)
                {
                    return Malformed($"Malformed box token for concept '{_document.Concept}': {boxError}");
                }

                // Find the first mask whose position is after this box and before the next box.
                int? associatedMaskIndex = null;
                foreach (var (mPos, mIdx) in maskPositions)
                {
                    if (mPos > boxPos && mPos < nextBoxPos)
                    {
                        associatedMaskIndex = mIdx;
                        break;
                    }
                }

                MaskFields mask = default;
                if (associatedMaskIndex.HasValue)
                {
                    string? maskError = TryParseMask(new TokenBody(masks[associatedMaskIndex.Value].Groups[1].Value), out mask);
                    if (maskError is not null)
                    {
                        return Malformed($"Malformed mask token for concept '{_document.Concept}': {maskError}");
                    }
                }

                regions.Add(new SceneRegion(_document.Concept, box.X1, box.Y1, box.X2, box.Y2, box.W, box.H, mask.X, mask.Y, mask.Data));
            }

            // Validate any mask tokens that are not associated with a box (e.g., after the last box
            // where no next-box boundary caps them, or masks before all boxes).
            foreach (var (mPos, mIdx) in maskPositions)
            {
                bool associated = false;
                for (int i = 0; i < boxPositions.Count; i++)
                {
                    int boxPos = boxPositions[i].Position;
                    int nextBoxPos = i + 1 < boxPositions.Count ? boxPositions[i + 1].Position : int.MaxValue;
                    if (mPos > boxPos && mPos < nextBoxPos)
                    {
                        associated = true;
                        break;
                    }
                }

                if (!associated)
                {
                    // Validate the unassociated mask token — if malformed, report it.
                    string? maskError = TryParseMask(new TokenBody(masks[mIdx].Groups[1].Value), out _);
                    if (maskError is not null)
                    {
                        return Malformed($"Malformed mask token for concept '{_document.Concept}': {maskError}");
                    }
                }
            }

            return new SegmentationParseResult(false, regions.ToArray(), null);
            return new SegmentationParseResult(false, regions.ToArray(), null);
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

            int dataIndex = body.Value.IndexOf("data=", StringComparison.Ordinal);
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
