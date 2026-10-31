using Trackdub.Director.Core;
using static ApiResults;
using static AuditionOutputs;
using static RequestParsing;
using static RequestValidation;

// Endpoint handlers for the HTTP API. Program.cs maps each one by name.
internal static class ApiEndpoints
{
    private const string EmptySegmentsMessage = "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }.";

    internal static IResult Status()
    {
        bool hasKey = HasApiKey();
        string model = MetaAuth.GetModel();
        string baseUrl = MetaAuth.GetBaseUrl();

        // Never include the key.
        return Results.Json(new StatusResponse(hasKey, model, baseUrl, MetaAuth.GetReasoningEffort() ?? MetaAuth.DefaultReasoningEffort));
    }

    internal static async Task<IResult> ModelsAsync(MetaClient client, CancellationToken ct)
    {
        return await ApiHandlers.GuardAsync(async () =>
        {
            IReadOnlyList<MetaClient.ModelInfo> models = await client.ListModelsAsync(ct).ConfigureAwait(false);
            // Return shape the CLI's models command would show: array of { id, object, created, owned_by }
            var dto = models.Select(m => new ModelDto(m.Id, m.Object, m.Created, m.OwnedBy)).ToArray();
            return Results.Json(dto);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> TranslateAsync(HttpRequest request, TranslationService translationService, CancellationToken ct)
    {
        var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadTranslateRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (req?.Segments is not { Count: > 0 })
        {
            return Results.BadRequest(new { error = EmptySegmentsMessage });
        }

        if (req.Segments.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Text is null))
        {
            return Results.BadRequest(new { error = "Each segment must have a non-empty 'id' and 'text'." });
        }

        string from = ResolveLanguage(req.SourceLanguage, "en");
        string to = ResolveLanguage(req.TargetLanguage, "es");

        IReadOnlyList<TranslationSegmentInput> segments = req.Segments;
        return await ApiHandlers.GuardAsync(async () =>
        {
            TranslationBatchOutput batch = await translationService.TranslateBatchAsync(segments, from, to, ct).ConfigureAwait(false);
            return Results.Json(batch);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> QaAsync(HttpRequest request, MetaClient client, CancellationToken ct)
    {
        var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadQaRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (req?.Segments is not { Count: > 0 })
        {
            return Results.BadRequest(new { error = EmptySegmentsMessage });
        }

        string from = ResolveLanguage(req.SourceLanguage, "en");
        string to = ResolveLanguage(req.TargetLanguage, "es");

        if (!TryBuildQaOptions(req, to, out QaOptions? options, out string? error))
        {
            return Results.BadRequest(new { error });
        }

        return await ApiHandlers.RunSerializedAsync(() => ApiHandlers.RunQaWithReasoningAsync(client, options, req, from, to, ct), ct).ConfigureAwait(false);
    }

    internal static async Task<IResult> SceneContextAsync(HttpRequest request, SceneContextService sceneService, CancellationToken ct)
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

        return await ApiHandlers.GuardAsync(async () =>
        {
            SceneContextBatch batch = await sceneService.RunAsync(segments, ct).ConfigureAwait(false);
            return Results.Json(batch);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> DirectorAdaptationAsync(HttpRequest request, MetaClient client, CancellationToken ct)
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

        IReadOnlyList<DirectorAdaptationSegmentInput> segments = req.Segments;
        return await ApiHandlers.GuardAsync(async () =>
        {
            var service = new DirectorAdaptationService(client, QaOptions.FromEnvironment());
            DirectorAdaptationBatch batch = await service.RunAsync(segments, from, to, ct).ConfigureAwait(false);
            return Results.Json(batch);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> VisualLocalisationAsync(HttpRequest request, VisualLocalisationService service, CancellationToken ct)
    {
        var (segments, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadVisualLocalisationRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (segments is null || segments.Length == 0)
        {
            return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, frame_image_data_url, box, source_text, target_text }." });
        }

        return await ApiHandlers.GuardAsync(async () =>
        {
            VisualLocalisationBatch batch = await service.RunAsync(segments, ct).ConfigureAwait(false);
            return Results.Json(batch);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> AuditionAsync(HttpRequest request, MetaClient client, ITtsProvider tts, CancellationToken ct)
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

        return await ApiHandlers.RunSerializedAsync(() => ApiHandlers.RunAuditionAsync(client, tts, req, ct), ct).ConfigureAwait(false);
    }

    // SPA fallback: for non-/api routes, serve index.html if it exists (after a prod build).
    internal static async Task FallbackAsync(HttpContext ctx)
    {
        // Don't hijack API 404s
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { error = "Not found" }, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        var environment = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
        string indexPath = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "index.html");
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
    }
}
