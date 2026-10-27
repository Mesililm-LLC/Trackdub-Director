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

    public async Task<string> ChatCompletionAsync(
        IReadOnlyList<ChatMessage> messages,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ChatCompletionResult result = await ChatCompletionWithUsageAsync(messages, model, cancellationToken).ConfigureAwait(false);
        return result.Content;
    }

    public async Task<ChatCompletionResult> ChatCompletionWithUsageAsync(
        IReadOnlyList<ChatMessage> messages,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string apiKey = MetaAuth.GetApiKey();
        string baseUrl = MetaAuth.GetBaseUrl();
        string resolvedModel = string.IsNullOrWhiteSpace(model) ? MetaAuth.GetModel() : model.Trim();

        string? reasoningEffort = NormalizeReasoningEffort(MetaAuth.GetReasoningEffort());
        var payload = new ChatCompletionRequest(resolvedModel, messages.ToArray(), reasoningEffort);

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string url = $"{baseUrl}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

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

        string body;
        using (response)
        {
            body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string detail = body.Length > 500 ? body[..500] : body;
                throw new MetaApiException($"Meta Model API HTTP {(int)response.StatusCode}: {detail}");
            }
        }

        ChatCompletionResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse Meta Model API response: {body[..Math.Min(500, body.Length)]}", ex);
        }

        string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            string preview = body.Length > 500 ? body[..500] : body;
            throw new MetaApiException($"Unexpected Meta Model API response shape: {preview}");
        }

        string responseModel = parsed?.Model ?? resolvedModel;
        TokenUsage? usage = null;
        if (parsed?.Usage is not null)
        {
            usage = new TokenUsage(
                parsed.Usage.PromptTokens,
                parsed.Usage.CompletionTokens,
                parsed.Usage.TotalTokens);
        }

        return new ChatCompletionResult(content, responseModel, usage);
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

        return ChatCompletionAsync(messages, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Generic translate with usage, used by TranslationService.
    /// </summary>
    public Task<ChatCompletionResult> TranslateWithUsageAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        string sourceDisplay = LanguageDisplayName(sourceLanguage);
        string targetDisplay = LanguageDisplayName(targetLanguage);

        IReadOnlyList<ChatMessage> messages =
        [
            new ChatMessage("system", $"You are a translation engine. Translate the user's text from {sourceDisplay} to {targetDisplay}. Output only the translation, with no quotes, notes, or explanation."),
            new ChatMessage("user", text),
        ];

        return ChatCompletionWithUsageAsync(messages, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        string apiKey = MetaAuth.GetApiKey();
        string baseUrl = MetaAuth.GetBaseUrl().TrimEnd('/');
        string url = $"{baseUrl}/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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

        string body;
        using (response)
        {
            body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string detail = body.Length > 500 ? body[..500] : body;
                throw new MetaApiException($"Meta Model API HTTP {(int)response.StatusCode}: {detail}");
            }
        }

        ModelsResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ModelsResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse models response: {body[..Math.Min(500, body.Length)]}", ex);
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
    /// </summary>
    public Task<ChatCompletionResult> ReviseTranslationForTimingAsync(
        string sourceText,
        string currentTranslation,
        string sourceLanguage,
        string targetLanguage,
        double windowSeconds,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTranslation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        string sourceDisplay = LanguageDisplayName(sourceLanguage);
        string targetDisplay = LanguageDisplayName(targetLanguage);

        IReadOnlyList<ChatMessage> messages =
        [
            new ChatMessage("system", $"You are a translation engine. Revise the {targetDisplay} translation of the given {sourceDisplay} line so it preserves the meaning but fits a dubbing window of approximately {windowSeconds:0.0#} seconds. Keep the output at most {maxChars} characters. Output only the revised translation, with no quotes, notes, or explanation."),
            new ChatMessage("user", $"Original {sourceDisplay}: {sourceText}\nCurrent {targetDisplay} ({currentTranslation.Length} chars): {currentTranslation}\nRewrite it shorter, at most {maxChars} characters, preserving meaning."),
        ];

        return ChatCompletionWithUsageAsync(messages, cancellationToken: cancellationToken);
    }

    private static string LanguageDisplayName(string code)
    {
        return code.Trim().ToLowerInvariant() switch
        {
            "en" => "English",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "pt" => "Portuguese",
            "it" => "Italian",
            "ja" => "Japanese",
            "ko" => "Korean",
            "zh" => "Chinese",
            _ => code.Trim(),
        };
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
        [property: JsonPropertyName("total_tokens")] int TotalTokens);

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

    public sealed record TokenUsage(
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens);

    public sealed record ModelInfo(
        string Id,
        string Object,
        long Created,
        string OwnedBy);
}
