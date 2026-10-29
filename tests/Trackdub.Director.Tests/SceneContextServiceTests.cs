using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Deterministic fake-handler tests for the scene-context lane. The canned
/// output_text below is the exact token grammar the live sam-3.1 probe returned
/// for the concept "red rectangle" on a 64x64 synthetic frame.
/// </summary>
public sealed class SceneContextServiceTests
{
    private const string ProbeOutputText = "<0f>0<|box;x1=10;y1=10;x2=29;y2=49;w=64;h=64|><|mask;x=0;y=0;data=40,20,abcXYZ|>\n";
    private const string FrameDataUrl = "data:image/png;base64,iVBORw0KGgo=";

    private static HttpResponseMessage JsonResponse(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage ResponsesOk(string outputText, int inputTokens = 0, int outputTokens = 0, int imagesProcessed = 1)
    {
        var payload = new
        {
            id = "resp_test",
            @object = "response",
            status = "completed",
            model = "sam-3.1",
            error = (object?)null,
            output = new[]
            {
                new
                {
                    type = "message",
                    role = "assistant",
                    status = "completed",
                    content = new[] { new { type = "output_text", text = outputText, annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() } },
                },
            },
            usage = new
            {
                input_tokens = inputTokens,
                input_tokens_details = new { cached_tokens = 0 },
                output_tokens = outputTokens,
                output_tokens_details = new { reasoning_tokens = 0 },
                total_tokens = inputTokens + outputTokens,
                images_processed = imagesProcessed,
            },
        };
        return JsonResponse(payload);
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
            EnvShim(new EnvSetting("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1")),
            EnvShim(new EnvSetting("META_SCENE_MODEL", "sam-3.1")));
    }

