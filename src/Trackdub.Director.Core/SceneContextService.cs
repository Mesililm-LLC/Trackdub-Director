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

        string endpointHost = GetEndpointHost();
        var results = new List<SceneSegmentResult>(segments.Count);
        int totalPrompt = 0;
        int totalCompletion = 0;
        int totalTokens = 0;
        int totalImages = 0;

        foreach (SceneSegmentInput segment in segments)
        {
            SceneSegmentResult result = await ProcessSegmentAsync(segment, endpointHost, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            totalPrompt += result.Usage.PromptTokens;
            totalCompletion += result.Usage.CompletionTokens;
            totalTokens += result.Usage.TotalTokens;
            totalImages += result.ImagesProcessed;
        }

        return new SceneContextBatch(
            results.ToArray(),
            new TranslationUsageSummary(totalPrompt, totalCompletion, totalTokens),
            totalImages);
    }

    private async Task<SceneSegmentResult> ProcessSegmentAsync(
        SceneSegmentInput segment,
        string endpointHost,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.Id);

        string? speakerId = NullIfBlank(segment.SpeakerId);
        string? speakerLabel = NullIfBlank(segment.SpeakerLabel);
        string[] concepts = (segment.Concepts ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToArray();

        TranslationProvenance Provenance(string modelId) => new(
            modelId, endpointHost, DateTime.UtcNow.ToString("O"), PromptTemplateVersion);

        if (string.IsNullOrWhiteSpace(segment.FrameImageDataUrl) || concepts.Length == 0)
        {
            // Nothing to query: honest Empty with Regions null (unknown, not "none found").
            return new SceneSegmentResult(
                segment.Id,
                SceneContextStatus.Empty,
                new DirectorContext(speakerId, speakerLabel, null, null, null, Regions: null),
                Error: null,
                Provenance(MetaAuth.GetSceneModel()),
                new TranslationUsage(0, 0, 0),
                ImagesProcessed: 0,
                RawContent: null);
        }

        var regions = new List<SceneRegion>();
        var rawParts = new List<string>();
        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        int imagesProcessed = 0;
        string modelId = MetaAuth.GetSceneModel();

        // Local builder so Failed results keep the provenance/usage accumulated so far.
        SceneSegmentResult Failed(string error, string? raw) => new(
            segment.Id,
            SceneContextStatus.Failed,
            Context: null,
            error.Length > 500 ? error[..500] : error,
            Provenance(modelId),
            new TranslationUsage(promptTokens, completionTokens, totalTokens),
            imagesProcessed,
            raw);

        foreach (string concept in concepts)
        {
            MetaClient.SegmentationResponseResult response;
            try
            {
                response = await _client.SegmentConceptAsync(
                    segment.FrameImageDataUrl!, concept, model: null, cancellationToken).ConfigureAwait(false);
            }
            catch (MetaApiException ex) when (!ex.Message.Contains("No Meta Model API key", StringComparison.OrdinalIgnoreCase))
            {
                return Failed(ex.Message, rawParts.Count > 0 ? string.Join("\n", rawParts) : null);
            }
            catch (HttpRequestException ex)
            {
                return Failed(ex.Message, rawParts.Count > 0 ? string.Join("\n", rawParts) : null);
            }

            modelId = response.Model;
            promptTokens += response.Usage?.PromptTokens ?? 0;
            completionTokens += response.Usage?.CompletionTokens ?? 0;
            totalTokens += response.Usage?.TotalTokens ?? ((response.Usage?.PromptTokens ?? 0) + (response.Usage?.CompletionTokens ?? 0));
            imagesProcessed += response.ImagesProcessed;
            rawParts.Add(response.OutputText);

            SegmentationParseResult parsed = SceneSegmentationParser.Parse(response.OutputText, concept);
            if (parsed.IsMalformed)
            {
                return Failed(parsed.Error ?? "Malformed segmentation response.", response.OutputText);
            }

            regions.AddRange(parsed.Regions);
        }

        var context = new DirectorContext(
            speakerId,
            speakerLabel,
            EmotionToneLabel: null,
            OnScreenText: null,
            SceneSummary: null,
            Regions: regions.ToArray());

        return new SceneSegmentResult(
            segment.Id,
            regions.Count > 0 ? SceneContextStatus.Parsed : SceneContextStatus.Empty,
            context,
            Error: null,
            Provenance(modelId),
            new TranslationUsage(promptTokens, completionTokens, totalTokens),
            imagesProcessed,
            string.Join("\n", rawParts));
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

        string raw = rawText.Trim();
        MatchCollection boxes = BoxToken.Matches(raw);
        if (boxes.Count == 0)
        {
            return Malformed($"Segmentation response for concept '{concept}' contained no box token: {Truncate(raw, 200)}");
        }

        MatchCollection masks = MaskToken.Matches(raw);
        var regions = new List<SceneRegion>(boxes.Count);

        for (int i = 0; i < boxes.Count; i++)
        {
            if (!TryParseBox(boxes[i].Groups[1].Value, out (int X1, int Y1, int X2, int Y2, int W, int H) box, out string? boxError))
            {
                return Malformed($"Malformed box token for concept '{concept}': {boxError}");
            }

            int? maskX = null;
            int? maskY = null;
            string? maskData = null;
            if (i < masks.Count)
            {
                if (!TryParseMask(masks[i].Groups[1].Value, out maskX, out maskY, out maskData, out string? maskError))
                {
                    return Malformed($"Malformed mask token for concept '{concept}': {maskError}");
                }
            }

            regions.Add(new SceneRegion(concept.Trim(), box.X1, box.Y1, box.X2, box.Y2, box.W, box.H, maskX, maskY, maskData));
        }

        return new SegmentationParseResult(false, regions.ToArray(), null);
    }

    private static SegmentationParseResult Malformed(string error) => new(true, [], error);

    private static bool TryParseBox(
        string body,
        out (int X1, int Y1, int X2, int Y2, int W, int H) box,
        out string? error)
    {
        box = default;
        error = null;
        Dictionary<string, string> fields = ParseFields(body);

        string[] required = ["x1", "y1", "x2", "y2", "w", "h"];
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in required)
        {
            if (!fields.TryGetValue(name, out string? rawValue)
                || !int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                error = $"missing or non-integer '{name}' in '{Truncate(body, 120)}'";
                return false;
            }

            values[name] = value;
        }

        box = (values["x1"], values["y1"], values["x2"], values["y2"], values["w"], values["h"]);
        if (box.W <= 0 || box.H <= 0 || box.X1 < 0 || box.Y1 < 0 || box.X2 < box.X1 || box.Y2 < box.Y1)
        {
            error = $"invalid box geometry in '{Truncate(body, 120)}'";
            return false;
        }

        return true;
    }

    private static bool TryParseMask(string body, out int? maskX, out int? maskY, out string? maskData, out string? error)
    {
        maskX = null;
        maskY = null;
        maskData = null;
        error = null;

        int dataIndex = body.IndexOf("data=", StringComparison.Ordinal);
        if (dataIndex < 0)
        {
            error = $"missing 'data=' in '{Truncate(body, 120)}'";
            return false;
        }

        // Mask data is opaque and may itself contain ';' or '=' — take the remainder verbatim.
        maskData = body[(dataIndex + "data=".Length)..];
        if (string.IsNullOrEmpty(maskData))
        {
            error = "empty mask data";
            return false;
        }

        Dictionary<string, string> fields = ParseFields(body[..dataIndex]);
        if (fields.TryGetValue("x", out string? xRaw))
        {
            if (!int.TryParse(xRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x))
            {
                error = $"non-integer mask 'x' in '{Truncate(body, 120)}'";
                return false;
            }

            maskX = x;
        }

        if (fields.TryGetValue("y", out string? yRaw))
        {
            if (!int.TryParse(yRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                error = $"non-integer mask 'y' in '{Truncate(body, 120)}'";
                return false;
            }

            maskY = y;
        }

        return true;
    }

    private static Dictionary<string, string> ParseFields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                fields[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }

        return fields;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";
}
