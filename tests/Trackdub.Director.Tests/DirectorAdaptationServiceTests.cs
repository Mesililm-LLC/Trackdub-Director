using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Deterministic fake-handler tests for Director Mode Stage B2 register
/// adaptation. The stub dispatches on the template markers: adaptation calls
/// carry DIRECTOR_REGISTER_ADAPTATION, meaning-gate calls carry
/// MEANING_FIDELITY_JUDGE. Call counts are asserted in every test so a code
/// path that silently spends (or skips) a call fails here.
/// </summary>
public sealed class DirectorAdaptationServiceTests
{
    private const string SourceText = "We need one more take.";
    private const string ExistingTranslation = "Necesitamos una toma más.";
    private const string AdaptedInformal = "Claro, una toma más.";
    private const string AdaptedLong = "Necesitaríamos, por favor, realizar una toma adicional completa.";

    private sealed class LaneStub
    {
        public int AdaptationCalls;
        public int MeaningCalls;
        public List<string> AdaptationBodies = [];
        public string AdaptationContent = AdaptedInformal;
        public string AdaptationFinishReason = "stop";
        public string MeaningContent = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";
        public bool MeaningTransportsError;

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (body.Contains(DirectorAdaptationService.DirectorMarker, StringComparison.Ordinal))
            {
                AdaptationCalls++;
                AdaptationBodies.Add(body);
                return ChatOk(AdaptationContent, promptTokens: 120, completionTokens: 40, reasoningTokens: 12, finishReason: AdaptationFinishReason);
            }

            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                MeaningCalls++;
                if (MeaningTransportsError)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("""{"error":{"message":"upstream down"}}""", Encoding.UTF8, "application/json"),
                    };
                }

                return ChatOk(MeaningContent, promptTokens: 90, completionTokens: 30, reasoningTokens: null);
            }

            throw new InvalidOperationException($"Unexpected request body: {body}");
        }
    }

    private static HttpResponseMessage ChatOk(string content, int promptTokens, int completionTokens, int? reasoningTokens, string finishReason = "stop")
    {
        var payload = new
        {
            id = "chatcmpl-test-director",
            @object = "chat.completion",
            created = 1793261234L,
            model = "muse-spark-1.1",
            choices = new[] { new { index = 0, finish_reason = finishReason, message = new { role = "assistant", content, refusal = (string?)null } } },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
                completion_tokens_details = reasoningTokens.HasValue ? new { reasoning_tokens = reasoningTokens.Value } : null,
                prompt_tokens_details = new { cached_tokens = 0 },
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly LaneStub _stub;
        public DelegatingHandlerStub(LaneStub stub) => _stub = stub;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _stub.HandleAsync(request, cancellationToken);
    }

    private sealed record EnvSetting(string Key, string Value);

    private static IDisposable EnvShim(EnvSetting setting)
    {
        string? previous = Environment.GetEnvironmentVariable(setting.Key);
        Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        return new Restore(setting, previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly EnvSetting _setting;
        private readonly string? _previous;
        public Restore(EnvSetting setting, string? previous) { _setting = setting; _previous = previous; }
        public void Dispose() => Environment.SetEnvironmentVariable(_setting.Key, _previous);
    }

    private sealed class Composite : IDisposable
    {
        private readonly IDisposable[] _items;
        public Composite(params IDisposable[] items) => _items = items;
        public void Dispose() { foreach (IDisposable d in _items) d.Dispose(); }
    }

    private static IDisposable FakeAuth()
    {
        return new Composite(
            EnvShim(new EnvSetting("META_API_KEY", "test-fake-key")),
            EnvShim(new EnvSetting("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1")));
    }

    private static (DirectorAdaptationService Service, IDisposable Scope) BuildService(LaneStub stub)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(stub));
        var metaClient = new MetaClient(httpClient);
        return (new DirectorAdaptationService(metaClient), new Composite(FakeAuth(), httpClient, metaClient));
    }

    private static DirectorAdaptationSegmentInput OptedIn(
        string register = "informal",
        double durationSeconds = 4.0,
        DirectorContext? context = null)
    {
        return new DirectorAdaptationSegmentInput(
            "seg-002", SourceText, ExistingTranslation, durationSeconds, context,
            new DirectorAdaptationSpec(register));
    }

    [Fact]
    public async Task AdaptationProduced_AndBothGatesPass()
    {
        var stub = new LaneStub();
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Passed, result.Status);
        Assert.Equal(ExistingTranslation, result.OriginalTranslation);
        Assert.Equal(AdaptedInformal, result.AdaptedTranslation);
        Assert.Equal("informal", result.TargetRegister);
        Assert.Null(result.Error);

        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);
        Assert.NotNull(result.EstimatedSeconds);
        Assert.Equal(4.0, result.WindowSeconds);
        Assert.NotNull(result.Meaning);
        Assert.Equal(MeaningVerdict.Pass, result.Meaning.Verdict);
        Assert.Empty(result.Meaning.Issues);
        Assert.Equal(120, result.Meaning.Usage.TotalTokens);

        Assert.NotNull(result.Provenance);
        Assert.Equal("muse-spark-1.1", result.Provenance.ModelId);
        Assert.Equal("api.meta.ai", result.Provenance.EndpointHost);
        Assert.Equal("director-v1", result.Provenance.PromptTemplateVersion);
        Assert.NotNull(result.Usage);
        Assert.Equal(160, result.Usage.TotalTokens);
        Assert.Equal(12, result.Usage.ReasoningTokens);

        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(1, stub.MeaningCalls);
        string body = Assert.Single(stub.AdaptationBodies);
        Assert.Contains("muse-spark-1.1", body, StringComparison.Ordinal);
        Assert.Contains("\"reasoning_effort\":\"low\"", body, StringComparison.Ordinal);
        Assert.Contains(SourceText, body, StringComparison.Ordinal);
        // Body is JSON-escaped (á becomes á), so match the ASCII prefix.
        Assert.Contains("Necesitamos una toma", body, StringComparison.Ordinal);
        Assert.Contains("informal", body, StringComparison.Ordinal);

        Assert.Equal(160, batch.UsageTotal.TotalTokens);
        Assert.Equal(120, batch.MeaningUsageTotal.TotalTokens);
    }

    [Fact]
    public async Task AdaptedLineFailsMeaning_ReportedFailedWithIssuesPreserved()
    {
        var stub = new LaneStub
        {
            MeaningContent = """{"verdict":"fail","issues":["dropped modality: source says the take is needed"],"explanation":"Candidate drops the obligation."}""",
        };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Equal(AdaptedInformal, result.AdaptedTranslation);
        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);
        Assert.NotNull(result.Meaning);
        Assert.Equal(MeaningVerdict.Fail, result.Meaning.Verdict);
        Assert.Equal(["dropped modality: source says the take is needed"], result.Meaning.Issues);
        Assert.Equal("Candidate drops the obligation.", result.Meaning.Explanation);
        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(1, stub.MeaningCalls);
    }

    [Fact]
    public async Task AdaptedLineFailsTiming_MeaningNotEvaluatedWithZeroMeaningSpend()
    {
        var stub = new LaneStub { AdaptationContent = AdaptedLong };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn("formal", durationSeconds: 0.5)], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Equal(AdaptedLong, result.AdaptedTranslation);
        Assert.Equal(QaVerdict.Fail, result.TimingVerdict);
        Assert.NotNull(result.EstimatedSeconds);
        Assert.True(result.EstimatedSeconds.Value > 0.5);
        Assert.Equal(0.5, result.WindowSeconds);
        Assert.NotNull(result.Meaning);
        Assert.Equal(MeaningVerdict.NotEvaluated, result.Meaning.Verdict);
        Assert.Equal(0, result.Meaning.Usage.TotalTokens);
        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(0, stub.MeaningCalls);
        Assert.Equal(0, batch.MeaningUsageTotal.TotalTokens);
    }

    [Fact]
    public async Task ContextPresent_FieldsUsedReflectsOnlyNonNullFields_ContextAbsent_IsEmpty()
    {
        var stub = new LaneStub();
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        // Deserialized from the wire shape the validation input uses: context
        // inline with two fields set, one blank, one missing.
        const string json = """
            [
              {
                "id": "seg-002",
                "text": "We need one more take.",
                "translation": "Necesitamos una toma más.",
                "duration_seconds": 4.0,
                "context": {
                  "emotion_tone_label": "cheerful",
                  "scene_summary": "A woman stands outside a coffee shop entrance talking on her cell phone.",
                  "speaker_description": "",
                  "on_screen_text": []
                },
                "director_adaptation": { "target_register": "informal" }
              },
              {
                "id": "seg-003",
                "text": "Action!",
                "translation": "¡Acción!",
                "duration_seconds": 4.0,
                "director_adaptation": { "target_register": "formal" }
              }
            ]
            """;
        string tmp = Path.Combine(Path.GetTempPath(), $"director-b2-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tmp, json);
        try
        {
            IReadOnlyList<DirectorAdaptationSegmentInput> segments =
                await DirectorAdaptationService.LoadSegmentsAsync(tmp);
            DirectorAdaptationBatch batch = await service.RunAsync(segments, "en", "es");

            DirectorAdaptationSegmentResult withContext = batch.Segments[0];
            Assert.Equal(["emotion_tone_label", "scene_summary"], withContext.ContextFieldsUsed);
            Assert.Equal(DirectorAdaptationStatus.Passed, withContext.Status);
            Assert.Contains("cheerful", stub.AdaptationBodies[0], StringComparison.Ordinal);
            Assert.Contains("coffee shop", stub.AdaptationBodies[0], StringComparison.Ordinal);

            DirectorAdaptationSegmentResult withoutContext = batch.Segments[1];
            Assert.Empty(withoutContext.ContextFieldsUsed);
            Assert.Equal(DirectorAdaptationStatus.Passed, withoutContext.Status);
            Assert.DoesNotContain("SCENE CONTEXT", stub.AdaptationBodies[1], StringComparison.Ordinal);

            Assert.Equal(2, stub.AdaptationCalls);
            Assert.Equal(2, stub.MeaningCalls);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task InvalidRegister_FailsClosedWithZeroSpend_AndUntouchedSegmentMakesNoCalls()
    {
        var stub = new LaneStub();
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        var segments = new[]
        {
            OptedIn("royal"),
            new DirectorAdaptationSegmentInput("seg-009", "Quiet on set, please.", "Silencio en el set, por favor.", 1.0),
        };

        DirectorAdaptationBatch batch = await service.RunAsync(segments, "en", "es");

        DirectorAdaptationSegmentResult invalid = batch.Segments[0];
        Assert.Equal(DirectorAdaptationStatus.Failed, invalid.Status);
        Assert.Equal("royal", invalid.TargetRegister);
        Assert.Null(invalid.AdaptedTranslation);
        Assert.Null(invalid.Usage);
        Assert.Null(invalid.Provenance);
        Assert.NotNull(invalid.Error);
        Assert.Contains("target_register", invalid.Error, StringComparison.Ordinal);

        DirectorAdaptationSegmentResult untouched = batch.Segments[1];
        Assert.Equal(DirectorAdaptationStatus.NotRequested, untouched.Status);
        Assert.Null(untouched.AdaptedTranslation);
        Assert.Null(untouched.Usage);

        Assert.Equal(0, stub.AdaptationCalls);
        Assert.Equal(0, stub.MeaningCalls);
        Assert.Equal(0, batch.UsageTotal.TotalTokens);
        Assert.Equal(0, batch.MeaningUsageTotal.TotalTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Primera línea.\nSegunda línea.")]
    public async Task MalformedAdaptationResponse_FailsClosed(string malformedContent)
    {
        var stub = new LaneStub { AdaptationContent = malformedContent };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Null(result.AdaptedTranslation);
        Assert.Null(result.TimingVerdict);
        Assert.Null(result.Meaning);
        Assert.NotNull(result.Error);
        Assert.Contains("Malformed", result.Error, StringComparison.Ordinal);

        // The call was made and billed; usage is recorded, not discarded.
        Assert.NotNull(result.Usage);
        Assert.Equal(160, result.Usage.TotalTokens);
        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(0, stub.MeaningCalls);
    }

    [Fact]
    public async Task ChangedTrue_WhenAdaptationRewritesTheLine_Seg008FormalShape()
    {
        // Live shape: seg-008 formal introduced the usted form "Le" over the
        // register-neutral baseline.
        var stub = new LaneStub { AdaptationContent = "Le veremos en la próxima etapa." };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        var segment = new DirectorAdaptationSegmentInput(
            "seg-008", "We will see you on the next stage.", "Nos vemos en la próxima etapa.",
            DurationSeconds: 2.0, Context: null, new DirectorAdaptationSpec("formal"));

        DirectorAdaptationBatch batch = await service.RunAsync([segment], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Passed, result.Status);
        Assert.Equal("Le veremos en la próxima etapa.", result.AdaptedTranslation);
        Assert.True(result.Changed);
        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(1, stub.MeaningCalls);
    }

    [Fact]
    public async Task ChangedFalse_WhenModelReturnsLineUnchanged_Seg008InformalShape()
    {
        // Live shape: seg-008 informal and the seg-002 context run both returned
        // their baselines byte-identical. Unchanged is a recorded outcome; the
        // gates still run on the returned line exactly as usual.
        var stub = new LaneStub { AdaptationContent = "Nos vemos en la próxima etapa." };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        var segment = new DirectorAdaptationSegmentInput(
            "seg-008", "We will see you on the next stage.", "Nos vemos en la próxima etapa.",
            DurationSeconds: 2.0, Context: null, new DirectorAdaptationSpec("informal"));

        DirectorAdaptationBatch batch = await service.RunAsync([segment], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Passed, result.Status);
        Assert.Equal("Nos vemos en la próxima etapa.", result.AdaptedTranslation);
        Assert.False(result.Changed);
        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);
        Assert.NotNull(result.Meaning);
        Assert.Equal(MeaningVerdict.Pass, result.Meaning.Verdict);
        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(1, stub.MeaningCalls);
    }

    [Fact]
    public async Task TruncatedCompletion_FailsClosedWithUsageRecorded()
    {
        // A finish_reason of "length" is not an adapted line: even a short
        // prefix that would pass both gates must not report Passed.
        var stub = new LaneStub { AdaptationContent = "Una toma.", AdaptationFinishReason = "length" };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn(durationSeconds: 4.0)], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Null(result.AdaptedTranslation);
        Assert.Null(result.TimingVerdict);
        Assert.Null(result.Meaning);
        Assert.NotNull(result.Error);
        Assert.Contains("length", result.Error, StringComparison.Ordinal);
        Assert.NotNull(result.Usage);
        Assert.Equal(160, result.Usage.TotalTokens);
        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(0, stub.MeaningCalls);
    }

    [Fact]
    public async Task NullSegmentEntry_FailsTheLoaderWithADiagnostic()
    {
        const string json = """[null]""";
        string tmp = Path.Combine(Path.GetTempPath(), $"director-b2-null-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tmp, json);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DirectorAdaptationService.LoadSegmentsAsync(tmp));
            Assert.Contains("index 0", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task MeaningTransportError_IsTerminalFailWithErrorVerdict()
    {
        var stub = new LaneStub { MeaningTransportsError = true };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);
        Assert.NotNull(result.Meaning);
        Assert.Equal(MeaningVerdict.Error, result.Meaning.Verdict);
        Assert.Equal(0, result.Meaning.Usage.TotalTokens);
        Assert.Equal(1, stub.AdaptationCalls);
        Assert.Equal(1, stub.MeaningCalls);
    }
}
