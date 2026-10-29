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

    if (req is null || req.Segments is null || req.Segments.Count == 0)
    {
        return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }." });
    }

    if (req.Segments.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Text is null))
    {
        return Results.BadRequest(new { error = "Each segment must have a non-empty 'id' and 'text'." });
    }

    string from = string.IsNullOrWhiteSpace(req.SourceLanguage) ? "en" : req.SourceLanguage.Trim();
    string to = string.IsNullOrWhiteSpace(req.TargetLanguage) ? "es" : req.TargetLanguage.Trim();

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

    if (req is null || req.Segments is null || req.Segments.Count == 0)
    {
        return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }." });
    }

    string from = string.IsNullOrWhiteSpace(req.SourceLanguage) ? "en" : req.SourceLanguage.Trim();
    string to = string.IsNullOrWhiteSpace(req.TargetLanguage) ? "es" : req.TargetLanguage.Trim();

    // Build QaOptions: request overrides > env defaults, same precedence the CLI uses.
    QaOptions env = QaOptions.FromEnvironment();
    var cpsOverrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in env.CharactersPerSecondByLanguage)
    {
        cpsOverrides[kv.Key] = kv.Value;
    }

    int maxAttempts = req.Options?.MaxAttempts ?? env.MaxAttempts;
    double tolerance = req.Options?.ToleranceFraction ?? env.ToleranceFraction;
    double? cps = req.Options?.TargetCharactersPerSecond;
    string? reasoningEffort = req.Options?.ReasoningEffort ?? env.ReasoningEffort;

    if (maxAttempts < 0)
    {
        return Results.BadRequest(new { error = "maxAttempts must be >= 0." });
    }

    if (tolerance < 0)
    {
        return Results.BadRequest(new { error = "toleranceFraction must be >= 0." });
    }

    if (cps.HasValue)
    {
        if (cps.Value <= 0)
        {
            return Results.BadRequest(new { error = "targetCharactersPerSecond must be > 0." });
        }

        cpsOverrides[to.ToLowerInvariant()] = cps.Value;
    }

    if (reasoningEffort is not null)
    {
        string v = reasoningEffort.Trim().ToLowerInvariant();
        if (v is not ("low" or "medium" or "high"))
        {
            return Results.BadRequest(new { error = "reasoningEffort must be low, medium, or high." });
        }

        reasoningEffort = v;
    }

    var options = new QaOptions
    {
        MaxAttempts = maxAttempts,
        ToleranceFraction = tolerance,
        CharactersPerSecondByLanguage = cpsOverrides,
        ReasoningEffort = reasoningEffort,
    };

    // Inject reasoning_effort into env for the MetaClient call path (MetaAuth reads it).
    // Do not mutate process env globally; instead temporarily set for this request scope via a helper.
    // MetaClient reads MetaAuth.GetReasoningEffort() at call time, so we need to make it visible.
    // Easiest: set env var for duration of request, restore after. Single-process server so this is racy under concurrency,
    // so we instead rely on the fact that MetaClient uses the process env at call time and we have just set options.
    // To avoid cross-request pollution we explicitly set/restore around the run.
    string? prevReasoning = Environment.GetEnvironmentVariable(MetaAuth.ReasoningEffortEnv);
    bool didSetReasoning = false;
    if (reasoningEffort is not null && !string.Equals(prevReasoning, reasoningEffort, StringComparison.OrdinalIgnoreCase))
    {
        Environment.SetEnvironmentVariable(MetaAuth.ReasoningEffortEnv, reasoningEffort);
        didSetReasoning = true;
    }

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
    finally
    {
        if (didSetReasoning)
        {
            Environment.SetEnvironmentVariable(MetaAuth.ReasoningEffortEnv, prevReasoning);
        }
    }
});
const int MaxSceneSegments = 100;
const int MaxConceptsPerSegment = 8;


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

    if (segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)))
    {
        return Results.BadRequest(new { error = "Each segment must have a non-empty 'id'." });
    }

    if (segments.Length > MaxSceneSegments)
    {
        return Results.BadRequest(new { error = $"At most {MaxSceneSegments} segments are allowed per request." });
    }

    if (segments.Any(s => (s.Concepts?.Count(c => !string.IsNullOrWhiteSpace(c)) ?? 0) > MaxConceptsPerSegment))
    {
        return Results.BadRequest(new { error = $"At most {MaxConceptsPerSegment} concepts are allowed per segment." });
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

    private static async Task<TranslateRequest?> ReadTranslateRequestAsync(HttpRequest http, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        // Accept either { segments: [...] } wrapper or raw [...] array
        if (root.ValueKind == JsonValueKind.Array)
        {
            var arr = JsonSerializer.Deserialize<TranslationSegmentInput[]>(root.GetRawText(), JsonOptionsWeb);
            if (arr is null)
            {
                throw new InvalidOperationException("Invalid segments array.");
            }

            return new TranslateRequest(arr, null, null);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Request body must be a JSON object with 'segments' or a JSON array of segments.");
        }

        // Try wrapper
        if (root.TryGetProperty("segments", out JsonElement segEl))
        {
            var segments = JsonSerializer.Deserialize<TranslationSegmentInput[]>(segEl.GetRawText(), JsonOptionsWeb);
            if (segments is null)
            {
                throw new InvalidOperationException("Invalid 'segments' array.");
            }

            string? from = root.TryGetProperty("sourceLanguage", out JsonElement fl) ? fl.GetString() : null;
            if (from is null && root.TryGetProperty("source_language", out JsonElement fl2))
            {
                from = fl2.GetString();
            }

            if (from is null && root.TryGetProperty("from", out JsonElement fl3))
            {
                from = fl3.GetString();
            }

            string? to = root.TryGetProperty("targetLanguage", out JsonElement tl) ? tl.GetString() : null;
            if (to is null && root.TryGetProperty("target_language", out JsonElement tl2))
            {
                to = tl2.GetString();
            }

            if (to is null && root.TryGetProperty("to", out JsonElement tl3))
            {
                to = tl3.GetString();
            }

            return new TranslateRequest(segments, from, to);
        }

        // Maybe the client sent { id, text } without wrapper? Treat as error
        throw new InvalidOperationException("Request must include a non-empty 'segments' array.");
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

        string? from = null;
        if (root.TryGetProperty("sourceLanguage", out JsonElement fl)) from = fl.GetString();
        else if (root.TryGetProperty("source_language", out JsonElement fl2)) from = fl2.GetString();
        else if (root.TryGetProperty("from", out JsonElement fl3)) from = fl3.GetString();

        string? to = null;
        if (root.TryGetProperty("targetLanguage", out JsonElement tl)) to = tl.GetString();
        else if (root.TryGetProperty("target_language", out JsonElement tl2)) to = tl2.GetString();
        else if (root.TryGetProperty("to", out JsonElement tl3)) to = tl3.GetString();

        QaOptionsDto? optionsDto = null;
        if (root.TryGetProperty("options", out JsonElement optEl) && optEl.ValueKind == JsonValueKind.Object)
        {
            int? maxAttempts = null;
            if (optEl.TryGetProperty("maxAttempts", out JsonElement ma) && ma.ValueKind == JsonValueKind.Number && ma.TryGetInt32(out int mav)) maxAttempts = mav;
            else if (optEl.TryGetProperty("max_attempts", out JsonElement ma2) && ma2.ValueKind == JsonValueKind.Number && ma2.TryGetInt32(out int mav2)) maxAttempts = mav2;

            double? tolerance = null;
            if (optEl.TryGetProperty("toleranceFraction", out JsonElement tf) && tf.ValueKind == JsonValueKind.Number && tf.TryGetDouble(out double tfv)) tolerance = tfv;
            else if (optEl.TryGetProperty("tolerance_fraction", out JsonElement tf2) && tf2.ValueKind == JsonValueKind.Number && tf2.TryGetDouble(out double tfv2)) tolerance = tfv2;
            else if (optEl.TryGetProperty("tolerance", out JsonElement tf3) && tf3.ValueKind == JsonValueKind.Number && tf3.TryGetDouble(out double tfv3)) tolerance = tfv3;

            double? cps = null;
            if (optEl.TryGetProperty("targetCharactersPerSecond", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out double cv)) cps = cv;
            else if (optEl.TryGetProperty("target_cps", out JsonElement c2) && c2.ValueKind == JsonValueKind.Number && c2.TryGetDouble(out double cv2)) cps = cv2;
            else if (optEl.TryGetProperty("cps", out JsonElement c3) && c3.ValueKind == JsonValueKind.Number && c3.TryGetDouble(out double cv3)) cps = cv3;

            string? reasoning = null;
            if (optEl.TryGetProperty("reasoningEffort", out JsonElement re) && re.ValueKind == JsonValueKind.String) reasoning = re.GetString();
            else if (optEl.TryGetProperty("reasoning_effort", out JsonElement re2) && re2.ValueKind == JsonValueKind.String) reasoning = re2.GetString();

            optionsDto = new QaOptionsDto(maxAttempts, tolerance, cps, reasoning);
        }

        return new QaApiRequest(segments, from, to, optionsDto);
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

internal sealed record QaOptionsDto(
    int? MaxAttempts,
    double? ToleranceFraction,
    double? TargetCharactersPerSecond,
    string? ReasoningEffort);
