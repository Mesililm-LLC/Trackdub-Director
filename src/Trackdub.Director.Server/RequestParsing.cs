using System.Text.Json;
using Trackdub.Director.Core;

// Reads request bodies into the records the endpoints use. Malformed bodies throw; endpoints map the throw to 400.
internal static class RequestParsing
{
    internal static async Task<AuditionApiRequest?> ReadAuditionRequestAsync(HttpRequest http, CancellationToken ct)
    {
        SegmentBody<AuditionSegmentInput> body = await ReadSegmentBodyAsync<AuditionSegmentInput>(http, ct).ConfigureAwait(false);
        return new AuditionApiRequest(body.Segments, body.SourceLanguage, body.TargetLanguage);
    }

    internal static async Task<TranslateRequest?> ReadTranslateRequestAsync(HttpRequest http, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        // Accept either { segments: [...] } wrapper or raw [...] array
        if (root.ValueKind == JsonValueKind.Array)
        {
            return new TranslateRequest(DeserializeTranslationSegments(root, "Invalid segments array."), null, null);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Request body must be a JSON object with 'segments' or a JSON array of segments.");
        }

        // Try wrapper
        if (root.TryGetProperty("segments", out JsonElement segEl))
        {
            return new TranslateRequest(
                DeserializeTranslationSegments(segEl, "Invalid 'segments' array."),
                ReadFirst<string>(root, el => el.GetString(), "sourceLanguage", "source_language", "from"),
                ReadFirst<string>(root, el => el.GetString(), "targetLanguage", "target_language", "to"));
        }

        // Maybe the client sent { id, text } without wrapper? Treat as error
        throw new InvalidOperationException("Request must include a non-empty 'segments' array.");
    }

    // Returns the value of the first alias that is present, even when that value is null.
    internal static string? ReadFirstPresentString(JsonElement root, params string[] names)
    {
        string? present = Array.Find(names, name => root.TryGetProperty(name, out _));
        return present is null ? null : root.GetProperty(present).GetString();
    }

    internal static async Task<QaApiRequest?> ReadQaRequestAsync(HttpRequest http, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var arr = JsonSerializer.Deserialize<QaSegmentInput[]>(root.GetRawText(), JsonOptionsWeb);
            if (arr is null)
            {
                throw new InvalidOperationException("Invalid segments array.");
            }

            return new QaApiRequest(arr, null, null, null);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Request body must be a JSON object with 'segments' or a JSON array of segments.");
        }

        if (!root.TryGetProperty("segments", out JsonElement segEl))
        {
            throw new InvalidOperationException("Request must include a non-empty 'segments' array.");
        }

        var segments = JsonSerializer.Deserialize<QaSegmentInput[]>(segEl.GetRawText(), JsonOptionsWeb);
        if (segments is null)
        {
            throw new InvalidOperationException("Invalid 'segments' array.");
        }

        string? from = ReadFirstPresentString(root, "sourceLanguage", "source_language", "from");
        string? to = ReadFirstPresentString(root, "targetLanguage", "target_language", "to");

        QaOptionsDto? optionsDto = null;
        if (root.TryGetProperty("options", out JsonElement optEl) && optEl.ValueKind == JsonValueKind.Object)
        {
            optionsDto = ReadQaOptions(optEl);
        }

        return new QaApiRequest(segments, from, to, optionsDto);
    }

    internal static QaOptionsDto ReadQaOptions(JsonElement optEl) => new(
        ReadFirst<int?>(optEl, el => el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : null, "maxAttempts", "max_attempts"),
        ReadFirst<double?>(optEl, el => el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double v) ? v : null, "toleranceFraction", "tolerance_fraction", "tolerance"),
        ReadFirst<double?>(optEl, el => el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double v) ? v : null, "targetCharactersPerSecond", "target_cps", "cps"),
        ReadFirst<string>(optEl, el => el.ValueKind == JsonValueKind.String ? el.GetString() : null, "reasoningEffort", "reasoning_effort"));

    internal static TranslationSegmentInput[] DeserializeTranslationSegments(JsonElement element, string errorMessage)
    {
        return JsonSerializer.Deserialize<TranslationSegmentInput[]>(element.GetRawText(), JsonOptionsWeb)
            ?? throw new InvalidOperationException(errorMessage);
    }

    // Returns the first alias whose value converts to T; aliases that are missing or fail to convert are skipped.
    internal static T? ReadFirst<T>(JsonElement obj, Func<JsonElement, T?> convert, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj.TryGetProperty(name, out JsonElement el) && convert(el) is { } value)
            {
                return value;
            }
        }

        return default;
    }

    internal static async Task<SceneSegmentInput[]?> ReadSceneRequestAsync(HttpRequest http, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        // Accept either { segments: [...] } wrapper or raw [...] array
        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<SceneSegmentInput[]>(root.GetRawText(), JsonOptionsWeb)
                ?? throw new InvalidOperationException("Invalid segments array.");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("segments", out JsonElement segEl))
        {
            throw new InvalidOperationException("Request must include a non-empty 'segments' array.");
        }

        return JsonSerializer.Deserialize<SceneSegmentInput[]>(segEl.GetRawText(), JsonOptionsWeb)
            ?? throw new InvalidOperationException("Invalid 'segments' array.");
    }

    internal static async Task<AdaptationApiRequest?> ReadAdaptationRequestAsync(HttpRequest http, CancellationToken ct)
    {
        SegmentBody<DirectorAdaptationSegmentInput> body = await ReadSegmentBodyAsync<DirectorAdaptationSegmentInput>(http, ct).ConfigureAwait(false);
        return new AdaptationApiRequest(body.Segments, body.SourceLanguage, body.TargetLanguage);
    }

    // A body is either a raw segment array, or an object with a 'segments' array and optional languages.
    internal static async Task<SegmentBody<T>> ReadSegmentBodyAsync<T>(HttpRequest http, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return new SegmentBody<T>(DeserializeSegments<T>(root, "Invalid segments array."), null, null);
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("segments", out JsonElement segEl))
        {
            throw new InvalidOperationException("Request must include a non-empty 'segments' array.");
        }

        return new SegmentBody<T>(
            DeserializeSegments<T>(segEl, "Invalid 'segments' array."),
            ReadFirstPresentString(root, "sourceLanguage", "source_language", "from"),
            ReadFirstPresentString(root, "targetLanguage", "target_language", "to"));
    }

    internal readonly record struct SegmentBody<T>(T[] Segments, string? SourceLanguage, string? TargetLanguage);

    private static T[] DeserializeSegments<T>(JsonElement element, string errorMessage)
    {
        return JsonSerializer.Deserialize<T[]>(element.GetRawText(), JsonOptionsWeb)
            ?? throw new InvalidOperationException(errorMessage);
    }

    internal static readonly JsonSerializerOptions JsonOptionsWeb = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
