using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Translates a batch of segments through <see cref="MetaClient"/>.
/// Mirrors the pattern in Trackdub's Translation/OpenAiCloudTranslationEngine.cs
/// (JSON-array prompt, per-segment handling) but via the Meta Model API.
/// No code is copied; the shape is adapted for this repo's file-based batch flow.
/// </summary>
public sealed class TranslationService
{
    public const string PromptTemplateVersion = "v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly MetaClient _client;

    public TranslationService(MetaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public TranslationService()
        : this(new MetaClient())
    {
    }

    public async Task<IReadOnlyList<TranslationSegmentInput>> LoadSegmentsAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        string json = await File.ReadAllTextAsync(inputPath, cancellationToken).ConfigureAwait(false);

        TranslationSegmentInput[]? segments;
        try
        {
            segments = JsonSerializer.Deserialize<TranslationSegmentInput[]>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid segments JSON in '{inputPath}': {ex.Message}", ex);
        }

        if (segments is null)
        {
            throw new InvalidOperationException($"Empty or null JSON in '{inputPath}'. Expected an array of {{id, text}}.");
        }

        for (int i = 0; i < segments.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(segments[i].Id))
            {
                throw new InvalidOperationException($"Segment at index {i} has no id.");
            }

            if (segments[i].Text is null)
            {
                throw new InvalidOperationException($"Segment '{segments[i].Id}' has no text.");
            }
        }

        return segments;
    }

    public async Task<TranslationBatchOutput> TranslateBatchAsync(
        IReadOnlyList<TranslationSegmentInput> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        string endpointHost = GetEndpointHost();
        var outputs = new List<TranslatedSegmentOutput>(segments.Count);
        int totalPrompt = 0;
        int totalCompletion = 0;
        int totalTokens = 0;

        foreach (TranslationSegmentInput segment in segments)
        {
            MetaClient.ChatCompletionResult result = await _client.TranslateWithUsageAsync(
                segment.Text,
                sourceLanguage,
                targetLanguage,
                cancellationToken).ConfigureAwait(false);

            int promptTokens = result.Usage?.PromptTokens ?? 0;
            int completionTokens = result.Usage?.CompletionTokens ?? 0;
            int segTotal = result.Usage?.TotalTokens ?? (promptTokens + completionTokens);

            totalPrompt += promptTokens;
            totalCompletion += completionTokens;
            totalTokens += segTotal;

            var provenance = new TranslationProvenance(
                result.Model,
                endpointHost,
                DateTime.UtcNow.ToString("O"),
                PromptTemplateVersion);

            var usage = new TranslationUsage(promptTokens, completionTokens, segTotal);

            outputs.Add(new TranslatedSegmentOutput(
                segment.Id,
                segment.Text,
                result.Content,
                provenance,
                usage));
        }

        var usageTotal = new TranslationUsageSummary(totalPrompt, totalCompletion, totalTokens);

        return new TranslationBatchOutput(sourceLanguage, targetLanguage, outputs.ToArray(), usageTotal);
    }

    public async Task WriteOutputAsync(
        string outputPath,
        TranslationBatchOutput batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(batch);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(batch, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
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
