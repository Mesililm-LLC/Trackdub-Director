using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// ElevenLabs text-to-speech via plain HttpClient (no third-party packages).
/// Requests raw PCM output (<c>pcm_16000</c>: mono 16-bit at 16 kHz) so take
/// durations are computed from the audio bytes themselves, never estimated.
/// Text-only calls: no voice cloning and no reference audio of any kind.
/// </summary>
public sealed class ElevenLabsTtsProvider : ITtsProvider, IDisposable
{
    /// <summary>Multilingual model used for Spanish synthesis.</summary>
    public const string DefaultModelId = "eleven_multilingual_v2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _modelId;

    public ElevenLabsTtsProvider(HttpClient? httpClient = null, string? modelId = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _ownsClient = true;
        }

        _modelId = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim();
    }

    public int PcmSampleRate => 16000;

    public int PcmChannels => 1;

    public int PcmBitsPerSample => 16;

    public string ModelId => _modelId;

    public void EnsureConfigured()
    {
        _ = ElevenLabsAuth.GetApiKey();
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ElevenLabsAuth.GetBaseUrl()}/voices");
        AddAuth(request);
        byte[] body = await SendForBytesAsync(request, cancellationToken).ConfigureAwait(false);
        return ParseVoices(body);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(string voiceId, string text, string languageCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        string url = $"{ElevenLabsAuth.GetBaseUrl()}/text-to-speech/{Uri.EscapeDataString(voiceId.Trim())}?output_format=pcm_16000";
        var payload = new SynthRequest(text, _modelId, languageCode.Trim());
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuth(request);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        byte[] audio = await SendForBytesAsync(request, cancellationToken).ConfigureAwait(false);
        if (audio.Length == 0)
        {
            throw new TtsApiException("ElevenLabs synthesis returned no audio bytes.");
        }

        return new TtsSynthesisResult(audio, text.Length);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static void AddAuth(HttpRequestMessage request)
    {
        request.Headers.Add("xi-api-key", ElevenLabsAuth.GetApiKey());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<byte[]> SendForBytesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TtsApiException($"ElevenLabs API request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TtsApiException("ElevenLabs API request timed out.", ex);
        }

        using (response)
        {
            byte[] body = response.Content is null
                ? []
                : await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string text = body.Length > 500 ? Encoding.UTF8.GetString(body, 0, 500) : Encoding.UTF8.GetString(body);
                throw new TtsApiException($"ElevenLabs API HTTP {(int)response.StatusCode}: {text}");
            }

            return body;
        }
    }

    private static IReadOnlyList<TtsVoice> ParseVoices(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new TtsApiException($"Failed to parse ElevenLabs voice list: {Truncate(Encoding.UTF8.GetString(body))}", ex);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("voices", out JsonElement voices) || voices.ValueKind != JsonValueKind.Array)
            {
                throw new TtsApiException($"Unexpected ElevenLabs voice list shape: {Truncate(Encoding.UTF8.GetString(body))}");
            }

            var result = new List<TtsVoice>();
            foreach (JsonElement voice in voices.EnumerateArray())
            {
                TtsVoice? parsed = ParseVoice(voice);
                if (parsed is not null)
                {
                    result.Add(parsed);
                }
            }

            return result;
        }
    }

    // Null for entries that are not objects or lack a voice id or name.
    private static TtsVoice? ParseVoice(JsonElement voice)
    {
        if (voice.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? id = ReadString(voice, "voice_id");
        string? name = ReadString(voice, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new TtsVoice(
            id.Trim(),
            name.Trim(),
            ReadString(voice, "category")?.Trim(),
            ReadGender(voice),
            ReadStringArray(voice, "high_quality_base_model_ids"));
    }

    private static string? ReadGender(JsonElement voice)
    {
        if (voice.TryGetProperty("labels", out JsonElement labels) && labels.ValueKind == JsonValueKind.Object)
        {
            return ReadString(labels, "gender")?.Trim();
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string[]? ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private static string Truncate(string body)
    {
        return body.Length > 500 ? body[..500] : body;
    }

    private sealed record SynthRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("model_id")] string ModelId,
        [property: JsonPropertyName("language_code")] string LanguageCode);
}
