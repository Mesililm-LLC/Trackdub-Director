using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Director.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

// HttpClient used by MetaClient. Registered so tests can replace it with a fake handler.
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddTransient(sp => new MetaClient(sp.GetRequiredService<HttpClient>()));
builder.Services.AddTransient<TranslationService>(sp => new TranslationService(sp.GetRequiredService<MetaClient>()));
builder.Services.AddTransient<SceneContextService>(sp => new SceneContextService(sp.GetRequiredService<MetaClient>()));
// One provider for the process: it owns a 120-second client sized for audio synthesis.
builder.Services.AddSingleton<ITtsProvider>(_ => new ElevenLabsTtsProvider());

// JSON: keep defaults but ensure case-insensitive reads and respect JsonPropertyName attributes
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNameCaseInsensitive = true;
    opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseCors();

if (!app.Environment.IsDevelopment())
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
else
{
    // In dev, static files still work if wwwroot exists (after a prod build), but Vite is primary.
    app.UseStaticFiles();
}

// --- API ---

app.MapGet("/api/status", () =>
{
    bool hasKey = HasApiKey();
    string model = MetaAuth.GetModel();
    string baseUrl = MetaAuth.GetBaseUrl();

    // Never include the key.
    return Results.Json(new StatusResponse(hasKey, model, baseUrl, MetaAuth.GetReasoningEffort() ?? MetaAuth.DefaultReasoningEffort));
});

app.MapGet("/api/models", async (MetaClient client, CancellationToken ct) =>
{
    try
    {
        IReadOnlyList<MetaClient.ModelInfo> models = await client.ListModelsAsync(ct).ConfigureAwait(false);
        // Return shape the CLI's models command would show: array of { id, object, created, owned_by }
        var dto = models.Select(m => new ModelDto(m.Id, m.Object, m.Created, m.OwnedBy)).ToArray();
        return Results.Json(dto);
    }
    catch (MetaApiException ex)
    {
        return MapMetaException(ex);
    }
});

