using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Director.Core;
using static RequestParsing;
using static RequestValidation;
using static ApiResults;
using static AuditionOutputs;

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
    var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadTranslateRequestAsync(request, ct));
    if (readError is not null)
    {
        return readError;
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
    var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadQaRequestAsync(request, ct));
    if (readError is not null)
    {
        return readError;
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
        return await ApiHandlers.RunQaAsync(client, options, req, from, to, ct).ConfigureAwait(false);
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
    var (segments, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadSceneRequestAsync(request, ct));
    if (readError is not null)
    {
        return readError;
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
    var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadAdaptationRequestAsync(request, ct));
    if (readError is not null)
    {
        return readError;
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
    var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadAuditionRequestAsync(request, ct));
    if (readError is not null)
    {
        return readError;
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

    return await ApiHandlers.RunAuditionAsync(client, tts, req, ct).ConfigureAwait(false);
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
}
