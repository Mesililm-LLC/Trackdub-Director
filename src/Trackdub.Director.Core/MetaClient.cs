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
        ArgumentNullException.ThrowIfNull(messages);

        string apiKey = MetaAuth.GetApiKey();
        string baseUrl = MetaAuth.GetBaseUrl();
        string resolvedModel = string.IsNullOrWhiteSpace(model) ? MetaAuth.GetModel() : model.Trim();

        var payload = new ChatCompletionRequest(resolvedModel, messages.ToArray());

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

        return content;
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

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    // Wire format

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages);

    public sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] ChatChoice[]? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}
