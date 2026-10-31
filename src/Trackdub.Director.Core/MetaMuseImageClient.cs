using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Muse Image edits via POST /v1/responses (probe-verified shape: one user
/// message with an input_text instruction plus an input_image data URL; the
/// image comes back as base64 in the image_generation_call item). Crops are
/// sent as PNG data URLs; returned bytes (WEBP in the probe) are decoded by
/// <see cref="WicImageDecoder"/>. Auth comes from <see cref="MetaAuth"/> and
/// keys are never logged.
/// </summary>
public sealed class MetaMuseImageClient : IMuseImageClient, IDisposable
{
    public const string DefaultImageModel = "muse-image-1.0";
    public const string ImageModelEnv = "META_IMAGE_MODEL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public MetaMuseImageClient(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
            _ownsClient = true;
        }
    }

    public static string GetImageModel()
    {
        string? raw = Environment.GetEnvironmentVariable(ImageModelEnv);
        return string.IsNullOrWhiteSpace(raw) ? DefaultImageModel : raw.Trim();
    }

    public async Task<MuseImageEditResult> EditAsync(RasterImage image, string instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);

        string model = GetImageModel();
        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(PngCodec.Encode(image));
        var payload = new ResponsesRequest(
            model,
            [new ResponsesInputItem("user",
                [new ResponsesContentPart("input_text", instruction.Trim(), null),
                 new ResponsesContentPart("input_image", null, dataUrl)])]);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MetaAuth.GetBaseUrl().TrimEnd('/')}/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetaAuth.GetApiKey());
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        string body = await SendForBodyAsync(request, cancellationToken).ConfigureAwait(false);
        return ParseResponse(body, model);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static MuseImageEditResult ParseResponse(string body, string requestedModel)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse Meta Model API response: {Truncate(body)}", ex);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new MetaApiException($"Unexpected Meta Model API response shape: {Truncate(body)}");
            }

            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                string message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "unknown error"
                    : "unknown error";
                throw new MetaApiException($"Meta Model API error: {message}");
            }

            string? status = root.TryGetProperty("status", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (status is not null && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                throw new MetaApiException($"Meta Model API response not completed (status: {status}).");
            }

            string model = root.TryGetProperty("model", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString() ?? requestedModel
                : requestedModel;
            string? responseId = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            byte[]? imageBytes = ExtractImageBytes(root);
            if (imageBytes is null || imageBytes.Length == 0)
            {
                throw new MetaApiException("Meta Model API response carried no image (missing image_generation_call result).");
            }

            RasterImage raster = WicImageDecoder.Decode(imageBytes);
            return new MuseImageEditResult(raster, model, ParseUsage(root), responseId);
        }
    }

    private static byte[]? ExtractImageBytes(JsonElement root)
    {
        if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "image_generation_call", StringComparison.Ordinal)
                && item.TryGetProperty("result", out JsonElement result)
                && result.ValueKind == JsonValueKind.String)
            {
                string? b64 = result.GetString();
                if (string.IsNullOrWhiteSpace(b64))
                {
                    return null;
                }

                try
                {
                    return Convert.FromBase64String(b64.Trim());
                }
                catch (FormatException ex)
                {
                    throw new MetaApiException("Meta Model API image result was not valid base64.", ex);
                }
            }
        }

        return null;
    }

    private static MetaClient.TokenUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new MetaClient.TokenUsage(
            GetInt(usage, "input_tokens"),
            GetInt(usage, "output_tokens"),
            GetInt(usage, "total_tokens"));
    }

    private static int GetInt(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            ? number
            : 0;
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
                throw new MetaApiException($"Meta Model API HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            return body;
        }
    }

    private static string Truncate(string body)
    {
        return body.Length > 500 ? body[..500] : body;
    }

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
}
