using System.Net;
using System.Security.Cryptography;
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

    // Verbatim raw body of the live muse-spark-1.1 scene-vision-v1 probe on the
    // VOA 02:42 frame (D:\tmp\vision_probe_muse-spark-1.1.json). This is the
    // fixture the B1 parser and merge behavior are pinned to.
    private const string ProbeVisionResponseBody = """{"id":"chatcmpl-01a186b2-eade-74aa-a0f6-6bd0b8d71421","choices":[{"finish_reason":"stop","index":0,"message":{"content":"{\"scene_summary\": \"A woman stands outside a coffee shop entrance talking on her cell phone while smiling.\", \"emotion_tone_label\": \"cheerful\", \"speaker_description\": \"a smiling woman in a knit beanie and black winter coat talking on a phone\", \"on_screen_text\": [\"COFFEE SHOP\", \"STORE HOURS\", \"MONDAY - FRIDAY\", \"6:30AM - 8:00PM\", \"SATURDAY & SUNDAY\", \"8:00AM - 8:00PM\"], \"burned_in_text\": [\"VOA Learning English\", \"Okay. My apartment is across from a big department store.\"]}","refusal":null,"role":"assistant"},"logprobs":null}],"created":1793261234,"model":"muse-spark-1.1","object":"chat.completion","usage":{"completion_tokens":756,"prompt_tokens":2878,"total_tokens":3634,"completion_tokens_details":{"reasoning_tokens":611},"prompt_tokens_details":{"cached_tokens":0}}}""";

    // Verbatim raw body of the earlier muse-wonder-1.0 probe (D:\tmp\wonder_probe_response.json),
    // whose ask had no burned_in_text split. B1 must fail this shape closed.
    private const string WonderResponseBody = """{"id":"chatcmpl-01a1864a-4e32-7209-b337-890bba47f6b1","choices":[{"finish_reason":"stop","index":0,"message":{"content":"{\"scene_summary\": \"A woman in winter clothing stands outside a coffee shop talking on a cell phone.\", \"emotion_tone_label\": \"cheerful\", \"on_screen_text\": [\"COFFEE SHOP\", \"STORE HOURS\", \"MONDAY - FRIDAY\", \"6:30AM - 8:00PM\", \"SATURDAY & SUNDAY\", \"8:00AM - 8:00PM\", \"VOA Learning English\", \"Okay. My apartment is across from a big department store.\"], \"speaker_description\": \"woman in black winter coat, knit hat, and patterned scarf\"}","refusal":null,"role":"assistant"},"logprobs":null}],"created":1793254378,"model":"muse-wonder-1.0","object":"chat.completion","usage":{"completion_tokens":977,"prompt_tokens":2763,"total_tokens":3740,"completion_tokens_details":{"reasoning_tokens":841},"prompt_tokens_details":{"cached_tokens":0}}}""";

    // Independently restated scene-vision-v1 ask: the service constant must equal
    // this text character for character (the live prompt-token count is the
    // second oracle for the same property).
    private const string ExpectedVisionPrompt = "Look at this video frame. Return strict JSON only, with no markdown fences and no commentary, using exactly these keys: scene_summary (one sentence describing the scene), emotion_tone_label (short label for the visible speaker's emotion or tone, or null if no speaker is visible), speaker_description (short phrase describing the visible speaker, or null if no speaker is visible), on_screen_text (array of exact text strings physically present in the scene only, such as signs and posters; do not include burned-in subtitles or captions, and do not include watermarks or logos), burned_in_text (array of exact text strings overlaid on the video only, namely burned-in subtitles or captions and watermarks or logos; do not include text physically present in the scene). Use an empty array when no text of that kind is visible. Transcribe text exactly as shown. Do not guess.";

    private static HttpResponseMessage RawJson(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage ChatVisionOk(string content)
    {
        var payload = new
        {
            id = "chatcmpl-test-vision",
            @object = "chat.completion",
            created = 1793261234L,
            model = "muse-spark-1.1",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content, refusal = (string?)null } } },
            usage = new
            {
                prompt_tokens = 2878,
                completion_tokens = 756,
                total_tokens = 3634,
                completion_tokens_details = new { reasoning_tokens = 611 },
                prompt_tokens_details = new { cached_tokens = 0 },
            },
        };
        return JsonResponse(payload);
    }

    private static string FrameSha256Hex()
    {
        byte[] bytes = Convert.FromBase64String("iVBORw0KGgo=");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

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
            EnvShim(new EnvSetting("META_SCENE_MODEL", "sam-3.1")),
            EnvShim(new EnvSetting("META_VISION_MODEL", "muse-spark-1.1")));
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

    private static void AssertVisionTemplateVerbatim()
    {
        Assert.Equal(ExpectedVisionPrompt, SceneContextService.VisionPromptText);
        Assert.Equal("scene-vision-v1", SceneContextService.VisionPromptTemplateVersion);
    }

    private static void AssertVisionRequest(HttpRequestMessage request)
    {
        string uri = request.RequestUri?.ToString() ?? string.Empty;
        Assert.Contains("/chat/completions", uri);
        Assert.DoesNotContain("/responses", uri);
    }

    private static void AssertVisionRequestBody(string body)
    {
        // Compare decoded JSON values, not raw substrings: the encoder escapes
        // apostrophes (speaker\u0027s) without changing the string the API reads.
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        Assert.Equal("muse-spark-1.1", root.GetProperty("model").GetString());
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        JsonElement content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal(ExpectedVisionPrompt, content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(FrameDataUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Contains("\"model\":\"muse-spark-1.1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"reasoning_effort\":\"low\"", body, StringComparison.Ordinal);
        
        Assert.Contains("\"image_url\"", body, StringComparison.Ordinal);
        Assert.Contains(FrameDataUrl, body, StringComparison.Ordinal);
    }

    private static void AssertEnrichmentScalars(DirectorContext context)
    {
        Assert.Equal("A woman stands outside a coffee shop entrance talking on her cell phone while smiling.", context.SceneSummary);
        Assert.Equal("cheerful", context.EmotionToneLabel);
        Assert.Equal("a smiling woman in a knit beanie and black winter coat talking on a phone", context.SpeakerDescription);
    }

    private static void AssertTextSplit(DirectorContext context)
    {
        // "COFFEE SHOP" is in-scene; the subtitle line and the VOA watermark are burned-in.
        Assert.NotNull(context.OnScreenText);
        Assert.Contains("COFFEE SHOP", context.OnScreenText!);
        Assert.DoesNotContain("Okay. My apartment is across from a big department store.", context.OnScreenText!);
        Assert.DoesNotContain("VOA Learning English", context.OnScreenText!);
        Assert.NotNull(context.BurnedInText);
        Assert.Equal(["VOA Learning English", "Okay. My apartment is across from a big department store."], context.BurnedInText!);
    }

    private static void AssertVisionProvenance(SceneSegmentResult result, string returnedModel = "muse-spark-1.1")
    {
        Assert.NotNull(result.VisionProvenance);
        Assert.Equal("muse-spark-1.1", result.VisionProvenance!.RequestedModelId);
        Assert.Equal(returnedModel, result.VisionProvenance.ModelId);
        Assert.Equal("api.meta.ai", result.VisionProvenance.EndpointHost);
        Assert.Equal(SceneContextService.VisionPromptTemplateVersion, result.VisionProvenance.PromptTemplateVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.VisionProvenance.TimestampUtc));
    }

    private static void AssertVisionUsage(SceneSegmentResult result)
    {
        Assert.NotNull(result.VisionUsage);
        Assert.Equal(2878, result.VisionUsage!.PromptTokens);
        Assert.Equal(756, result.VisionUsage.CompletionTokens);
        Assert.Equal(3634, result.VisionUsage.TotalTokens);
        Assert.Equal(611, result.VisionUsage.ReasoningTokens);
        Assert.Equal(0, result.VisionUsage.CachedTokens);
    }

    private static void AssertVisionBatchTotal(SceneContextBatch batch)
    {
        Assert.NotNull(batch.VisionUsageTotal);
        Assert.Equal(3634, batch.VisionUsageTotal!.TotalTokens);
        Assert.Equal(611, batch.VisionUsageTotal.ReasoningTokens);
    }

    private static void AssertFrameProvenance(SceneSegmentResult result)
    {
        Assert.Equal("hero-0242", result.FrameId);
        Assert.Equal(162.0, result.TimecodeSeconds);
        Assert.Equal(FrameSha256Hex(), result.FrameSha256);
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
    public async Task Vision_enrichment_parsed_from_probe_fixture_with_text_split()
    {
        using var auth = FakeAuth();
        AssertVisionTemplateVerbatim();
        string? visionBody = null;
        var (service, http) = BuildService(async (request, cancellationToken) =>
        {
            AssertVisionRequest(request);
            visionBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return RawJson(ProbeVisionResponseBody);
        });
        using (http)
        {
            var input = new SceneSegmentInput(
                "seg-001", "Okay. My apartment is across from a big department store.", FrameDataUrl, Concepts: null,
                SpeakerId: "ashley", SpeakerLabel: "Ashley",
                FrameId: "hero-0242", TimecodeSeconds: 162.0, FrameSha256: FrameSha256Hex(), VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            Assert.NotNull(visionBody);
            AssertVisionRequestBody(visionBody!);
            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertParsed(result);
            // Appearance never overwrites caller-known speaker identity.
            Assert.Equal("ashley", result.Context!.SpeakerId);
            Assert.Equal("Ashley", result.Context.SpeakerLabel);
            AssertEnrichmentScalars(result.Context);
            AssertTextSplit(result.Context);
            // No concepts were supplied, so regions stay unknown (null), not [].
            Assert.Null(result.Context.Regions);
            AssertVisionProvenance(result);
            AssertVisionUsage(result);
            AssertVisionBatchTotal(batch);
            AssertFrameProvenance(result);
            Assert.Equal(0, result.Usage.TotalTokens);
            Assert.Equal(0, result.ImagesProcessed);
            // SAM never ran on this segment, so its raw content is null, not empty.
            Assert.Null(result.RawContent);
        }
    }

    [Fact]
    public async Task Vision_enrichment_merges_with_sam_regions()
    {
        using var auth = FakeAuth();
        string? visionBody = null;
        var (service, http) = BuildService(async (request, cancellationToken) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                visionBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return RawJson(ProbeVisionResponseBody);
            }

            AssertRequest(request);
            return ResponsesOk(ProbeOutputText, inputTokens: 0, outputTokens: 0, imagesProcessed: 1);
        });
        using (http)
        {
            var input = new SceneSegmentInput(
                "seg-001", "We need one more take.", FrameDataUrl, ["red rectangle"],
                SpeakerId: "spk-1", SpeakerLabel: "Director", VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertParsed(result);
            AssertCallerContext(result.Context!);
            // Stage A evidence survives the merge untouched...
            AssertRegion(Assert.Single(result.Context!.Regions!));
            AssertProvenanceIdentity(result);
            AssertResultUsage(result);
            // ...and B1 evidence lands beside it, each lane in its own fields.
            AssertEnrichmentScalars(result.Context);
            AssertTextSplit(result.Context);
            AssertVisionProvenance(result);
            AssertVisionUsage(result);
            AssertVisionBatchTotal(batch);
            // SAM regions ride along as text context; the template itself is unaltered.
            Assert.NotNull(visionBody);
            using JsonDocument visionDoc = JsonDocument.Parse(visionBody!);
            JsonElement visionContent = visionDoc.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal(3, visionContent.GetArrayLength());
            Assert.Equal(ExpectedVisionPrompt, visionContent[0].GetProperty("text").GetString());
            Assert.Equal("Segmentation regions already found in this frame: red rectangle box (10,10)-(29,49) in 64x64.", visionContent[1].GetProperty("text").GetString());
            
        }
    }

    [Fact]
    public async Task Vision_no_in_scene_text_keeps_empty_array()
    {
        using var auth = FakeAuth();
        const string content = """{"scene_summary": "A blank wall with a watermark.", "emotion_tone_label": null, "speaker_description": null, "on_screen_text": [], "burned_in_text": ["VOA Learning English"]}""";
        var (service, http) = BuildService((_, _) => Task.FromResult(ChatVisionOk(content)));
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, Concepts: null, VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertParsed(result);
            Assert.Equal("A blank wall with a watermark.", result.Context!.SceneSummary);
            Assert.Null(result.Context.EmotionToneLabel);
            Assert.Null(result.Context.SpeakerDescription);
            // Looked and found none: an honest empty array, never collapsed to null.
            Assert.NotNull(result.Context.OnScreenText);
            Assert.Empty(result.Context.OnScreenText!);
            Assert.NotNull(result.Context.BurnedInText);
            Assert.Equal(["VOA Learning English"], result.Context.BurnedInText!);
        }
    }

    [Theory]
    [InlineData("Here is the scene you asked for!")]
    [InlineData("{\"scene_summary\": \"x\", \"emotion_tone_label\": null, \"speaker_description\": null, \"on_screen_text\": []}")]
    [InlineData("{\"scene_summary\": \"x\", \"emotion_tone_label\": null, \"speaker_description\": null, \"on_screen_text\": \"COFFEE SHOP\", \"burned_in_text\": []}")]
    public async Task Vision_malformed_response_fails_closed(string content)
    {
        using var auth = FakeAuth();
        var (service, http) = BuildService((_, _) => Task.FromResult(ChatVisionOk(content)));
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, Concepts: null, VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            // Fail closed: no defaulted context, but the spend and raw output stay visible.
            AssertFailed(result);
            Assert.Equal(content, result.VisionRawContent);
            AssertVisionProvenance(result);
            AssertVisionUsage(result);
        }
    }

    [Fact]
    public async Task Wonder_baseline_shape_without_text_split_fails_closed()
    {
        using var auth = FakeAuth();
        var (service, http) = BuildService((_, _) => Task.FromResult(RawJson(WonderResponseBody)));
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, Concepts: null, VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertFailed(result);
            Assert.Contains("burned_in_text", result.Error!, StringComparison.Ordinal);
            AssertVisionProvenance(result, returnedModel: "muse-wonder-1.0");
            Assert.NotNull(result.VisionUsage);
            Assert.Equal(3740, result.VisionUsage!.TotalTokens);
            Assert.Equal(841, result.VisionUsage.ReasoningTokens);
        }
    }

    [Fact]
    public async Task Frame_hash_mismatch_fails_without_live_call()
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
            var input = new SceneSegmentInput(
                "seg-001", "Action!", FrameDataUrl, ["red rectangle"],
                FrameId: "hero-0242", TimecodeSeconds: 162.0,
                FrameSha256: new string('0', 64), VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            Assert.Equal(0, calls);
            AssertFailed(result);
            Assert.Contains("SHA-256 mismatch", result.Error!, StringComparison.Ordinal);
            // The computed hash is still recorded so the caller can see what arrived.
            AssertFrameProvenance(result);
            Assert.Null(result.VisionProvenance);
            Assert.Null(result.VisionUsage);
        }
    }

    [Fact]
    public async Task Frame_hash_expected_for_remote_frame_fails_without_live_call()
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
            var input = new SceneSegmentInput(
                "seg-001", "Action!", "https://example.invalid/frame.jpg", ["red rectangle"],
                FrameSha256: FrameSha256Hex(), VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            Assert.Equal(0, calls);
            AssertFailed(result);
            Assert.Contains("could not be verified", result.Error!, StringComparison.Ordinal);
            Assert.Null(result.FrameSha256);
        }
    }

    [Fact]
    public void Vision_parser_tolerates_fence_wrapping()
    {
        VisionParseResult parsed = SceneVisionParser.Parse(
            "```json\n{\"scene_summary\": \"x\", \"emotion_tone_label\": null, \"speaker_description\": null, \"on_screen_text\": [], \"burned_in_text\": []}\n```");
        Assert.False(parsed.IsMalformed);
        Assert.Equal("x", parsed.Enrichment!.SceneSummary);
        Assert.NotNull(parsed.Enrichment.OnScreenText);
        Assert.Empty(parsed.Enrichment.OnScreenText!);
    }

    [Fact]
    public void Vision_parser_missing_key_is_malformed()
    {
        VisionParseResult parsed = SceneVisionParser.Parse("{\"scene_summary\": \"x\"}");
        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Enrichment);
    }

    [Fact]
    public async Task Vision_uses_model_captured_at_run_start()
    {
        using var auth = FakeAuth();
        string? visionBody = null;
        var (service, http) = BuildService(async (request, cancellationToken) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                visionBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Environment.SetEnvironmentVariable("META_VISION_MODEL", "muse-spark-1.1");
                return RawJson(ProbeVisionResponseBody);
            }

            // Simulate the env changing mid-run: request and provenance must not drift.
            Environment.SetEnvironmentVariable("META_VISION_MODEL", "muse-spark-1.3");
            return ResponsesOk(ProbeOutputText);
        });
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, ["red rectangle"], VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertParsed(result);
            Assert.NotNull(visionBody);
            using JsonDocument doc = JsonDocument.Parse(visionBody!);
            Assert.Equal("muse-spark-1.1", doc.RootElement.GetProperty("model").GetString());
            AssertVisionProvenance(result);
        }
    }

    [Fact]
    public async Task Vision_blank_content_retains_usage_and_fails_closed()
    {
        using var auth = FakeAuth();
        var (service, http) = BuildService((_, _) => Task.FromResult(ChatVisionOk(string.Empty)));
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, Concepts: null, VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertFailed(result);
            Assert.Equal(string.Empty, result.VisionRawContent);
            // The API billed the call even though its content was unusable; keep the spend visible.
            AssertVisionUsage(result);
            AssertVisionProvenance(result);
        }
    }

    [Fact]
    public async Task Vision_regions_context_is_capped()
    {
        using var auth = FakeAuth();
        string samOutput = string.Concat(Enumerable.Range(0, 21).Select(i => $"<|box;x1={i};y1=0;x2={i + 1};y2=1;w=100;h=100|>"));
        string? visionBody = null;
        var (service, http) = BuildService(async (request, cancellationToken) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                visionBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return RawJson(ProbeVisionResponseBody);
            }

            return ResponsesOk(samOutput);
        });
        using (http)
        {
            var input = new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, ["red rectangle"], VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertParsed(result);
            // Stored regions are uncapped; only the text context sent to vision is bounded.
            Assert.Equal(21, result.Context!.Regions!.Length);
            Assert.NotNull(visionBody);
            using JsonDocument doc = JsonDocument.Parse(visionBody!);
            string regionsText = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString()!;
            Assert.Contains("and 1 more (not shown)", regionsText, StringComparison.Ordinal);
            Assert.DoesNotContain("(20,0)-", regionsText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Frame_hash_ignores_base64_marker_in_remote_url()
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
            // The suffix decodes to bytes matching the expected hash; a remote URL
            // must still never be "verified" against its own suffix.
            var input = new SceneSegmentInput(
                "seg-001", "Action!", "https://example.invalid/frame;token=1;base64,iVBORw0KGgo=", ["red rectangle"],
                FrameSha256: FrameSha256Hex(), VisionEnrichment: true);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            Assert.Equal(0, calls);
            AssertFailed(result);
            Assert.Contains("could not be verified", result.Error!, StringComparison.Ordinal);
            Assert.Null(result.FrameSha256);
        }
    }

    [Fact]
    public void Vision_parser_duplicate_key_is_malformed()
    {
        VisionParseResult parsed = SceneVisionParser.Parse(
            "{\"scene_summary\": \"x\", \"scene_summary\": \"y\", \"emotion_tone_label\": null, \"speaker_description\": null, \"on_screen_text\": [], \"burned_in_text\": []}");
        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Enrichment);
        Assert.Contains("duplicate", parsed.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_empty_text_is_a_miss_not_malformed()
    {
        SegmentationParseResult parsed = SceneSegmentationParser.Parse("   ", "person");
        Assert.False(parsed.IsMalformed);
        Assert.Empty(parsed.Regions);
    }

    private static HttpResponseMessage UpstreamError() => new(HttpStatusCode.InternalServerError)
    {
        Content = new StringContent("""{"error":{"message":"upstream down"}}""", Encoding.UTF8, "application/json"),
    };

    [Theory]
    [InlineData("concept")]
    [InlineData("vision")]
    [InlineData("transport")]
    public async Task Upstream_failure_fails_the_segment_without_defaulting(string mode)
    {
        using var auth = FakeAuth();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler = mode == "transport"
            ? (_, _) => throw new HttpRequestException("connection reset")
            : (_, _) => Task.FromResult(UpstreamError());
        var (service, http) = BuildService(handler);
        using (http)
        {
            SceneSegmentInput input = mode == "vision"
                ? new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, VisionEnrichment: true)
                : new SceneSegmentInput("seg-001", "Action!", FrameDataUrl, ["red rectangle"]);

            SceneContextBatch batch = await service.RunAsync([input]);

            SceneSegmentResult result = Assert.Single(batch.Segments);
            AssertFailed(result);
            Assert.Contains(mode == "transport" ? "connection reset" : "HTTP 500", result.Error, StringComparison.Ordinal);
        }
    }
}