app.MapPost("/api/translate", async (HttpRequest request, TranslationService translationService, CancellationToken ct) =>
{
    TranslateRequest? req;
    try
    {
        req = await ReadTranslateRequestAsync(request, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (req?.Segments is not { Count: > 0 })
    {
        return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }." });
    }

    if (req.Segments.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Text is null))
    {
        return Results.BadRequest(new { error = "Each segment must have a non-empty 'id' and 'text'." });
    }

    string from = ResolveLanguage(req.SourceLanguage, "en");
    string to = ResolveLanguage(req.TargetLanguage, "es");

    try
    {
        TranslationBatchOutput batch = await translationService.TranslateBatchAsync(req.Segments, from, to, ct).ConfigureAwait(false);
        return Results.Json(batch);
    }
    catch (MetaApiException ex)
    {
        return MapMetaException(ex);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/qa", async (HttpRequest request, MetaClient client, CancellationToken ct) =>
{
    QaApiRequest? req;
    try
    {
        req = await ReadQaRequestAsync(request, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (req?.Segments is not { Count: > 0 })
    {
        return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }." });
    }

    string from = ResolveLanguage(req.SourceLanguage, "en");
    string to = ResolveLanguage(req.TargetLanguage, "es");

    if (!TryBuildQaOptions(req, to, out QaOptions? options, out string? error))
    {
        return Results.BadRequest(new { error });
    }

    // MetaClient reads the reasoning effort from the process env at call time, so set it for this run and restore after.
    string? prevReasoning = Environment.GetEnvironmentVariable(MetaAuth.ReasoningEffortEnv);
    bool didSetReasoning = false;
    if (options.ReasoningEffort is not null && !string.Equals(prevReasoning, options.ReasoningEffort, StringComparison.OrdinalIgnoreCase))
    {
        Environment.SetEnvironmentVariable(MetaAuth.ReasoningEffortEnv, options.ReasoningEffort);
        didSetReasoning = true;
    }

    try
    {
        return await RunQaAsync(client, options, req, from, to, ct).ConfigureAwait(false);
    }
    finally
    {
        if (didSetReasoning)
        {
            Environment.SetEnvironmentVariable(MetaAuth.ReasoningEffortEnv, prevReasoning);
        }
    }
});
app.MapPost("/api/scene-context", async (HttpRequest request, SceneContextService sceneService, CancellationToken ct) =>
{
    SceneSegmentInput[]? segments;
    try
    {
        segments = await ReadSceneRequestAsync(request, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (segments is null || segments.Length == 0)
    {
        return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text?, frame_image_data_url?, concepts?, speaker_id?, speaker_label?, frame_id?, timecode_seconds?, frame_sha256?, vision_enrichment? }." });
    }

    string? validationError = ValidateSceneSegments(segments);
    if (validationError is not null)
    {
        return Results.BadRequest(new { error = validationError });
    }

    try
    {
        SceneContextBatch batch = await sceneService.RunAsync(segments, ct).ConfigureAwait(false);
        return Results.Json(batch);
    }
    catch (MetaApiException ex)
    {
        return MapMetaException(ex);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/director-adaptation", async (HttpRequest request, MetaClient client, CancellationToken ct) =>
{
    AdaptationApiRequest? req;
    try
    {
        req = await ReadAdaptationRequestAsync(request, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (req is null || req.Segments.Count == 0)
    {
        return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text?, translation?, duration_seconds?, context?, director_adaptation? }." });
    }

    string? validationError = ValidateAdaptationSegments(req.Segments);
    if (validationError is not null)
    {
        return Results.BadRequest(new { error = validationError });
    }

    string from = ResolveLanguage(req.SourceLanguage, "en");
    string to = ResolveLanguage(req.TargetLanguage, "es");

    try
    {
        var service = new DirectorAdaptationService(client, QaOptions.FromEnvironment());
        DirectorAdaptationBatch batch = await service.RunAsync(req.Segments, from, to, ct).ConfigureAwait(false);
        return Results.Json(batch);
    }
    catch (MetaApiException ex)
    {
        return MapMetaException(ex);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/audition", async (HttpRequest request, MetaClient client, ITtsProvider tts, CancellationToken ct) =>
{
    AuditionApiRequest? req;
    try
    {
        req = await ReadAuditionRequestAsync(request, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (req is null || req.Segments.Count == 0)
    {
        return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text, duration_seconds?, speaker_id? }." });
    }

    string? validationError = ValidateAuditionSegments(req.Segments);
    if (validationError is not null)
    {
        return Results.BadRequest(new { error = validationError });
    }

    string from = ResolveLanguage(req.SourceLanguage, "en");
    string to = ResolveLanguage(req.TargetLanguage, "es");

    try
    {
        var service = new AuditionService(client, tts, QaOptions.FromEnvironment());
        AuditionReport report = await service.RunAsync(req.Segments, from, to, ct).ConfigureAwait(false);
        await TryWriteAuditionOutputsAsync(report, ct).ConfigureAwait(false);
        return Results.Json(report);
    }
    catch (TtsApiException ex)
    {
        return MapTtsException(ex);
    }
    catch (MetaApiException ex)
    {
        return MapMetaException(ex);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// SPA fallback: for non-/api routes, serve index.html if it exists (after a prod build)
app.MapFallback(async (HttpContext ctx) =>
{
    // Don't hijack API 404s
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsJsonAsync(new { error = "Not found" }, ctx.RequestAborted).ConfigureAwait(false);
        return;
    }

    string indexPath = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(indexPath, ctx.RequestAborted).ConfigureAwait(false);
        return;
    }

    // No built frontend: return a minimal hint
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/html";
    await ctx.Response.WriteAsync("""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Trackdub Director</title></head>
        <body style="font-family:system-ui;background:#111;color:#eee;padding:2rem">
        <h1>Trackdub Director — API running</h1>
        <p>Frontend not built. Run <code>npm run build</code> in <code>web/</code> or start Vite with <code>npm run dev</code>.</p>
        <p>API: <a style="color:#8be" href="/api/status">GET /api/status</a></p>
        </body></html>
        """, ctx.RequestAborted).ConfigureAwait(false);
});

app.Run();

// For WebApplicationFactory in tests
public partial class Program
{
    private static bool HasApiKey()
    {
        try
        {
            _ = MetaAuth.GetApiKey();
            return true;
        }
        catch (MetaApiException)
        {
            return false;
        }
    }

    private static IResult MapMetaException(MetaApiException ex)
    {
        // Keys are never logged or echoed; the message already omits the key per MetaClient invariant.
        // 401 when no key is configured; otherwise 502 (upstream).
        string msg = ex.Message ?? "Meta Model API error";
        if (msg.Contains("No Meta Model API key", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = msg }, statusCode: 401);
        }

        if (msg.Contains("HTTP 401", StringComparison.Ordinal) || msg.Contains("HTTP 403", StringComparison.Ordinal))
        {
            return Results.Json(new { error = msg }, statusCode: 401);
        }

        return Results.Json(new { error = msg }, statusCode: 502);
    }

    private static IResult MapTtsException(TtsApiException ex)
    {
        string msg = ex.Message ?? "ElevenLabs API error";
        if (msg.Contains("No ElevenLabs API key", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("HTTP 401", StringComparison.Ordinal)
            || msg.Contains("HTTP 403", StringComparison.Ordinal))
        {
            return Results.Json(new { error = msg }, statusCode: 401);
        }

        return Results.Json(new { error = msg }, statusCode: 502);
    }

    private static async Task<AuditionApiRequest?> ReadAuditionRequestAsync(HttpRequest http, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var arr = JsonSerializer.Deserialize<AuditionSegmentInput[]>(root.GetRawText(), JsonOptionsWeb)
                ?? throw new InvalidOperationException("Invalid segments array.");
            return new AuditionApiRequest(arr, null, null);
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("segments", out JsonElement segEl))
        {
            throw new InvalidOperationException("Request must include a non-empty 'segments' array.");
        }

        var segments = JsonSerializer.Deserialize<AuditionSegmentInput[]>(segEl.GetRawText(), JsonOptionsWeb)
            ?? throw new InvalidOperationException("Invalid 'segments' array.");

        string? from = ReadFirstPresentString(root, "sourceLanguage", "source_language", "from");
        string? to = ReadFirstPresentString(root, "targetLanguage", "target_language", "to");

        return new AuditionApiRequest(segments, from, to);
    }

    private static string? ValidateAuditionSegments(IReadOnlyList<AuditionSegmentInput> segments)
    {
        if (segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Text is null))
        {
            return "Each segment must have a non-empty 'id' and 'text'.";
        }

        if (segments.Select(s => s!.Id.Trim()).Distinct(StringComparer.Ordinal).Count() != segments.Count)
        {
            return "Duplicate segment ids are not allowed.";
        }

        if (segments.Count > MaxAuditionSegments)
        {
            return $"At most {MaxAuditionSegments} segments are allowed per request.";
        }

        return null;
    }

    // Persists winning WAVs + the tracked manifest only when AUDITION_OUTPUT_DIR
    // names the dataset folder (live capture runs). API tests leave it unset so
    // no fake-run files ever land in the repo.
    private static async Task TryWriteAuditionOutputsAsync(AuditionReport report, CancellationToken ct)
    {
        string? outputDir = Environment.GetEnvironmentVariable("AUDITION_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        string auditionDir = Path.Combine(outputDir, "media", "audition");
        Directory.CreateDirectory(auditionDir);
        foreach (AuditionSegmentReport segment in report.Segments)
        {
            AuditionTakeReport? winner = segment.Takes.FirstOrDefault(t => t.IsWinner);
            if (winner?.AudioBytes is { } bytes && segment.WinnerFileName is not null)
            {
                await WriteWinnerAsync(auditionDir, segment.WinnerFileName, bytes, ct).ConfigureAwait(false);
            }
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        string manifest = JsonSerializer.Serialize(report, options);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "audition-manifest.json"), manifest, ct).ConfigureAwait(false);
    }

    // The file name comes from AuditionService.TakeFileName, which keeps it inside one folder. The check
    // here is a second guard: the resolved path must stay under the audition directory.
    private static async Task WriteWinnerAsync(string auditionDir, string fileName, byte[] bytes, CancellationToken ct)
    {
        string root = Path.GetFullPath(auditionDir) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(auditionDir, fileName));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Winning take file name resolves outside the audition directory.");
        }

        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
    }

    private static async Task<TranslateRequest?> ReadTranslateRequestAsync(HttpRequest http, CancellationToken ct)
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
    private static string? ReadFirstPresentString(JsonElement root, params string[] names)
    {
        string? present = Array.Find(names, name => root.TryGetProperty(name, out _));
        return present is null ? null : root.GetProperty(present).GetString();
    }

    private static async Task<QaApiRequest?> ReadQaRequestAsync(HttpRequest http, CancellationToken ct)
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

    private static QaOptionsDto ReadQaOptions(JsonElement optEl) => new(
        ReadFirst<int?>(optEl, el => el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : null, "maxAttempts", "max_attempts"),
        ReadFirst<double?>(optEl, el => el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double v) ? v : null, "toleranceFraction", "tolerance_fraction", "tolerance"),
        ReadFirst<double?>(optEl, el => el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double v) ? v : null, "targetCharactersPerSecond", "target_cps", "cps"),
        ReadFirst<string>(optEl, el => el.ValueKind == JsonValueKind.String ? el.GetString() : null, "reasoningEffort", "reasoning_effort"));

    private static TranslationSegmentInput[] DeserializeTranslationSegments(JsonElement element, string errorMessage)
    {
        return JsonSerializer.Deserialize<TranslationSegmentInput[]>(element.GetRawText(), JsonOptionsWeb)
            ?? throw new InvalidOperationException(errorMessage);
    }

    // Returns the first alias whose value converts to T; aliases that are missing or fail to convert are skipped.
    private static T? ReadFirst<T>(JsonElement obj, Func<JsonElement, T?> convert, params string[] names)
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

    // Builds QaOptions with request overrides > env defaults, same precedence the CLI uses.
    private static bool TryBuildQaOptions(QaApiRequest req, string to, [NotNullWhen(true)] out QaOptions? options, [NotNullWhen(false)] out string? error)
    {
        options = null;
        error = null;

        QaOptions env = QaOptions.FromEnvironment();
        int maxAttempts = req.Options?.MaxAttempts ?? env.MaxAttempts;
        double tolerance = req.Options?.ToleranceFraction ?? env.ToleranceFraction;
        double? cps = req.Options?.TargetCharactersPerSecond;
        string? reasoningEffort = req.Options?.ReasoningEffort ?? env.ReasoningEffort;

        error = ValidateQaOptionValues(maxAttempts, tolerance, cps);
        if (error is not null)
        {
            return false;
        }

        if (reasoningEffort is not null)
        {
            reasoningEffort = NormalizeReasoningEffort(reasoningEffort);
            if (reasoningEffort is null)
            {
                error = "reasoningEffort must be low, medium, or high.";
                return false;
            }
        }

        options = new QaOptions
        {
            MaxAttempts = maxAttempts,
            ToleranceFraction = tolerance,
            CharactersPerSecondByLanguage = BuildCpsOverrides(env, to, cps),
            ReasoningEffort = reasoningEffort,
        };
        return true;
    }

    private static string? ValidateQaOptionValues(int maxAttempts, double tolerance, double? cps)
    {
        if (maxAttempts < 0)
        {
            return "maxAttempts must be >= 0.";
        }

        if (tolerance < 0)
        {
            return "toleranceFraction must be >= 0.";
        }

        if (cps is <= 0.0)
        {
            return "targetCharactersPerSecond must be > 0.";
        }

        return null;
    }

    // Copies the env CPS defaults, then applies the request's CPS for the target language when one was given.
    private static Dictionary<string, double> BuildCpsOverrides(QaOptions env, string to, double? cps)
    {
        var overrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in env.CharactersPerSecondByLanguage)
        {
            overrides[kv.Key] = kv.Value;
        }

        if (cps is { } value)
        {
            overrides[to.ToLowerInvariant()] = value;
        }

        return overrides;
    }

    // Lowercases and trims a reasoning effort, or returns null when it is not one of the supported levels.
    private static string? NormalizeReasoningEffort(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" ? normalized : null;
    }

    private static async Task<IResult> RunQaAsync(MetaClient client, QaOptions options, QaApiRequest req, string from, string to, CancellationToken ct)
    {
        try
        {
            var qaService = new QaService(client, options);

            // Convert to QaService's input shape (already matches)
            IReadOnlyList<QaSegmentInput> segments = req.Segments.Select(s => new QaSegmentInput(s.Id, s.Text, s.DurationSeconds)).ToArray();

            QaReport report = await qaService.RunAsync(segments, from, to, ct).ConfigureAwait(false);
            return Results.Json(report);
        }
        catch (MetaApiException ex)
        {
            return MapMetaException(ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<SceneSegmentInput[]?> ReadSceneRequestAsync(HttpRequest http, CancellationToken ct)
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

    private static async Task<AdaptationApiRequest?> ReadAdaptationRequestAsync(HttpRequest http, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var arr = JsonSerializer.Deserialize<DirectorAdaptationSegmentInput[]>(root.GetRawText(), JsonOptionsWeb)
                ?? throw new InvalidOperationException("Invalid segments array.");
            return new AdaptationApiRequest(arr, null, null);
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("segments", out JsonElement segEl))
        {
            throw new InvalidOperationException("Request must include a non-empty 'segments' array.");
        }

        var segments = JsonSerializer.Deserialize<DirectorAdaptationSegmentInput[]>(segEl.GetRawText(), JsonOptionsWeb)
            ?? throw new InvalidOperationException("Invalid 'segments' array.");

        string? from = ReadFirstPresentString(root, "sourceLanguage", "source_language", "from");
        string? to = ReadFirstPresentString(root, "targetLanguage", "target_language", "to");

        return new AdaptationApiRequest(segments, from, to);
    }

    private const int MaxSceneSegments = 100;
    private const int MaxConceptsPerSegment = 8;
    private const int MaxAdaptationSegments = 100;
    private const int MaxAuditionSegments = 100;

    // Each validator returns the error message to send back, or null when the request is acceptable.
    private static string? ValidateSceneSegments(SceneSegmentInput[] segments)
    {
        if (segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)))
        {
            return "Each segment must have a non-empty 'id'.";
        }

        if (segments.Length > MaxSceneSegments)
        {
            return $"At most {MaxSceneSegments} segments are allowed per request.";
        }

        if (segments.Any(s => (s.Concepts?.Count(c => !string.IsNullOrWhiteSpace(c)) ?? 0) > MaxConceptsPerSegment))
        {
            return $"At most {MaxConceptsPerSegment} concepts are allowed per segment.";
        }

        return null;
    }

    private static string? ValidateAdaptationSegments(IReadOnlyList<DirectorAdaptationSegmentInput> segments)
    {
        if (segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)))
        {
            return "Each segment must have a non-empty 'id'.";
        }

        if (segments.Select(s => s!.Id.Trim()).Distinct(StringComparer.Ordinal).Count() != segments.Count)
        {
            return "Duplicate segment ids are not allowed.";
        }

        if (segments.Count > MaxAdaptationSegments)
        {
            return $"At most {MaxAdaptationSegments} segments are allowed per request.";
        }

        return null;
    }

    // Trims the caller's language, or falls back to the default when it is missing or blank.
    private static string ResolveLanguage(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static readonly JsonSerializerOptions JsonOptionsWeb = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}

// DTOs

internal sealed record StatusResponse(
    [property: JsonPropertyName("hasKey")] bool HasKey,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("reasoningEffort")] string ReasoningEffort);

internal sealed record ModelDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);

internal sealed record TranslateRequest(
    IReadOnlyList<TranslationSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage);

internal sealed record QaApiRequest(
    IReadOnlyList<QaSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage,
    QaOptionsDto? Options);

internal sealed record AdaptationApiRequest(
    IReadOnlyList<DirectorAdaptationSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage);

internal sealed record AuditionApiRequest(
    IReadOnlyList<AuditionSegmentInput> Segments,
    string? SourceLanguage,
    string? TargetLanguage);

internal sealed record QaOptionsDto(
    int? MaxAttempts,
    double? ToleranceFraction,
    double? TargetCharactersPerSecond,
    string? ReasoningEffort);
