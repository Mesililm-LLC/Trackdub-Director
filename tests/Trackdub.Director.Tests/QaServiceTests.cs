using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class QaServiceTests
{
    private const string Model = "muse-spark-1.1";
    private const int TranslationPrompt = 10;
    private const int TranslationCompletion = 5;
    private const int MeaningPrompt = 7;
    private const int MeaningCompletion = 4;

    private static HttpResponseMessage ChatResponse(string content, int promptTokens, int completionTokens)
    {
        var payload = new
        {
            id = $"chatcmpl-test-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = Model,
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
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// Scripted Meta endpoint. A request carrying <see cref="MeaningGate.MeaningMarker"/> is a meaning check;
    /// any other request is a translation that dequeues <see cref="Translations"/>. Meaning calls are counted,
    /// so tests can assert that the gate was skipped without relying on a thrown exception.
    /// </summary>
    private sealed class ScriptedMeta
    {
        private int _meaningCalls;

        public Queue<string> Translations { get; init; } = new();

        public Queue<string> MeaningJsons { get; init; } = new();

        public Queue<Exception> MeaningExceptions { get; init; } = new();

        public Action<string>? InspectRevisionBody { get; init; }

        public int MeaningCalls => _meaningCalls;

        public HttpMessageHandler ToHandler() => new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = await ReadBodyAsync(request, ct);
            Assert.Contains("/chat/completions", request.RequestUri?.ToString() ?? string.Empty);

            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                return NextMeaningResponse();
            }

            if (body.Contains("Revise", StringComparison.Ordinal))
            {
                InspectRevisionBody?.Invoke(body);
            }

            if (Translations.Count == 0)
            {
                throw new InvalidOperationException("No more canned translations queued.");
            }

            return ChatResponse(Translations.Dequeue(), TranslationPrompt, TranslationCompletion);
        });

        private HttpResponseMessage NextMeaningResponse()
        {
            Interlocked.Increment(ref _meaningCalls);
            if (MeaningExceptions.Count > 0)
            {
                throw MeaningExceptions.Dequeue();
            }

            if (MeaningJsons.Count == 0)
            {
                throw new InvalidOperationException("No more canned meaning JSON queued.");
            }

            return ChatResponse(MeaningJsons.Dequeue(), MeaningPrompt, MeaningCompletion);
        }
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
        var b = EnvShim("META_MODEL", Model);
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

    private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
        => request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

    private static string UserPrompt(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    }

    private static async Task<QaReport> RunSegments(
        HttpMessageHandler handler,
        QaOptions options,
        QaSegmentInput[] segments,
        CancellationToken ct = default)
    {
        using var http = new HttpClient(handler);
        using var client = new MetaClient(http);
        return await new QaService(client, options).RunAsync(segments, "en", "es", ct);
    }

    private static IEnumerable<QaAttemptReport> AllAttempts(QaSegmentReport seg)
        => new[] { seg.FirstAttempt }.Concat(seg.Revisions);

    /// <summary>
    /// An attempt that failed timing: meaning was never evaluated and carries no provenance.
    /// </summary>
    private static void AssertTimingOnlyFailure(QaAttemptReport a)
    {
        Assert.Equal((QaVerdict.Fail, QaVerdict.Fail, MeaningVerdict.NotEvaluated), (a.Verdict, a.TimingVerdict, a.MeaningVerdict));
        Assert.Equal((0, 0, 0), (a.MeaningPromptTokens, a.MeaningCompletionTokens, a.MeaningTotalTokens));
        Assert.All(
            new object?[] { a.MeaningIssues, a.MeaningModelId, a.MeaningPromptTemplateVersion, a.MeaningTimestampUtc, a.MeaningExplanation },
            value => Assert.Null(value));
    }

    /// <summary>
    /// A meaning check that ran and passed: the attempt records the gate's provenance.
    /// </summary>
    private static void AssertMeaningProvenance(QaAttemptReport a)
    {
        Assert.Equal((MeaningPrompt, MeaningCompletion, MeaningGate.PromptTemplateVersion, "api.meta.ai"), (a.MeaningPromptTokens, a.MeaningCompletionTokens, a.MeaningPromptTemplateVersion, a.MeaningEndpointHost));
        Assert.False(string.IsNullOrWhiteSpace(a.MeaningModelId));
        Assert.False(string.IsNullOrWhiteSpace(a.MeaningTimestampUtc));
    }

    private static void AssertMeaningTokensRecorded(QaAttemptReport first, QaAttemptReport second)
    {
        Assert.True(first.MeaningTotalTokens > 0);
        Assert.True(second.MeaningTotalTokens > 0);
    }

    [Fact]
    public async Task Line_passes_first_try()
    {
        using var _ = FakeAuth();

        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>(["Hola."]),
            MeaningJsons = new Queue<string>([MeaningPassJson()]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-a", "Hello.", DurationSeconds: 4.2)]);

        Assert.Single(report.Segments);
        Assert.Equal((1, 1, 1, 0), (report.Totals.TotalSegments, report.Totals.FirstPassCount, report.Totals.FinalPassCount, report.Totals.TotalRevisions));
        Assert.True(report.Totals.TotalTokens > 0);

        QaSegmentReport seg = report.Segments[0];
        QaAttemptReport first = seg.FirstAttempt;
        Assert.Equal((QaVerdict.Pass, QaVerdict.Pass, MeaningVerdict.Pass, QaVerdict.Pass), (first.Verdict, first.TimingVerdict, first.MeaningVerdict, seg.FinalVerdict));
        Assert.Empty(first.MeaningIssues ?? []);
        Assert.Empty(seg.Revisions);
        AssertMeaningProvenance(first);

        Assert.Equal(("Hola.", "Hola."), (first.Translation, seg.FinalTranslation));

        // TokenUsage sums translation + meaning.
        Assert.Equal((TranslationPrompt + MeaningPrompt, TranslationCompletion + MeaningCompletion), (seg.TokenUsage.PromptTokens, seg.TokenUsage.CompletionTokens));
    }

    [Fact]
    public async Task Line_fails_then_revised_and_passes()
    {
        using var _ = FakeAuth();

        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Esta es una traduccion muy larga que no cabe",
                "Traduccion corta",
            ]),
            MeaningJsons = new Queue<string>([MeaningPassJson("Short but faithful.")]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-b", "This is a long source line that will overrun", DurationSeconds: 1.0)]);

        QaSegmentReport seg = report.Segments[0];
        QaAttemptReport first = seg.FirstAttempt;
        Assert.Equal((QaVerdict.Fail, QaVerdict.Fail, MeaningVerdict.NotEvaluated), (first.Verdict, first.TimingVerdict, first.MeaningVerdict));
        Assert.Equal(0, first.MeaningPromptTokens);
        Assert.Single(seg.Revisions);

        QaAttemptReport revision = seg.Revisions[0];
        Assert.Equal((QaVerdict.Pass, QaVerdict.Pass, MeaningVerdict.Pass), (revision.Verdict, revision.TimingVerdict, revision.MeaningVerdict));
        Assert.Equal((QaVerdict.Pass, 0, 1, 1, "Traduccion corta"), (seg.FinalVerdict, report.Totals.FirstPassCount, report.Totals.FinalPassCount, report.Totals.TotalRevisions, seg.FinalTranslation));

        // Token usage: 2 translations (10+10 prompt, 5+5 comp) + 1 meaning (7+4).
        Assert.Equal((20 + MeaningPrompt, 10 + MeaningCompletion), (seg.TokenUsage.PromptTokens, seg.TokenUsage.CompletionTokens));
    }

    [Fact]
    public async Task Line_never_fits_reported_failed_after_max_attempts()
    {
        using var _ = FakeAuth();

        // No meaning calls because timing never passes.
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Traduccion larguisima imposible de acortar sin perder sentido",
                "Traduccion larguisima",
                "Traduccion larga",
                "Texto largo",
            ]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-c", "Hello world", DurationSeconds: 0.4)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal((QaVerdict.Fail, QaVerdict.Fail), (seg.FirstAttempt.Verdict, seg.FirstAttempt.TimingVerdict));
        Assert.Equal(3, seg.Revisions.Length);
        foreach (QaAttemptReport rev in seg.Revisions)
        {
            Assert.Equal((QaVerdict.Fail, QaVerdict.Fail), (rev.Verdict, rev.TimingVerdict));
            Assert.Equal(0, rev.MeaningPromptTokens);
        }

        Assert.Equal((QaVerdict.Fail, 0, 3, "Texto largo"), (seg.FinalVerdict, report.Totals.FinalPassCount, report.Totals.TotalRevisions, seg.FinalTranslation));

        // 4 model calls (initial + 3 revisions), no meaning tokens.
        Assert.Equal((40, 20), (seg.TokenUsage.PromptTokens, seg.TokenUsage.CompletionTokens));
    }

    [Fact]
    public async Task Window_estimated_from_source_when_duration_absent()
    {
        using var _ = FakeAuth();

        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>(["Hola", "Hi"]),
            MeaningJsons = new Queue<string>([MeaningPassJson()]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.0 },
            [new QaSegmentInput("seg-d", "Hi", DurationSeconds: null)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal((QaVerdict.Fail, QaVerdict.Fail), (seg.FirstAttempt.Verdict, seg.FirstAttempt.TimingVerdict));
        Assert.Single(seg.Revisions);
        Assert.Equal((QaVerdict.Pass, QaVerdict.Pass, MeaningVerdict.Pass), (seg.FinalVerdict, seg.Revisions[0].TimingVerdict, seg.Revisions[0].MeaningVerdict));
    }

    [Fact]
    public async Task Regression_sequence_faithful_too_long_then_short_unfaithful_then_faithful_passes()
    {
        using var _ = FakeAuth();

        // Window 1.4s at 15 cps allows about 23 characters with 10% tolerance.
        // Attempt 1: the long faithful line overruns, so meaning is not checked.
        // Attempt 2: "Una toma mas." fits but drops the modality, so meaning fails.
        // Attempt 3: "Necesitamos otra toma." fits and passes meaning.
        bool revisionContainedIssues = false;
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Necesitamos una toma mas larga que no cabe para forzar revision porque es demasiado extensa",
                "Una toma mas.",
                "Necesitamos otra toma.",
            ]),
            MeaningJsons = new Queue<string>([
                MeaningFailJson(["dropped modality/subject 'Necesitamos' (need) — changes from obligation to simple assertion"], "Candidate omits the subject and modality 'we need', changing the intent."),
                MeaningPassJson("Compact faithful paraphrase preserves meaning."),
            ]),
            InspectRevisionBody = body => revisionContainedIssues |= body.Contains("Material meaning issues to fix", StringComparison.Ordinal)
                && body.Contains("Necesitamos", StringComparison.Ordinal)
                && (body.Contains("Una toma mas.", StringComparison.Ordinal) || body.Contains("Necesitamos otra toma", StringComparison.Ordinal)),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-reg", "We need one more take.", DurationSeconds: 1.4)]);

        QaSegmentReport seg = report.Segments[0];
        QaAttemptReport first = seg.FirstAttempt;
        Assert.Equal((QaVerdict.Fail, QaVerdict.Fail, MeaningVerdict.NotEvaluated), (first.Verdict, first.TimingVerdict, first.MeaningVerdict));
        Assert.Equal((0, 0), (first.MeaningPromptTokens, first.MeaningTotalTokens));
        Assert.Equal(2, seg.Revisions.Length);

        QaAttemptReport unfaithful = seg.Revisions[0];
        Assert.Equal((QaVerdict.Fail, QaVerdict.Pass, MeaningVerdict.Fail), (unfaithful.Verdict, unfaithful.TimingVerdict, unfaithful.MeaningVerdict));
        Assert.Contains(unfaithful.MeaningIssues ?? [], i => i.Contains("Necesitamos", StringComparison.Ordinal));
        Assert.Equal(MeaningGate.PromptTemplateVersion, unfaithful.MeaningPromptTemplateVersion);

        QaAttemptReport faithful = seg.Revisions[1];
        Assert.Equal((QaVerdict.Pass, QaVerdict.Pass, MeaningVerdict.Pass), (faithful.Verdict, faithful.TimingVerdict, faithful.MeaningVerdict));
        Assert.Equal((QaVerdict.Pass, "Necesitamos otra toma."), (seg.FinalVerdict, seg.FinalTranslation));
        Assert.True(revisionContainedIssues, "Second revision prompt should contain prior meaning issues");
        AssertMeaningTokensRecorded(unfaithful, faithful);

        // 3 translations (30 prompt, 15 comp) + 2 meanings (14 prompt, 8 comp).
        Assert.Equal((30 + 2 * MeaningPrompt, 15 + 2 * MeaningCompletion), (seg.TokenUsage.PromptTokens, seg.TokenUsage.CompletionTokens));
    }

    [Fact]
    public async Task Meaning_feedback_survives_timing_failure_and_is_replaced_by_next_meaning_failure()
    {
        using var _ = FakeAuth();

        var revisionPrompts = new List<string>();
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Una toma mas.",
                "Necesitamos una toma mas larga que no cabe en la ventana de tiempo",
                "Otra toma.",
                "Necesitamos otra toma.",
            ]),
            MeaningJsons = new Queue<string>([
                MeaningFailJson(["dropped modality"], "Need was lost."),
                MeaningFailJson(["dropped subject"], "Subject was lost."),
                MeaningPassJson(),
            ]),
            InspectRevisionBody = body => revisionPrompts.Add(UserPrompt(body)),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("feedback", "We need one more take.", 1.4)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(3, seg.Revisions.Length);
        Assert.Equal(
            (MeaningVerdict.Fail, MeaningVerdict.NotEvaluated, MeaningVerdict.Fail, MeaningVerdict.Pass, QaVerdict.Pass),
            (seg.FirstAttempt.MeaningVerdict, seg.Revisions[0].MeaningVerdict, seg.Revisions[1].MeaningVerdict, seg.Revisions[2].MeaningVerdict, seg.FinalVerdict));

        string[] requiredPhrases =
        [
            "Fix the listed meaning issues and restore lost meaning",
            "without shortening further than necessary",
            "restore the lost meaning",
            $"at most {seg.FirstAttempt.MaxChars} characters",
        ];
        Assert.Equal(3, revisionPrompts.Count);
        Assert.Contains("dropped modality", revisionPrompts[0], StringComparison.Ordinal);
        Assert.Contains("dropped modality", revisionPrompts[1], StringComparison.Ordinal);

        string lastPrompt = revisionPrompts[2];
        Assert.Contains("dropped subject", lastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("dropped modality", lastPrompt, StringComparison.Ordinal);

        Assert.All(revisionPrompts, prompt => Assert.All(requiredPhrases, phrase => Assert.Contains(phrase, prompt, StringComparison.Ordinal)));
        Assert.All(revisionPrompts, prompt => Assert.DoesNotContain("Rewrite it shorter", prompt, StringComparison.Ordinal));
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

        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>(["Hola.", "Hola."]),
            MeaningJsons = new Queue<string>([raw, MeaningPassJson()]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 1 },
            [new QaSegmentInput("invalid-root", "Hello.", 4.2)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(MeaningVerdict.Fail, seg.FirstAttempt.MeaningVerdict);
        Assert.Equal(new[] { "unparseable meaning response" }, seg.FirstAttempt.MeaningIssues);
        Assert.Single(seg.Revisions);

        QaAttemptReport revision = seg.Revisions[0];
        Assert.Equal((MeaningVerdict.Pass, QaVerdict.Pass), (revision.MeaningVerdict, seg.FinalVerdict));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Revision_without_meaning_issues_still_requests_shorter_translation(string? issue)
    {
        using var _ = FakeAuth();

        string? userPrompt = null;
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>(["Hola."]),
            InspectRevisionBody = body => userPrompt = UserPrompt(body),
        };
        using var http = new HttpClient(meaning.ToHandler());
        using var client = new MetaClient(http);

        await client.ReviseTranslationForTimingAsync(
            new MetaClient.TimingRevisionRequest(
                "Hello.", "Hola a todos.", "en", "es", 1.0, 15,
                MeaningIssues: issue is null ? null : [issue]));

        string prompt = userPrompt ?? string.Empty;
        Assert.Contains("Rewrite it shorter, at most 15 characters, preserving meaning.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Material meaning issues", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("restore lost meaning", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regression_exhaustion_ends_failed()
    {
        using var _ = FakeAuth();

        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Necesitamos una toma mas larga que no cabe para forzar revision porque es demasiado extensa",
                "Una toma mas.",
                "Una toma mas.",
                "Una toma mas.",
            ]),
            MeaningJsons = new Queue<string>([
                MeaningFailJson(["dropped modality Necesitamos"], "Omits need."),
                MeaningFailJson(["dropped modality Necesitamos"], "Still omits."),
                MeaningFailJson(["dropped modality Necesitamos"], "Still omits."),
            ]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-exh", "We need one more take.", DurationSeconds: 1.4)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal((QaVerdict.Fail, 2), (seg.FirstAttempt.TimingVerdict, seg.Revisions.Length));
        Assert.All(seg.Revisions, r => Assert.Equal((QaVerdict.Fail, MeaningVerdict.Fail), (r.Verdict, r.MeaningVerdict)));
        Assert.Equal((QaVerdict.Fail, "Una toma mas."), (seg.FinalVerdict, seg.FinalTranslation));
    }

    [Fact]
    public async Task Skip_meaning_when_timing_fails()
    {
        using var _ = FakeAuth();

        // No meaning JSON is queued: a meaning call here would surface as an error and fail the counter check.
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Larga traduccion que no cabe en la ventana de tiempo asignada por el director",
                "Otra larga que tampoco cabe",
            ]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 1, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-skip", "Hello", DurationSeconds: 0.4)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(0, meaning.MeaningCalls);
        Assert.Single(seg.Revisions);
        Assert.All(AllAttempts(seg), AssertTimingOnlyFailure);
    }

    [Fact]
    public async Task Timing_fail_attempt_reports_NotEvaluated_with_zero_meaning_calls()
    {
        using var _ = FakeAuth();

        // Initial and every revision overrun a 1.0s window: meaning must never be called.
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Traduccion inicial muchisimo mas larga que la ventana permitida de un segundo",
                "Primera revision tambien demasiado larga para caber en la ventana de un segundo",
                "Segunda revision igual de larga y por lo tanto sigue sin caber en la ventana",
            ]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.0 },
            [new QaSegmentInput("seg-not-eval", "Hello there.", DurationSeconds: 1.0)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(0, meaning.MeaningCalls);
        Assert.Equal(2, seg.Revisions.Length);
        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);

        QaAttemptReport[] allAttempts = AllAttempts(seg).ToArray();
        Assert.Equal(3, allAttempts.Length);
        Assert.All(allAttempts, AssertTimingOnlyFailure);

        // NotEvaluated serializes as a distinct string in the report.
        string json = JsonSerializer.Serialize(seg);
        Assert.Contains("\"NotEvaluated\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"meaning_verdict\":\"Pass\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_propagates_and_stops_the_segment_loop()
    {
        using var _ = FakeAuth();

        // 8 segments so a swallowed cancellation would keep issuing API calls past the cancel point.
        var segs = Enumerable.Range(1, 8)
            .Select(i => new QaSegmentInput($"seg-{i:00}", "Hello.", DurationSeconds: 4.2))
            .ToArray();

        int translateCalls = 0;
        int meaningCalls = 0;
        using var cts = new CancellationTokenSource();
        var handler = new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = await ReadBodyAsync(request, ct);
            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                // Cancellation arrives while the meaning check for segment 1 is in flight.
                Interlocked.Increment(ref meaningCalls);
                await cts.CancelAsync();
                throw new TaskCanceledException("A task was canceled.");
            }

            Interlocked.Increment(ref translateCalls);
            return ChatResponse("Hola.", TranslationPrompt, TranslationCompletion);
        });

        // Caller cancellation must surface, not be converted into a per-segment MeaningVerdict.Error
        // that lets the loop march on through the remaining segments.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunSegments(handler, new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 }, segs, cts.Token));

        // The loop stopped at segment 1: one translate plus one meaning call, no segment 2+ work.
        Assert.Equal((1, 1), (translateCalls, meaningCalls));
    }

    [Fact]
    public async Task Caller_cancelled_token_on_meaning_is_not_recorded_as_meaning_error()
    {
        using var _ = FakeAuth();

        using var cts = new CancellationTokenSource();
        var handler = new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = await ReadBodyAsync(request, ct);
            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                await cts.CancelAsync();
                throw new TaskCanceledException("A task was canceled.");
            }

            return ChatResponse("Hola.", TranslationPrompt, TranslationCompletion);
        });

        var segs = new[] { new QaSegmentInput("seg-cancel", "Hello.", DurationSeconds: 4.2) };

        // Cancelled while the meaning check is in flight: this is cancellation, not a meaning Error.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunSegments(handler, new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 }, segs, cts.Token));
    }

    [Fact]
    public async Task Meaning_error_initial_is_terminal_and_does_not_consume_revision()
    {
        using var _ = FakeAuth();

        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>(["Hola."]),
            MeaningExceptions = new Queue<Exception>([new HttpRequestException("transport timeout")]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-err", "Hello.", DurationSeconds: 4.2)]);

        QaSegmentReport seg = report.Segments[0];
        QaAttemptReport first = seg.FirstAttempt;
        Assert.Equal((QaVerdict.Fail, QaVerdict.Pass, MeaningVerdict.Error, QaVerdict.Fail), (first.Verdict, first.TimingVerdict, first.MeaningVerdict, seg.FinalVerdict));
        Assert.Contains("transport timeout", first.MeaningExplanation ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(seg.Revisions);
    }

    [Fact]
    public async Task Meaning_error_on_revision_is_terminal()
    {
        using var _ = FakeAuth();

        // Initial translation overruns the 4.2s window (63c budget at 15cps), so timing fails and
        // the meaning check is skipped. Revision "Hola." fits, so meaning runs and the transport
        // failure is recorded as Error: combined Fail, terminal, no further revisions.
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>([
                "Traduccion larguisima que no cabe en la ventana asignada definitivamente",
                "Hola.",
            ]),
            MeaningExceptions = new Queue<Exception>([new HttpRequestException("API 502 bad gateway")]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-err2", "Hello.", DurationSeconds: 4.2)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal((1, QaVerdict.Fail), (meaning.MeaningCalls, seg.FinalVerdict));
        Assert.Single(seg.Revisions);

        QaAttemptReport revision = seg.Revisions[0];
        Assert.Equal((MeaningVerdict.NotEvaluated, MeaningVerdict.Error), (seg.FirstAttempt.MeaningVerdict, revision.MeaningVerdict));
        Assert.Contains("502", revision.MeaningExplanation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normalization_pass_with_issues_becomes_fail()
    {
        using var _ = FakeAuth();

        // The first meaning reply says pass but lists issues, so it normalizes to fail and a revision runs.
        // The revision's meaning also fails, so the final verdict is fail.
        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>(["Hola.", "Hola de nuevo."]),
            MeaningJsons = new Queue<string>([
                JsonSerializer.Serialize(new { verdict = "pass", issues = new[] { "dropped subject" }, explanation = "Claims pass but lists issues" }),
                JsonSerializer.Serialize(new { verdict = "fail", issues = new[] { "still off" }, explanation = "Still off." }),
            ]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 1, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-norm", "Hello.", DurationSeconds: 4.2)]);

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal((MeaningVerdict.Fail, QaVerdict.Fail), (seg.FirstAttempt.MeaningVerdict, seg.FirstAttempt.Verdict));
        Assert.Single(seg.Revisions);
        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);
    }

    [Fact]
    public async Task Idiomatic_faithful_passes_both_gates()
    {
        using var _ = FakeAuth();

        var meaning = new ScriptedMeta
        {
            Translations = new Queue<string>(["Nos hace falta una toma mas."]),
            MeaningJsons = new Queue<string>([MeaningPassJson("Idiomatic but faithful paraphrase.")]),
        };
        QaReport report = await RunSegments(
            meaning.ToHandler(),
            new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 },
            [new QaSegmentInput("seg-idiom", "We need one more take.", DurationSeconds: 2.0)]);

        QaSegmentReport seg = report.Segments[0];
        QaAttemptReport first = seg.FirstAttempt;
        Assert.Equal((QaVerdict.Pass, QaVerdict.Pass, MeaningVerdict.Pass), (first.Verdict, first.TimingVerdict, first.MeaningVerdict));
        AssertMeaningProvenance(first);
        Assert.Empty(seg.Revisions);
        Assert.Equal((QaVerdict.Pass, "Nos hace falta una toma mas."), (seg.FinalVerdict, seg.FinalTranslation));
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