    private static (SceneContextService Service, IDisposable Http) BuildService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler));
        var metaClient = new MetaClient(httpClient);
        return (new SceneContextService(metaClient), new Composite(httpClient, metaClient));
    }

    private static void AssertRequest(HttpRequestMessage request)
    {
        string uri = request.RequestUri?.ToString() ?? string.Empty;
        Assert.Contains("/responses", uri);
        Assert.DoesNotContain("/chat/completions", uri);
    }

    private static void AssertRequestBody(string body)
    {
        Assert.Contains("sam-3.1", body, StringComparison.Ordinal);
        Assert.Contains("red rectangle", body, StringComparison.Ordinal);
        Assert.Contains("input_image", body, StringComparison.Ordinal);
    }

    private static void AssertParsed(SceneSegmentResult result)
    {
        Assert.Equal(SceneContextStatus.Parsed, result.Status);
        Assert.Null(result.Error);
        Assert.NotNull(result.Context);
    }

    private static void AssertCallerContext(DirectorContext context)
    {
        Assert.Equal("spk-1", context.SpeakerId);
        Assert.Equal("Director", context.SpeakerLabel);
    }

    private static void AssertUnevidencedFieldsNull(DirectorContext context)
    {
        Assert.Null(context.EmotionToneLabel);
        Assert.Null(context.OnScreenText);
        Assert.Null(context.SceneSummary);
    }

    private static void AssertRegion(SceneRegion region)
    {
        SceneRegion expected = new("red rectangle", 10, 10, 29, 49, 64, 64, 0, 0, "40,20,abcXYZ");
        Assert.Equal(expected, region);
    }

    private static void AssertProvenanceIdentity(SceneSegmentResult result)
    {
        Assert.Equal("sam-3.1", result.Provenance.ModelId);
        Assert.Equal("api.meta.ai", result.Provenance.EndpointHost);
        Assert.Equal(SceneContextService.PromptTemplateVersion, result.Provenance.PromptTemplateVersion);
    }

    private static void AssertProvenanceTimestamp(SceneSegmentResult result)
    {
        Assert.False(string.IsNullOrWhiteSpace(result.Provenance.TimestampUtc));
    }

    private static void AssertResultUsage(SceneSegmentResult result)
    {
        Assert.Equal(0, result.Usage.TotalTokens);
        Assert.Equal(1, result.ImagesProcessed);
    }

    private static void AssertBatchUsage(SceneContextBatch batch)
    {
        Assert.Equal(1, batch.ImagesProcessedTotal);
        Assert.Equal(0, batch.UsageTotal.TotalTokens);
    }

    private static void AssertSpeakerUnknown(DirectorContext context)
    {
        Assert.Null(context.SpeakerId);
        Assert.Null(context.SpeakerLabel);
    }

    private static void AssertDirectorFieldsUnknown(DirectorContext context)
    {
        Assert.Null(context.EmotionToneLabel);
        Assert.Null(context.OnScreenText);
        Assert.Null(context.SceneSummary);
    }

    private static void AssertEmptyResult(SceneSegmentResult result)
    {
        Assert.Equal(SceneContextStatus.Empty, result.Status);
        Assert.NotNull(result.Context);
        AssertSpeakerUnknown(result.Context!);
    }

    private static void AssertQueriedMiss(SceneSegmentResult result)
    {
        Assert.NotNull(result.Context!.Regions);
        Assert.Empty(result.Context.Regions!);
        Assert.Equal(1, result.ImagesProcessed);
    }

    private static void AssertNeverQueried(SceneSegmentResult result)
    {
        Assert.Equal(SceneContextStatus.Empty, result.Status);
        Assert.NotNull(result.Context);
        Assert.Null(result.Context!.Regions);
    }

    private static void AssertNoUsage(SceneSegmentResult result)
    {
        Assert.Equal(0, result.ImagesProcessed);
        Assert.Equal(0, result.Usage.TotalTokens);
    }

    private static void AssertFailed(SceneSegmentResult result)
    {
        Assert.Equal(SceneContextStatus.Failed, result.Status);
        Assert.Null(result.Context);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    private static void AssertFailedRaw(SceneSegmentResult result, string outputText)
    {
        Assert.Equal(outputText, result.RawContent);
        Assert.Equal("sam-3.1", result.Provenance.ModelId);
        Assert.Equal(SceneContextService.PromptTemplateVersion, result.Provenance.PromptTemplateVersion);
    }

    [Fact]
    public async Task Context_parsed_records_region_provenance_and_usage()
    {
        using var auth = FakeAuth();
        var (service, http) = BuildService(async (request, cancellationToken) =>
        {
            AssertRequest(request);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            AssertRequestBody(body);
            Assert.Contains("data:image/png;base64,", body, StringComparison.Ordinal);
            return ResponsesOk(ProbeOutputText, inputTokens: 0, outputTokens: 0, imagesProcessed: 1);
        });
        using (http)
        {
            var input = new SceneSegmentInput(
                "seg-001", "We need one more take.", FrameDataUrl, ["red rectangle"],
                SpeakerId: "spk-1", SpeakerLabel: "Director");

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertParsed(result);
            AssertCallerContext(result.Context!);
            AssertUnevidencedFieldsNull(result.Context!);
            AssertRegion(Assert.Single(result.Context!.Regions!));
            AssertProvenanceIdentity(result);
            AssertProvenanceTimestamp(result);
            AssertResultUsage(result);
            AssertBatchUsage(batch);
        }
    }

    [Fact]
    public async Task Context_empty_when_model_returns_no_region()
    {
        using var auth = FakeAuth();
        var (service, http) = BuildService((_, _) => Task.FromResult(ResponsesOk(string.Empty)));
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, ["green circle"]);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertEmptyResult(result);
            AssertDirectorFieldsUnknown(result.Context!);
            // Queried and missed: regions is an honest empty array, not null.
            AssertQueriedMiss(result);
        }
    }

    [Fact]
    public async Task Context_empty_without_frame_makes_no_live_call()
    {
        using var auth = FakeAuth();
        int calls = 0;
        var (service, http) = BuildService((_, _) =>
        {
            calls++;
            return Task.FromResult(ResponsesOk(ProbeOutputText));
        });
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameImageDataUrl: null, Concepts: ["person"]);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            Assert.Equal(0, calls);
            // Never queried: regions unknown (null), distinct from queried-and-missed ([]).
            AssertNeverQueried(result);
            AssertNoUsage(result);
        }
    }

    [Theory]
    [InlineData("Here is the segmentation you asked for!")]
    [InlineData("<|box;x1=10;y1=10|>")]
    [InlineData("<|box;x1=a;y1=10;x2=29;y2=49;w=64;h=64|>")]
    [InlineData("<|box;x1=30;y1=10;x2=29;y2=49;w=64;h=64|>")]
    [InlineData("<|box;x1=10;y1=10;x2=29;y2=49;w=64;h=64|><|mask;x=0;y=0|>")]
    public async Task Malformed_response_is_failed_not_defaulted(string outputText)
    {
        using var auth = FakeAuth();
        var (service, http) = BuildService((_, _) => Task.FromResult(ResponsesOk(outputText)));
        using (http)
        {
            var input = new SceneSegmentInput(
                "seg-001", "Action!", FrameDataUrl, ["red rectangle"], SpeakerId: "spk-1");

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertFailed(result);
            AssertFailedRaw(result, outputText);
        }
    }

    [Fact]
    public void Parser_empty_text_is_a_miss_not_malformed()
    {
        SegmentationParseResult parsed = SceneSegmentationParser.Parse("   ", "person");
        Assert.False(parsed.IsMalformed);
        Assert.Empty(parsed.Regions);
    }
}
