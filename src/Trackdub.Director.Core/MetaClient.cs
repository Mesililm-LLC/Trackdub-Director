using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Minimal Meta Model API client. Uses HttpClient + System.Text.Json only, no
/// third-party packages. Env-only auth via <see cref="MetaAuth"/>.
/// </summary>
public sealed class MetaClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["es"] = "Spanish",
            ["fr"] = "French",
            ["de"] = "German",
            ["pt"] = "Portuguese",
            ["it"] = "Italian",
            ["ja"] = "Japanese",
            ["ko"] = "Korean",
            ["zh"] = "Chinese",
        };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public MetaClient(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _ownsClient = true;
        }
    }

    public async Task<string> ChatCompletionAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ChatCompletionResult result = await ChatCompletionWithUsageAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Content;
    }

    public async Task<ChatCompletionResult> ChatCompletionWithUsageAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string resolvedModel = ResolveModel(request.Model);
        string body = await SendChatAsync(resolvedModel, request.Messages, cancellationToken).ConfigureAwait(false);
        ChatCompletionResponse? parsed = ParseChatResponse(new BodyText(body));
        string content = ExtractContent(parsed, new BodyText(body));
        return new ChatCompletionResult(content, parsed?.Model ?? resolvedModel, ToUsage(parsed?.Usage));
    }

    private async Task<string> SendChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        string? reasoningEffort = NormalizeReasoningEffort(MetaAuth.GetReasoningEffort());
        var payload = new ChatCompletionRequest(model, messages.ToArray(), reasoningEffort);
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"{MetaAuth.GetBaseUrl()}/chat/completions",
            JsonSerializer.Serialize(payload, JsonOptions));
        return await SendForBodyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static ChatCompletionResponse? ParseChatResponse(BodyText body)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatCompletionResponse>(body.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse Meta Model API response: {Truncate(body)}", ex);
        }
    }

    private static string ExtractContent(ChatCompletionResponse? parsed, BodyText body)
    {
        string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new MetaApiException($"Unexpected Meta Model API response shape: {Truncate(body)}");
        }

        return content;
    }

    private static TokenUsage? ToUsage(UsageDto? usage)
    {
        return usage is null ? null : new TokenUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);
    }

    private static TokenUsageDetail? ToUsageDetail(UsageDto? usage)
    {
        return usage is null
            ? null
            : new TokenUsageDetail(
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                usage.CompletionTokensDetails?.ReasoningTokens,
                usage.PromptTokensDetails?.CachedTokens);
    }

    /// <summary>
    /// Translate one line from English to Spanish via the Meta Model API.
    /// </summary>
    public Task<string> TranslateToSpanishAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        IReadOnlyList<ChatMessage> messages =
        [
            new ChatMessage("system", "You are a translation engine. Translate the user's text from English to Spanish. Output only the translation, with no quotes, notes, or explanation."),
            new ChatMessage("user", text),
        ];

        return ChatCompletionAsync(new ChatRequest(messages), cancellationToken);
    }

    /// <summary>
    /// Generic translate with usage, used by TranslationService.
    /// </summary>
    public Task<ChatCompletionResult> TranslateWithUsageAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetLanguage);

        IReadOnlyList<ChatMessage> messages =
        [
            new ChatMessage("system", $"You are a translation engine. Translate the user's text from {LanguageDisplayName(request.SourceLanguage)} to {LanguageDisplayName(request.TargetLanguage)}. Output only the translation, with no quotes, notes, or explanation."),
            new ChatMessage("user", request.Text),
        ];

        return ChatCompletionWithUsageAsync(new ChatRequest(messages), cancellationToken);
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"{MetaAuth.GetBaseUrl().TrimEnd('/')}/models",
            json: null);
        string body = await SendForBodyAsync(request, cancellationToken).ConfigureAwait(false);

        ModelsResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ModelsResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse models response: {Truncate(new BodyText(body))}", ex);
        }

        if (parsed?.Data is null)
        {
            return [];
        }

        return parsed.Data.Select(m => new ModelInfo(m.Id, m.Object, m.Created, m.OwnedBy)).ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static string? NormalizeReasoningEffort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string v = raw.Trim().ToLowerInvariant();
        return v is "low" or "medium" or "high" ? v : null;
    }

    /// <summary>
    /// Revision call for the QA bounce-back: shorten a translation to fit a timing window.
    /// Preserves meaning; instructs the model to keep the output under maxChars.
    /// When MeaningIssues is provided, they are injected as feedback to fix.
    /// </summary>
    public Task<ChatCompletionResult> ReviseTranslationForTimingAsync(TimingRevisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRevisionRequest(request);

        string sourceDisplay = LanguageDisplayName(request.SourceLanguage);
        string targetDisplay = LanguageDisplayName(request.TargetLanguage);
        string issuesLine = BuildIssuesLine(request.MeaningIssues);
        string revisionInstruction = string.IsNullOrEmpty(issuesLine)
            ? $"Rewrite it shorter, at most {request.MaxChars} characters, preserving meaning."
            : $"Fix the listed meaning issues and restore lost meaning, at most {request.MaxChars} characters, without shortening further than necessary.";

        IReadOnlyList<ChatMessage> messages =
        [
            new ChatMessage("system", $"You are a translation engine. Revise the {targetDisplay} translation of the given {sourceDisplay} line so it preserves the meaning but fits a dubbing window of approximately {request.WindowSeconds:0.0#} seconds. Keep the output at most {request.MaxChars} characters. Output only the revised translation, with no quotes, notes, or explanation."),
            new ChatMessage("user", $"Original {sourceDisplay}: {request.SourceText}\nCurrent {targetDisplay} ({request.CurrentTranslation.Length} chars): {request.CurrentTranslation}\n{revisionInstruction}{issuesLine}"),
        ];

        return ChatCompletionWithUsageAsync(new ChatRequest(messages), cancellationToken);
    }

    private static void ValidateRevisionRequest(TimingRevisionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CurrentTranslation);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetLanguage);
    }

    private static string BuildIssuesLine(IReadOnlyList<string>? meaningIssues)
    {
        if (meaningIssues is null || meaningIssues.Count == 0)
        {
            return string.Empty;
        }

        string joined = string.Join("; ", meaningIssues.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        return string.IsNullOrWhiteSpace(joined) ? string.Empty : $"\nMaterial meaning issues to fix (restore the lost meaning): {joined}";
    }

    /// <summary>
    /// Meaning-fidelity check via the Meta API. Prompt-only JSON with resilient parser (exercised path).
    /// </summary>
    public async Task<MeaningCheckResult> CheckMeaningAsync(MeaningCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMeaningRequest(request);

        IReadOnlyList<ChatMessage> messages =
        [
            new ChatMessage("system", MeaningGate.BuildSystemPrompt()),
            new ChatMessage("user", MeaningGate.BuildUserPrompt(
                request.SourceText,
                request.CandidateTranslation,
                LanguageDisplayName(request.SourceLanguage),
                LanguageDisplayName(request.TargetLanguage))),
        ];

        ChatCompletionResult raw = await ChatCompletionWithUsageAsync(new ChatRequest(messages), cancellationToken).ConfigureAwait(false);
        return MeaningGate.ParseAndNormalize(raw.Content, raw.Model, raw.Usage, GetEndpointHostForUsage());
    }

    private static void ValidateMeaningRequest(MeaningCheckRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateTranslation);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetLanguage);
    }

    /// <summary>
    /// Segment one concept phrase in one frame via POST /v1/responses (probe-verified:
    /// sam-3.1 is not served on /chat/completions). The concept is sent verbatim as the
    /// input_text part; a generic instruction returns empty output_text. A concept hit
    /// returns output_text carrying box/mask tokens; a miss returns empty output_text,
    /// which is a valid result here, not an error.
    /// </summary>
    public async Task<SegmentationResponseResult> SegmentConceptAsync(SegmentationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string imageUrl = ValidateImageUrl(request.FrameImageDataUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Concept);

        string resolvedModel = string.IsNullOrWhiteSpace(request.Model) ? MetaAuth.GetSceneModel() : request.Model.Trim();
        var payload = new ResponsesRequest(
            resolvedModel,
            [new ResponsesInputItem("user",
                [new ResponsesContentPart("input_text", request.Concept.Trim(), null),
                 new ResponsesContentPart("input_image", null, imageUrl)])]);

        using HttpRequestMessage httpRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"{MetaAuth.GetBaseUrl().TrimEnd('/')}/responses",
            JsonSerializer.Serialize(payload, JsonOptions));
        string body = await SendForBodyAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        return SegmentationResponseParser.Parse(new SegmentationBody(new BodyText(body), new ModelId(resolvedModel)));
    }

    /// <summary>
    /// Describe one frame via POST /v1/chat/completions (Stage B1 vision enrichment,
    /// probe-verified with template scene-vision-v1). One user message: the prompt
    /// text part first, an optional segmentation-regions text part, then the
    /// OpenAI-style image_url part carrying the full frame. The vision lane always
    /// sends reasoning_effort "low", matching the probe that verified this shape.
    /// Returns the raw assistant content unparsed; callers own JSON validation and
    /// must fail closed on malformed output.
    /// </summary>
    public async Task<VisionCompletionResult> DescribeFrameAsync(VisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string imageUrl = ValidateImageUrl(request.FrameImageDataUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PromptText);

        string resolvedModel = string.IsNullOrWhiteSpace(request.Model) ? MetaAuth.GetVisionModel() : request.Model.Trim();
        var parts = new List<VisionContentPart>
        {
            new("text", request.PromptText, null),
        };
        if (!string.IsNullOrWhiteSpace(request.RegionsContextText))
        {
            parts.Add(new VisionContentPart("text", request.RegionsContextText.Trim(), null));
        }

        parts.Add(new VisionContentPart("image_url", null, new VisionImageUrl(imageUrl)));
        var payload = new VisionChatRequest(resolvedModel, [new VisionChatMessage("user", parts.ToArray())], "low");

        using HttpRequestMessage httpRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"{MetaAuth.GetBaseUrl().TrimEnd('/')}/chat/completions",
            JsonSerializer.Serialize(payload, JsonOptions));
        string body = await SendForBodyAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        ChatCompletionResponse? parsed = ParseChatResponse(new BodyText(body));
        // Vision callers own content validation and fail closed in their parser;
        // blank content must not discard usage the API has already billed.
        string content = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;
        return new VisionCompletionResult(content, resolvedModel, parsed?.Model ?? resolvedModel, ToUsageDetail(parsed?.Usage));
    }

    /// <summary>
    /// Adapt one existing translation to a target register via POST /v1/chat/completions
    /// (Director Mode Stage B2, template director-v1). System and user prompt text are
    /// built by the caller (DirectorAdaptationService owns the template). This lane
    /// always sends reasoning_effort "low" and the default chat model (muse-spark-1.1).
    /// Returns the raw assistant content unparsed; callers own line validation and must
    /// fail closed on malformed output. Blank content does not discard billed usage.
    /// </summary>
    public async Task<AdaptationCompletionResult> AdaptRegisterAsync(AdaptationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);

        string resolvedModel = string.IsNullOrWhiteSpace(request.Model) ? MetaAuth.GetModel() : request.Model.Trim();
        var payload = new ChatCompletionRequest(
            resolvedModel,
            [new ChatMessage("system", request.SystemPrompt), new ChatMessage("user", request.UserPrompt)],
            "low");

        using HttpRequestMessage httpRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"{MetaAuth.GetBaseUrl().TrimEnd('/')}/chat/completions",
            JsonSerializer.Serialize(payload, JsonOptions));
        string body = await SendForBodyAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        ChatCompletionResponse? parsed = ParseChatResponse(new BodyText(body));
        ChatChoice? firstChoice = parsed?.Choices?.FirstOrDefault();
        string content = firstChoice?.Message?.Content?.Trim() ?? string.Empty;
        return new AdaptationCompletionResult(
            content, resolvedModel, parsed?.Model ?? resolvedModel, ToUsageDetail(parsed?.Usage), firstChoice?.FinishReason);
    }

    private static string ValidateImageUrl(string frameImageDataUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameImageDataUrl);
        string imageUrl = frameImageDataUrl.Trim();
        if (!imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            && !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Frame image must be a data:image/ URL or an https:// URL.", nameof(frameImageDataUrl));
        }

        return imageUrl;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string? json)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetaAuth.GetApiKey());
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task<string> SendForBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MetaApiException($"Meta Model API request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetaApiException("Meta Model API request timed out.", ex);
        }

        using (response)
        {
            string body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new MetaApiException($"Meta Model API HTTP {(int)response.StatusCode}: {Truncate(new BodyText(body))}");
            }

            return body;
        }
    }

    private static string ResolveModel(string? model)
    {
        return string.IsNullOrWhiteSpace(model) ? MetaAuth.GetModel() : model.Trim();
    }

    private static string GetEndpointHostForUsage()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return uri.Host;
        }

        return baseUrl;
    }

    internal static string LanguageDisplayName(string code)
    {
        string trimmed = code.Trim();
        return LanguageNames.TryGetValue(trimmed, out string? name) ? name : trimmed;
    }

    private static string Truncate(BodyText body)
    {
        return body.Value.Length > 500 ? body.Value[..500] : body.Value;
    }

    private sealed record BodyText(string Value);

    private sealed record ModelId(string Value);

    private sealed record FieldName(string Value);

    private sealed record SegmentationBody(BodyText Body, ModelId Model);

    private sealed record UsageResult(TokenUsage? Usage, int ImagesProcessed);

    private static class SegmentationResponseParser
    {
        public static SegmentationResponseResult Parse(SegmentationBody body)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body.Body.Value);
            }
            catch (JsonException ex)
            {
                throw new MetaApiException($"Failed to parse Meta Model API response: {Truncate(body.Body)}", ex);
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new MetaApiException($"Unexpected Meta Model API response shape: {Truncate(body.Body)}");
                }

                string? error = ErrorMessage(root);
                if (error is not null)
                {
                    throw new MetaApiException($"Meta Model API error: {error}");
                }

                string? status = Status(root);
                if (status is not null && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MetaApiException($"Meta Model API response not completed (status: {status}).");
                }

                UsageResult usage = Usage(root);
                return new SegmentationResponseResult(
                    string.Concat(TextParts(root)),
                    ResponseModel(root, body.Model),
                    usage.Usage,
                    usage.ImagesProcessed,
                    ResponseId(root));
            }
        }

        private static string? ErrorMessage(JsonElement root)
        {
            if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? "unknown error"
                : "unknown error";
        }

        private static string? Status(JsonElement root)
        {
            return root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }

        private static string ResponseModel(JsonElement root, ModelId fallback)
        {
            return root.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String
                ? model.GetString() ?? fallback.Value
                : fallback.Value;
        }

        private static string? ResponseId(JsonElement root)
        {
            return root.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }

        private static IEnumerable<string> TextParts(JsonElement root)
        {
            if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
            {
                throw new MetaApiException("Unexpected Meta Model API response shape: missing or non-array 'output'.");
            }

            var parts = new List<string>();
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new MetaApiException("Unexpected Meta Model API output item shape.");
                }

                foreach (JsonElement part in ContentParts(item))
                {
                    if (part.ValueKind != JsonValueKind.Object)
                    {
                        throw new MetaApiException("Unexpected Meta Model API output content shape.");
                    }

                    if (IsOutputText(part))
                    {
                        parts.Add(part.GetProperty("text").GetString() ?? string.Empty);
                    }
                }
            }

            // A concept miss is represented by an explicit empty output_text part.
            // A response with no such part is malformed, not a miss.
            if (parts.Count == 0)
            {
                throw new MetaApiException("Unexpected Meta Model API response shape: missing output_text part.");
            }

            return parts;
        }

        private static IEnumerable<JsonElement> ContentParts(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new MetaApiException("Unexpected Meta Model API output item shape.");
            }

            if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return content.EnumerateArray();
        }

        private static bool IsOutputText(JsonElement part)
        {
            return part.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "output_text", StringComparison.Ordinal)
                && part.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String;
        }

        private static UsageResult Usage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return new UsageResult(null, 0);
            }

            return new UsageResult(
                new TokenUsage(
                    GetInt(usage, new FieldName("input_tokens")),
                    GetInt(usage, new FieldName("output_tokens")),
                    GetInt(usage, new FieldName("total_tokens"))),
                GetInt(usage, new FieldName("images_processed")));
        }

        private static int GetInt(JsonElement obj, FieldName name)
        {
            return obj.TryGetProperty(name.Value, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number)
                ? number
                : 0;
        }
    }

    // Wire format

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null);

    public sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("choices")] ChatChoice[]? Choices,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("usage")] UsageDto? Usage,
        [property: JsonPropertyName("created")] long Created = 0,
        [property: JsonPropertyName("object")] string? Object = null);

    private sealed record UsageDto(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens,
        [property: JsonPropertyName("completion_tokens_details")] CompletionTokensDetailsDto? CompletionTokensDetails = null,
        [property: JsonPropertyName("prompt_tokens_details")] PromptTokensDetailsDto? PromptTokensDetails = null);

    private sealed record CompletionTokensDetailsDto(
        [property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens = null);

    private sealed record PromptTokensDetailsDto(
        [property: JsonPropertyName("cached_tokens")] int? CachedTokens = null);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("index")] int Index = 0,
        [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

    private sealed record ModelsResponse(
        [property: JsonPropertyName("object")] string? Object,
        [property: JsonPropertyName("data")] ModelDto[]? Data);

    private sealed record ModelDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("created")] long Created,
        [property: JsonPropertyName("owned_by")] string OwnedBy);

    public sealed record ChatCompletionResult(
        string Content,
        string Model,
        TokenUsage? Usage);

    /// <summary>Token usage with API detail fields; detail values stay null when the API omits them.</summary>
    public sealed record TokenUsageDetail(
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        int? ReasoningTokens,
        int? CachedTokens);

    public sealed record TokenUsage(
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens);

    private sealed record ResponsesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] ResponsesInputItem[] Input);

    private sealed record ResponsesInputItem(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] ResponsesContentPart[] Content);

    private sealed record ResponsesContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("image_url")] string? ImageUrl);

    /// <summary>Raw /v1/responses segmentation outcome. OutputText is empty on a concept miss.</summary>
    public sealed record SegmentationResponseResult(
        string OutputText,
        string Model,
        TokenUsage? Usage,
        int ImagesProcessed,
        string? ResponseId);

    public sealed record ModelInfo(
        string Id,
        string Object,
        long Created,
        string OwnedBy);

    public sealed record ChatRequest(
        IReadOnlyList<ChatMessage> Messages,
        string? Model = null);

    public sealed record TranslationRequest(
        string Text,
        string SourceLanguage,
        string TargetLanguage);

    public sealed record TimingRevisionRequest(
        string SourceText,
        string CurrentTranslation,
        string SourceLanguage,
        string TargetLanguage,
        double WindowSeconds,
        int MaxChars,
        IReadOnlyList<string>? MeaningIssues = null);

    public sealed record MeaningCheckRequest(
        string SourceText,
        string CandidateTranslation,
        string SourceLanguage,
        string TargetLanguage);

    private sealed record VisionChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] VisionChatMessage[] Messages,
        [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null);

    private sealed record VisionChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] VisionContentPart[] Content);

    private sealed record VisionContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("image_url")] VisionImageUrl? ImageUrl);

    private sealed record VisionImageUrl(
        [property: JsonPropertyName("url")] string Url);

    /// <summary>Raw vision outcome. Content is the unparsed assistant message; Model is the API-reported model.</summary>
    public sealed record VisionCompletionResult(
        string Content,
        string RequestedModel,
        string Model,
        TokenUsageDetail? Usage);

    public sealed record SegmentationRequest(
        string FrameImageDataUrl,
        string Concept,
        string? Model = null);

    public sealed record VisionRequest(
        string FrameImageDataUrl,
        string PromptText,
        string? RegionsContextText = null,
        string? Model = null);

    /// <summary>Raw adaptation outcome. Content is the unparsed assistant message; Model is the API-reported model.</summary>
    public sealed record AdaptationCompletionResult(
        string Content,
        string RequestedModel,
        string Model,
        TokenUsageDetail? Usage,
        string? FinishReason = null);

    public sealed record AdaptationRequest(
        string SystemPrompt,
        string UserPrompt,
        string? Model = null);
}
