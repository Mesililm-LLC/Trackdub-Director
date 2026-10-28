using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class QaServiceTests
{
    private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        string json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage ChatResponse(string modelId, string content, int promptTokens, int completionTokens)
    {
        var payload = new
        {
            id = $"chatcmpl-test-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = modelId,
            choices = new[]
            {
                new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } },
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
            },
        };
        return JsonResponse(payload);
    }

    /// <summary>
    /// Handler that distinguishes translation vs meaning calls by the meaning marker in the request body.
    /// Translation queue holds plain translation strings; meaning queue holds JSON strings for meaning check.
    /// Optionally injects exception for meaning calls via meaningExceptions queue.
    /// </summary>
    private static HttpMessageHandler DualHandler(
        string modelId,
        Queue<string> translations,
        Queue<string> meaningJsons,
        int transPrompt = 10,
        int transComp = 5,
        int meaningPrompt = 7,
        int meaningComp = 4,
        Queue<Exception>? meaningExceptions = null,
        List<string>? capturedBodies = null,
        Action<string>? inspectRevisionBody = null)
    {
        return new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            capturedBodies?.Add(body);
            Assert.Contains("/chat/completions", request.RequestUri?.ToString() ?? string.Empty);

            bool isMeaning = body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal);

            if (isMeaning)
            {
                if (meaningExceptions is not null && meaningExceptions.Count > 0)
                {
                    Exception ex = meaningExceptions.Dequeue();
                    // Simulate transport failure by throwing
                    throw ex;
                }

                if (meaningJsons.Count == 0)
                {
                    throw new InvalidOperationException("No more canned meaning JSON queued.");
                }

                string meaningJson = meaningJsons.Dequeue();
                return ChatResponse(modelId, meaningJson, meaningPrompt, meaningComp);
            }
            else
            {
                // Check revision body inspection if needed
                if (body.Contains("Revise", StringComparison.Ordinal) && inspectRevisionBody is not null)
                {
                    inspectRevisionBody(body);
                }

                if (translations.Count == 0)
                {
                    throw new InvalidOperationException("No more canned translations queued.");
                }

                string translation = translations.Dequeue();
                return ChatResponse(modelId, translation, transPrompt, transComp);
            }
        });
    }

    private static HttpMessageHandler SequencedHandler(
        string modelId,
        Queue<string> translations,
        int promptTokens = 10,
        int completionTokens = 5)
    {
        // Legacy simple handler without meaning; used for pure timing tests where meaning never called
        return DualHandler(modelId, translations, new Queue<string>(), transPrompt: promptTokens, transComp: completionTokens);
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private static IDisposable EnvShim(string key, string value)
    {
        string? prev = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        return new Restore(key, prev);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string _key;
        private readonly string? _prev;

        public Restore(string key, string? prev)
        {
            _key = key;
            _prev = prev;
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_key, _prev);
    }

    private static IDisposable FakeAuth()
    {
        var a = EnvShim("META_API_KEY", "test-fake-key");
        var b = EnvShim("META_MODEL", "muse-spark-1.1");
        var c = EnvShim("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1");
        var d = EnvShim("META_REASONING_EFFORT", "low");
        return new Composite(a, b, c, d);
    }

    private sealed class Composite : IDisposable
    {
        private readonly IDisposable[] _items;

        public Composite(params IDisposable[] items) => _items = items;

        public void Dispose()
        {
            foreach (IDisposable d in _items)
            {
                d.Dispose();
            }
        }
    }

    private static string MeaningPassJson(string explanation = "Meaning preserved.")
        => JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation });

    private static string MeaningFailJson(string[] issues, string explanation)
        => JsonSerializer.Serialize(new { verdict = "fail", issues, explanation });

    [Fact]
    public async Task Line_passes_first_try()
    {
        using var _ = FakeAuth();

        var q = new Queue<string>(["Hola."]);
        var m = new Queue<string>([MeaningPassJson()]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", q, m));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-a", "Hello.", DurationSeconds: 4.2) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        Assert.Single(report.Segments);
        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Pass, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Pass, seg.FirstAttempt.TimingVerdict);
        Assert.Equal(MeaningVerdict.Pass, seg.FirstAttempt.MeaningVerdict);
        Assert.Empty(seg.FirstAttempt.MeaningIssues ?? []);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Empty(seg.Revisions);
        Assert.Equal(1, report.Totals.TotalSegments);
        Assert.Equal(1, report.Totals.FirstPassCount);
        Assert.Equal(1, report.Totals.FinalPassCount);
        Assert.Equal(0, report.Totals.TotalRevisions);
        Assert.Equal("Hola.", seg.FirstAttempt.Translation);
        Assert.Equal("Hola.", seg.FinalTranslation);
        Assert.True(report.Totals.TotalTokens > 0);
        // TokenUsage sums translation + meaning
        Assert.Equal(10 + 7, seg.TokenUsage.PromptTokens);
        Assert.Equal(5 + 4, seg.TokenUsage.CompletionTokens);
        // Per-attempt meaning tokens
        Assert.Equal(7, seg.FirstAttempt.MeaningPromptTokens);
        Assert.Equal(4, seg.FirstAttempt.MeaningCompletionTokens);
        Assert.Equal(MeaningGate.PromptTemplateVersion, seg.FirstAttempt.MeaningPromptTemplateVersion);
        Assert.False(string.IsNullOrWhiteSpace(seg.FirstAttempt.MeaningTimestampUtc));
        Assert.Equal("api.meta.ai", seg.FirstAttempt.MeaningEndpointHost);
    }

    [Fact]
    public async Task Line_fails_then_revised_and_passes()
    {
        using var _ = FakeAuth();

        var q = new Queue<string>([
            "Esta es una traduccion muy larga que no cabe",
            "Traduccion corta",
        ]);
        var m = new Queue<string>([MeaningPassJson("Short but faithful.")]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", q, m));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-b", "This is a long source line that will overrun", DurationSeconds: 1.0) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.TimingVerdict);
        // Timing fail: meaning not checked
        Assert.Equal(MeaningVerdict.NotEvaluated, seg.FirstAttempt.MeaningVerdict);
        Assert.Equal(0, seg.FirstAttempt.MeaningPromptTokens);
        Assert.Single(seg.Revisions);
        Assert.Equal(QaVerdict.Pass, seg.Revisions[0].Verdict);
        Assert.Equal(QaVerdict.Pass, seg.Revisions[0].TimingVerdict);
        Assert.Equal(MeaningVerdict.Pass, seg.Revisions[0].MeaningVerdict);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Equal(0, report.Totals.FirstPassCount);
        Assert.Equal(1, report.Totals.FinalPassCount);
        Assert.Equal(1, report.Totals.TotalRevisions);
        Assert.Equal("Traduccion corta", seg.FinalTranslation);
        // Token usage: 2 translations (10+10 prompt, 5+5 comp) + 1 meaning (7+4)
        Assert.Equal(20 + 7, seg.TokenUsage.PromptTokens);
        Assert.Equal(10 + 4, seg.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task Line_never_fits_reported_failed_after_max_attempts()
    {
        using var _ = FakeAuth();

        var q = new Queue<string>([
            "Traduccion larguisima imposible de acortar sin perder sentido",
            "Traduccion larguisima",
            "Traduccion larga",
            "Texto largo",
        ]);
        // No meaning calls because timing never passes
        var m = new Queue<string>();
        using var http = new HttpClient(DualHandler("muse-spark-1.1", q, m));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-c", "Hello world", DurationSeconds: 0.4) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.TimingVerdict);
        Assert.Equal(3, seg.Revisions.Length);
        foreach (QaAttemptReport rev in seg.Revisions)
        {
            Assert.Equal(QaVerdict.Fail, rev.Verdict);
            Assert.Equal(QaVerdict.Fail, rev.TimingVerdict);
            Assert.Equal(0, rev.MeaningPromptTokens);
        }

        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);
        Assert.Equal(0, report.Totals.FinalPassCount);
        Assert.Equal(3, report.Totals.TotalRevisions);
        Assert.Equal("Texto largo", seg.FinalTranslation);
        // 4 model calls (initial + 3 revisions), no meaning tokens
        Assert.Equal(40, seg.TokenUsage.PromptTokens);
        Assert.Equal(20, seg.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task Window_estimated_from_source_when_duration_absent()
    {
        using var _ = FakeAuth();

        var q = new Queue<string>(["Hola", "Hi"]);
        var m = new Queue<string>([MeaningPassJson()]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", q, m));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.0 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-d", "Hi", DurationSeconds: null) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.TimingVerdict);
        Assert.Single(seg.Revisions);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Equal(QaVerdict.Pass, seg.Revisions[0].TimingVerdict);
        Assert.Equal(MeaningVerdict.Pass, seg.Revisions[0].MeaningVerdict);
    }

    [Fact]
    public async Task Regression_sequence_faithful_too_long_then_short_unfaithful_then_faithful_passes()
    {
        using var _ = FakeAuth();

        // Window 1.4s at 15 cps => ~21 chars allowed with 10% => 23 chars (1.54s)
        // Step1: long faithful fails timing (no meaning call)
        // Step2: Una toma mas. (13c incl period? Actually "Una toma mas." 13c) fits but fails meaning
        // Step3: Necesitamos otra toma. (22c) fits and passes meaning
        var translations = new Queue<string>([
            "Necesitamos una toma mas larga que no cabe para forzar revision porque es demasiado extensa",
            "Una toma mas.",
            "Necesitamos otra toma.",
        ]);
        var meanings = new Queue<string>([
            MeaningFailJson(["dropped modality/subject 'Necesitamos' (need) — changes from obligation to simple assertion"], "Candidate omits the subject and modality 'we need', changing the intent."),
            MeaningPassJson("Compact faithful paraphrase preserves meaning."),
        ]);
        var captured = new List<string>();
        bool revisionContainedIssues = false;
        using var http = new HttpClient(DualHandler("muse-spark-1.1", translations, meanings, capturedBodies: captured,
            inspectRevisionBody: body =>
            {
                if (body.Contains("Una toma mas.", StringComparison.Ordinal) || body.Contains("Necesitamos otra toma", StringComparison.Ordinal))
                {
                    // Second revision should contain previous meaning issues
                    if (body.Contains("Necesitamos", StringComparison.Ordinal) && body.Contains("Material meaning issues to fix", StringComparison.Ordinal))
                    {
                        revisionContainedIssues = true;
                    }
                }
            }));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-reg", "We need one more take.", DurationSeconds: 1.4) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        // First attempt: timing fail, not evaluated
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.TimingVerdict);
        Assert.Equal(MeaningVerdict.NotEvaluated, seg.FirstAttempt.MeaningVerdict);
        Assert.Equal(0, seg.FirstAttempt.MeaningPromptTokens);
        Assert.Equal(2, seg.Revisions.Length);
        // Second attempt (first revision): timing pass, meaning fail -> combined fail
        Assert.Equal(QaVerdict.Fail, seg.Revisions[0].Verdict);
        Assert.Equal(QaVerdict.Pass, seg.Revisions[0].TimingVerdict);
        Assert.Equal(MeaningVerdict.Fail, seg.Revisions[0].MeaningVerdict);
        Assert.Contains(seg.Revisions[0].MeaningIssues ?? [], i => i.Contains("Necesitamos", StringComparison.Ordinal));
        Assert.Equal(MeaningGate.PromptTemplateVersion, seg.Revisions[0].MeaningPromptTemplateVersion);
        // Third attempt: both pass -> final pass
        Assert.Equal(QaVerdict.Pass, seg.Revisions[1].Verdict);
        Assert.Equal(QaVerdict.Pass, seg.Revisions[1].TimingVerdict);
        Assert.Equal(MeaningVerdict.Pass, seg.Revisions[1].MeaningVerdict);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Equal("Necesitamos otra toma.", seg.FinalTranslation);
        // Revision feedback was injected
        Assert.True(revisionContainedIssues, "Second revision prompt should contain prior meaning issues");
        // Timing-fail attempt had no meaning tokens; others did
        Assert.Equal(0, seg.FirstAttempt.MeaningTotalTokens);
        Assert.True(seg.Revisions[0].MeaningTotalTokens > 0);
        Assert.True(seg.Revisions[1].MeaningTotalTokens > 0);
        // Token totals: 3 translations (30 prompt, 15 comp) + 2 meanings (14 prompt, 8 comp) = 44 prompt, 23 comp
        Assert.Equal(30 + 14, seg.TokenUsage.PromptTokens);
        Assert.Equal(15 + 8, seg.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task Meaning_feedback_survives_timing_failure_and_is_replaced_by_next_meaning_failure()
    {
        using var _ = FakeAuth();

        var translations = new Queue<string>([
            "Una toma mas.",
            "Necesitamos una toma mas larga que no cabe en la ventana de tiempo",
            "Otra toma.",
            "Necesitamos otra toma.",
        ]);
        var meanings = new Queue<string>([
            MeaningFailJson(["dropped modality"], "Need was lost."),
            MeaningFailJson(["dropped subject"], "Subject was lost."),
            MeaningPassJson(),
        ]);
        var revisionPrompts = new List<string>();
        using var http = new HttpClient(DualHandler("muse-spark-1.1", translations, meanings,
            inspectRevisionBody: body =>
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                revisionPrompts.Add(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            }));
        using var client = new MetaClient(http);
        var svc = new QaService(client, new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 });

        QaReport report = await svc.RunAsync([new QaSegmentInput("feedback", "We need one more take.", 1.4)], "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(MeaningVerdict.Fail, seg.FirstAttempt.MeaningVerdict);
        Assert.Equal(3, seg.Revisions.Length);
        Assert.Equal(MeaningVerdict.NotEvaluated, seg.Revisions[0].MeaningVerdict);
        Assert.Equal(MeaningVerdict.Fail, seg.Revisions[1].MeaningVerdict);
        Assert.Equal(MeaningVerdict.Pass, seg.Revisions[2].MeaningVerdict);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Equal(3, revisionPrompts.Count);
        Assert.Contains("dropped modality", revisionPrompts[0], StringComparison.Ordinal);
        Assert.Contains("dropped modality", revisionPrompts[1], StringComparison.Ordinal);
        Assert.Contains("dropped subject", revisionPrompts[2], StringComparison.Ordinal);
        Assert.DoesNotContain("dropped modality", revisionPrompts[2], StringComparison.Ordinal);
        Assert.All(revisionPrompts, prompt =>
        {
            Assert.Contains("Fix the listed meaning issues and restore lost meaning", prompt, StringComparison.Ordinal);
            Assert.Contains("without shortening further than necessary", prompt, StringComparison.Ordinal);
            Assert.Contains("all listed issues must be fixed", prompt, StringComparison.Ordinal);
            Assert.Contains($"at most {seg.FirstAttempt.MaxChars} characters", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("Rewrite it shorter", prompt, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"pass\"")]
    public async Task Non_object_meaning_response_allows_revision(string raw)
    {
        using var _ = FakeAuth();

        var translations = new Queue<string>(["Hola.", "Hola."]);
        var meanings = new Queue<string>([raw, MeaningPassJson()]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", translations, meanings));
        using var client = new MetaClient(http);
        var svc = new QaService(client, new QaOptions { MaxAttempts = 1 });

        QaReport report = await svc.RunAsync([new QaSegmentInput("invalid-root", "Hello.", 4.2)], "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(MeaningVerdict.Fail, seg.FirstAttempt.MeaningVerdict);
        Assert.Equal(new[] { "unparseable meaning response" }, seg.FirstAttempt.MeaningIssues);
        Assert.Single(seg.Revisions);
        Assert.Equal(MeaningVerdict.Pass, seg.Revisions[0].MeaningVerdict);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Revision_without_meaning_issues_still_requests_shorter_translation(string? issue)
    {
        using var _ = FakeAuth();

        string? userPrompt = null;
        using var http = new HttpClient(DualHandler("muse-spark-1.1", new Queue<string>(["Hola."]), new Queue<string>(),
            inspectRevisionBody: body =>
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                userPrompt = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            }));
        using var client = new MetaClient(http);

        await client.ReviseTranslationForTimingAsync("Hello.", "Hola a todos.", "en", "es", 1.0, 15,
            meaningIssues: issue is null ? null : [issue]);

        Assert.NotNull(userPrompt);
        Assert.Contains("Rewrite it shorter, at most 15 characters, preserving meaning.", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Material meaning issues", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("restore lost meaning", userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regression_exhaustion_ends_failed()
    {
        using var _ = FakeAuth();

        var translations = new Queue<string>([
            "Necesitamos una toma mas larga que no cabe para forzar revision porque es demasiado extensa",
            "Una toma mas.",
            "Una toma mas.",
            "Una toma mas.",
        ]);
        var meanings = new Queue<string>([
            MeaningFailJson(["dropped modality Necesitamos"], "Omits need."),
            MeaningFailJson(["dropped modality Necesitamos"], "Still omits."),
            MeaningFailJson(["dropped modality Necesitamos"], "Still omits."),
        ]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", translations, meanings));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-exh", "We need one more take.", DurationSeconds: 1.4) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.TimingVerdict);
        Assert.Equal(2, seg.Revisions.Length);
        Assert.All(seg.Revisions, r =>
        {
            Assert.Equal(QaVerdict.Fail, r.Verdict);
            Assert.Equal(MeaningVerdict.Fail, r.MeaningVerdict);
        });
        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);
        Assert.Equal("Una toma mas.", seg.FinalTranslation);
    }

    [Fact]
    public async Task Skip_meaning_when_timing_fails()
    {
        using var _ = FakeAuth();

        var translations = new Queue<string>([
            "Larga traduccion que no cabe en la ventana de tiempo asignada por el director",
            "Otra larga que tampoco cabe",
        ]);
        var meanings = new Queue<string>(); // should not be consumed
        int meaningCalls = 0;
        var handler = new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            bool isMeaning = body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal);
            if (isMeaning)
            {
                meaningCalls++;
            }

            if (isMeaning)
            {
                if (meanings.Count == 0) throw new InvalidOperationException("Meaning should not be called on timing fail");
                return ChatResponse("muse-spark-1.1", meanings.Dequeue(), 7, 4);
            }

            if (translations.Count == 0) throw new InvalidOperationException("No translations");
            return ChatResponse("muse-spark-1.1", translations.Dequeue(), 10, 5);
        });
        using var http = new HttpClient(handler);
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 1, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-skip", "Hello", DurationSeconds: 0.4) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        Assert.Equal(0, meaningCalls);
        Assert.Single(report.Segments[0].Revisions);
        Assert.All(new[] { report.Segments[0].FirstAttempt }.Concat(report.Segments[0].Revisions), a =>
        {
            Assert.Equal(QaVerdict.Fail, a.Verdict);
            Assert.Equal(0, a.MeaningPromptTokens);
            Assert.Equal(MeaningVerdict.NotEvaluated, a.MeaningVerdict);
            Assert.Null(a.MeaningIssues);
        });
    }

    [Fact]
    public async Task Timing_fail_attempt_reports_NotEvaluated_with_zero_meaning_calls()
    {
        using var _ = FakeAuth();

        // Initial and every revision overrun a 1.0s window: meaning must never be called.
        var translations = new Queue<string>([
            "Traduccion inicial muchisimo mas larga que la ventana permitida de un segundo",
            "Primera revision tambien demasiado larga para caber en la ventana de un segundo",
            "Segunda revision igual de larga y por lo tanto sigue sin caber en la ventana",
        ]);
        int meaningCalls = 0;
        var handler = new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                meaningCalls++;
                throw new InvalidOperationException("Meaning must not be called when timing fails.");
            }

            if (translations.Count == 0)
            {
                throw new InvalidOperationException("No more canned translations queued.");
            }

            return ChatResponse("muse-spark-1.1", translations.Dequeue(), 10, 5);
        });

        using var http = new HttpClient(handler);
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.0 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-not-eval", "Hello there.", DurationSeconds: 1.0) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(0, meaningCalls);
        Assert.Equal(2, seg.Revisions.Length);
        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);

        var allAttempts = new[] { seg.FirstAttempt }.Concat(seg.Revisions).ToArray();
        Assert.Equal(3, allAttempts.Length);
        foreach (QaAttemptReport a in allAttempts)
        {
            Assert.Equal(QaVerdict.Fail, a.TimingVerdict);
            Assert.Equal(MeaningVerdict.NotEvaluated, a.MeaningVerdict);
            // Combined verdict stays Fail
            Assert.Equal(QaVerdict.Fail, a.Verdict);
            // No meaning provenance or usage recorded
            Assert.Equal(0, a.MeaningPromptTokens);
            Assert.Equal(0, a.MeaningCompletionTokens);
            Assert.Equal(0, a.MeaningTotalTokens);
            Assert.Null(a.MeaningModelId);
            Assert.Null(a.MeaningPromptTemplateVersion);
            Assert.Null(a.MeaningTimestampUtc);
            Assert.Null(a.MeaningExplanation);
        }

        // NotEvaluated serializes as a distinct string in the report
        string json = JsonSerializer.Serialize(seg);
        Assert.Contains("\"NotEvaluated\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"meaning_verdict\":\"Pass\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Meaning_error_initial_is_terminal_and_does_not_consume_revision()
    {
        using var _ = FakeAuth();

        var translations = new Queue<string>(["Hola."]);
        var meaningExceptions = new Queue<Exception>([new HttpRequestException("transport timeout")]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", translations, new Queue<string>(), meaningExceptions: meaningExceptions));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-err", "Hello.", DurationSeconds: 4.2) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Pass, seg.FirstAttempt.TimingVerdict);
        Assert.Equal(MeaningVerdict.Error, seg.FirstAttempt.MeaningVerdict);
        Assert.Contains("transport timeout", seg.FirstAttempt.MeaningExplanation ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(seg.Revisions);
        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);
    }

    [Fact]
    public async Task Meaning_error_on_revision_is_terminal()
    {
        using var _ = FakeAuth();

        var translations = new Queue<string>([
            "Traduccion larguisima que no cabe en la ventana asignada definitivamente",
            "Hola.",
        ]);
        var meanings = new Queue<string>([MeaningPassJson()]);
        // Second meaning call (for revision) will throw
        var meaningExceptions = new Queue<Exception>();
        int meaningCallCount = 0;
        var handler = new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            bool isMeaning = body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal);
            if (isMeaning)
            {
                meaningCallCount++;
                if (meaningCallCount == 1)
                {
                    // This path shouldn't be hit for initial because initial timing fails
                    throw new InvalidOperationException("Initial meaning should not be called");
                }

                if (meaningCallCount == 2)
                {
                    throw new HttpRequestException("API 502 bad gateway");
                }
            }

            if (isMeaning)
            {
                return ChatResponse("muse-spark-1.1", meanings.Dequeue(), 7, 4);
            }

            return ChatResponse("muse-spark-1.1", translations.Dequeue(), 10, 5);
        });
        // For this test, first translation fails timing, so first meaning not called. Second translation passes timing, then meaning throws.
        // We need to adjust handler to count only meaning calls that actually happen.
        // So we replace with dedicated counts
        var translations2 = new Queue<string>([
            "Traduccion larguisima que no cabe en la ventana asignada definitivamente",
            "Hola.",
        ]);
        int revMeaningCalls = 0;
        var handler2 = new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            bool isMeaning = body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal);
            if (isMeaning)
            {
                revMeaningCalls++;
                if (revMeaningCalls == 1)
                {
                    throw new HttpRequestException("API 502 bad gateway");
                }

                return ChatResponse("muse-spark-1.1", MeaningPassJson(), 7, 4);
            }

            return ChatResponse("muse-spark-1.1", translations2.Dequeue(), 10, 5);
        });
        using var http = new HttpClient(handler2);
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-err2", "Hello.", DurationSeconds: 4.2) };
        // First fails timing, second passes timing then meaning errors
        // But first translation "Traduccion larg..." length? Let's pick long enough to fail 4.2s? 4.2s*15=63 chars, so long string 70 chars fails
        // Second "Hola." passes
        QaReport report = await svc.RunAsync(segs, "en", "es");
        QaSegmentReport seg = report.Segments[0];
        Assert.Single(seg.Revisions);
        Assert.Equal(MeaningVerdict.Error, seg.Revisions[0].MeaningVerdict);
        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);
        Assert.Contains("502", seg.Revisions[0].MeaningExplanation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normalization_pass_with_issues_becomes_fail()
    {
        using var _ = FakeAuth();

        // Initial passes both? No, meaning normalized to fail => combined fail => needs revision.
        // So after normalization, firstAttempt is fail, then revision.
        var translations = new Queue<string>(["Hola.", "Hola de nuevo."]);
        // First meaning says pass with issues => normalized to fail; second meaning passes cleanly? But we have MaxAttempts=1 so revisions=1 and final fail? Actually if second passes, final pass.
        // Make second meaning fail too so final fail deterministic.
        var meanings = new Queue<string>([
            JsonSerializer.Serialize(new { verdict = "pass", issues = new[] { "dropped subject" }, explanation = "Claims pass but lists issues" }),
            JsonSerializer.Serialize(new { verdict = "fail", issues = new[] { "still off" }, explanation = "Still off." }),
        ]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", translations, meanings));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 1, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-norm", "Hello.", DurationSeconds: 4.2) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        // First meaning normalized to fail
        Assert.Equal(MeaningVerdict.Fail, seg.FirstAttempt.MeaningVerdict);
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Single(seg.Revisions);
        // Second attempt also fails meaning, so final fail
        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);
    }

    [Fact]
    public async Task Idiomatic_faithful_passes_both_gates()
    {
        using var _ = FakeAuth();

        var translations = new Queue<string>(["Nos hace falta una toma mas."]);
        var meanings = new Queue<string>([MeaningPassJson("Idiomatic but faithful paraphrase.")]);
        using var http = new HttpClient(DualHandler("muse-spark-1.1", translations, meanings));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-idiom", "We need one more take.", DurationSeconds: 2.0) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Pass, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Pass, seg.FirstAttempt.TimingVerdict);
        Assert.Equal(MeaningVerdict.Pass, seg.FirstAttempt.MeaningVerdict);
        Assert.Empty(seg.Revisions);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Equal("Nos hace falta una toma mas.", seg.FinalTranslation);
        Assert.Equal(MeaningGate.PromptTemplateVersion, seg.FirstAttempt.MeaningPromptTemplateVersion);
        Assert.False(string.IsNullOrWhiteSpace(seg.FirstAttempt.MeaningModelId));
    }

    private static bool HasRealApiKey
    {
        get
        {
            string? k = Environment.GetEnvironmentVariable("META_API_KEY") ?? Environment.GetEnvironmentVariable("META_MODEL_API_KEY");
            return !string.IsNullOrWhiteSpace(k) && k.Trim() != "test-fake-key" && k.Trim().StartsWith("LLM_", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Live_qa_runs_only_with_api_key()
    {
        if (!HasRealApiKey)
        {
            return;
        }

        using var client = new MetaClient();
        var svc = new QaService(client, new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.10 });

        var segs = new[]
        {
            new QaSegmentInput("live-01", "Hello, world.", DurationSeconds: 2.0),
            new QaSegmentInput("live-02", "Action!", DurationSeconds: 0.5),
        };

        QaReport report = await svc.RunAsync(segs, "en", "es");
        Assert.Equal(2, report.Segments.Length);
        Assert.True(report.Totals.TotalTokens > 0);
        foreach (QaSegmentReport seg in report.Segments)
        {
            Assert.False(string.IsNullOrWhiteSpace(seg.FinalTranslation));
        }
    }
}
