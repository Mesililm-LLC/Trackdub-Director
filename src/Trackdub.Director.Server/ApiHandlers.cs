using System.Text.Json;
using Trackdub.Director.Core;
using static ApiResults;
using static AuditionOutputs;
using static RequestValidation;

// Runs the QA and audition pipelines for their endpoints and maps failures to HTTP results.
internal static class ApiHandlers
{
    // QA and audition set or read the process-wide reasoning effort and share the Meta call path, so their runs go one at a time.
    private static readonly SemaphoreSlim MetaRunLock = new(1, 1);

    internal static async Task<IResult> RunSerializedAsync(Func<Task<IResult>> run, CancellationToken ct)
    {
        await MetaRunLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await run().ConfigureAwait(false);
        }
        finally
        {
            MetaRunLock.Release();
        }
    }

    // Maps the upstream and input failures every endpoint shares to HTTP results.
    internal static async Task<IResult> GuardAsync(Func<Task<IResult>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
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
    }

    // Reads a request body. Malformed JSON or invalid content becomes a 400 result instead of an exception.
    internal static async Task<(T? Value, IResult? Error)> ReadOrBadRequestAsync<T>(Func<Task<T?>> read)
        where T : class
    {
        try
        {
            return (await read().ConfigureAwait(false), null);
        }
        catch (JsonException ex)
        {
            return (null, Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }));
        }
        catch (InvalidOperationException ex)
        {
            return (null, Results.BadRequest(new { error = ex.Message }));
        }
    }

    internal static async Task<IResult> RunAuditionAsync(MetaClient client, ITtsProvider tts, AuditionApiRequest req, CancellationToken ct)
    {
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
    }

    // MetaClient reads the reasoning effort from the process env at call time, so set it for this run and restore after.
    internal static async Task<IResult> RunQaWithReasoningAsync(MetaClient client, QaOptions options, QaApiRequest req, string from, string to, CancellationToken ct)
    {
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
    }

    internal static async Task<IResult> RunQaAsync(MetaClient client, QaOptions options, QaApiRequest req, string from, string to, CancellationToken ct)
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
}
