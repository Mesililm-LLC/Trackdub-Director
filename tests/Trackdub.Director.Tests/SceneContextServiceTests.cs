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
        public Restore(string key, string? prev) { _key = key; _prev = prev; }
        public void Dispose() => Environment.SetEnvironmentVariable(_key, _prev);
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
            EnvShim("META_API_KEY", "test-fake-key"),
            EnvShim("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1"),
            EnvShim("META_SCENE_MODEL", "sam-3.1"));
    }

    private static (SceneContextService Service, IDisposable Http) BuildService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler));
        var metaClient = new MetaClient(httpClient);
        return (new SceneContextService(metaClient), new Composite(httpClient, metaClient));
    }

    [Fact]
    public async Task Context_parsed_records_region_provenance_and_usage()
    {
        using var auth = FakeAuth();
        var (service, http) = BuildService(async (request, cancellationToken) =>
        {
            Assert.Contains("/responses", request.RequestUri?.ToString() ?? string.Empty);
            Assert.DoesNotContain("/chat/completions", request.RequestUri?.ToString() ?? string.Empty);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Assert.Contains("sam-3.1", body, StringComparison.Ordinal);
            Assert.Contains("red rectangle", body, StringComparison.Ordinal);
            Assert.Contains("input_image", body, StringComparison.Ordinal);
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
            Assert.Equal(SceneContextStatus.Parsed, result.Status);
            Assert.Null(result.Error);
            Assert.NotNull(result.Context);

            DirectorContext context = result.Context!;
            // Caller-known speaker is carried through; SAM-evidenced fields besides regions stay null.
            Assert.Equal("spk-1", context.SpeakerId);
            Assert.Equal("Director", context.SpeakerLabel);
            Assert.Null(context.EmotionToneLabel);
            Assert.Null(context.OnScreenText);
            Assert.Null(context.SceneSummary);

            SceneRegion region = Assert.Single(context.Regions!);
            Assert.Equal("red rectangle", region.Concept);
            Assert.Equal(10, region.X1);
            Assert.Equal(10, region.Y1);
            Assert.Equal(29, region.X2);
            Assert.Equal(49, region.Y2);
            Assert.Equal(64, region.FrameWidth);
            Assert.Equal(64, region.FrameHeight);
            Assert.Equal(0, region.MaskX);
            Assert.Equal(0, region.MaskY);
            Assert.Equal("40,20,abcXYZ", region.MaskData);

            Assert.Equal("sam-3.1", result.Provenance.ModelId);
            Assert.Equal("api.meta.ai", result.Provenance.EndpointHost);
            Assert.Equal(SceneContextService.PromptTemplateVersion, result.Provenance.PromptTemplateVersion);
            Assert.False(string.IsNullOrWhiteSpace(result.Provenance.TimestampUtc));
            Assert.Equal(0, result.Usage.TotalTokens);
            Assert.Equal(1, result.ImagesProcessed);
            Assert.Equal(1, batch.ImagesProcessedTotal);
            Assert.Equal(0, batch.UsageTotal.TotalTokens);
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
            Assert.Equal(SceneContextStatus.Empty, result.Status);
            Assert.NotNull(result.Context);
            Assert.Null(result.Context!.SpeakerId);
            Assert.Null(result.Context.SpeakerLabel);
            Assert.Null(result.Context.EmotionToneLabel);
            Assert.Null(result.Context.OnScreenText);
            Assert.Null(result.Context.SceneSummary);
            // Queried and missed: regions is an honest empty array, not null.
            Assert.NotNull(result.Context.Regions);
            Assert.Empty(result.Context.Regions!);
            Assert.Equal(1, result.ImagesProcessed);
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
            Assert.Equal(SceneContextStatus.Empty, result.Status);
            Assert.NotNull(result.Context);
            // Never queried: regions unknown (null), distinct from queried-and-missed ([]).
            Assert.Null(result.Context!.Regions);
            Assert.Equal(0, result.ImagesProcessed);
            Assert.Equal(0, result.Usage.TotalTokens);
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
            Assert.Equal(SceneContextStatus.Failed, result.Status);
            // Failed must never be smoothed into a defaulted all-null context.
            Assert.Null(result.Context);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Equal(outputText, result.RawContent);
            Assert.Equal("sam-3.1", result.Provenance.ModelId);
            Assert.Equal(SceneContextService.PromptTemplateVersion, result.Provenance.PromptTemplateVersion);
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
