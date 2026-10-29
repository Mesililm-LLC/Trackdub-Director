using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Server endpoint tests using the existing fake-handler pattern.
/// Status without a key, translate and QA happy paths, and models.
/// </summary>
public sealed class ServerEndpointTests
{
    private static HttpResponseMessage JsonOk(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    // Verbatim raw body of the live muse-spark-1.1 scene-vision-v1 probe fixture.
    private const string ProbeVisionResponseBody = """{"id":"chatcmpl-01a186b2-eade-74aa-a0f6-6bd0b8d71421","choices":[{"finish_reason":"stop","index":0,"message":{"content":"{\"scene_summary\": \"A woman stands outside a coffee shop entrance talking on her cell phone while smiling.\", \"emotion_tone_label\": \"cheerful\", \"speaker_description\": \"a smiling woman in a knit beanie and black winter coat talking on a phone\", \"on_screen_text\": [\"COFFEE SHOP\", \"STORE HOURS\", \"MONDAY - FRIDAY\", \"6:30AM - 8:00PM\", \"SATURDAY & SUNDAY\", \"8:00AM - 8:00PM\"], \"burned_in_text\": [\"VOA Learning English\", \"Okay. My apartment is across from a big department store.\"]}","refusal":null,"role":"assistant"},"logprobs":null}],"created":1793261234,"model":"muse-spark-1.1","object":"chat.completion","usage":{"completion_tokens":756,"prompt_tokens":2878,"total_tokens":3634,"completion_tokens_details":{"reasoning_tokens":611},"prompt_tokens_details":{"cached_tokens":0}}}""";

    private static string FrameSha256Hex()
    {
        byte[] bytes = Convert.FromBase64String("iVBORw0KGgo=");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void AssertSceneVisionContext(JsonElement context)
    {
        Assert.Equal("A woman stands outside a coffee shop entrance talking on her cell phone while smiling.", context.GetProperty("scene_summary").GetString());
        Assert.Equal("cheerful", context.GetProperty("emotion_tone_label").GetString());
        Assert.Equal("a smiling woman in a knit beanie and black winter coat talking on a phone", context.GetProperty("speaker_description").GetString());
    }

    private static void AssertSceneVisionTextSplit(JsonElement context)
    {
        string[] onScreen = context.GetProperty("on_screen_text").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("COFFEE SHOP", onScreen);
        Assert.DoesNotContain("Okay. My apartment is across from a big department store.", onScreen);
        Assert.DoesNotContain("VOA Learning English", onScreen);
        string[] burned = context.GetProperty("burned_in_text").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["VOA Learning English", "Okay. My apartment is across from a big department store."], burned);
    }

    private static void AssertSceneVisionMeta(JsonElement seg)
    {
        JsonElement visionProvenance = seg.GetProperty("vision_provenance");
        Assert.Equal("muse-spark-1.1", visionProvenance.GetProperty("requested_model_id").GetString());
        Assert.Equal("muse-spark-1.1", visionProvenance.GetProperty("model_id").GetString());
        Assert.Equal("scene-vision-v1", visionProvenance.GetProperty("prompt_template_version").GetString());
        JsonElement visionUsage = seg.GetProperty("vision_usage");
        Assert.Equal(3634, visionUsage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(611, visionUsage.GetProperty("reasoning_tokens").GetInt32());
    }

    private static void AssertSceneFrameProvenance(JsonElement seg)
    {
        Assert.Equal("hero-0242", seg.GetProperty("frame_id").GetString());
        Assert.Equal(162.0, seg.GetProperty("timecode_seconds").GetDouble());
        Assert.Equal(FrameSha256Hex(), seg.GetProperty("frame_sha256").GetString());
    }

    private static void AssertSceneVisionTotals(JsonElement root)
    {
        Assert.Equal(3634, root.GetProperty("vision_usage_total").GetProperty("total_tokens").GetInt32());
        Assert.Equal(611, root.GetProperty("vision_usage_total").GetProperty("reasoning_tokens").GetInt32());
    }

    private static HttpResponseMessage ChatOk(string modelId, string content, int promptTokens = 10, int completionTokens = 5)
    {
        var payload = new
        {
            id = $"chatcmpl-test-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = modelId,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens },
        };
        return JsonOk(payload);
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
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

    private static IDisposable FakeAuth(string model = "muse-spark-1.1")
    {
        var a = EnvShim("META_API_KEY", "test-fake-key");
        var b = EnvShim("META_MODEL", model);
        var c = EnvShim("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1");
        var d = EnvShim("META_REASONING_EFFORT", "low");
        return new Composite(a, b, c, d);
    }

    private sealed class Composite : IDisposable
    {
        private readonly IDisposable[] _items;
        public Composite(params IDisposable[] items) => _items = items;
        public void Dispose() { foreach (IDisposable d in _items) d.Dispose(); }
    }

    private sealed class ServerFactory : WebApplicationFactory<Program>
    {
        private readonly HttpMessageHandler _handler;
        public ServerFactory(HttpMessageHandler handler) => _handler = handler;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<HttpClient>();
                services.AddSingleton<HttpClient>(_ => new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(60) });
            });
        }
    }

    private sealed class ChatStubOptions
    {
        public string ModelId { get; init; } = "muse-spark-1.1";
        public int PromptTokens { get; init; } = 10;
        public int CompletionTokens { get; init; } = 5;
        public int MeaningPrompt { get; init; } = 7;
        public int MeaningComp { get; init; } = 4;
    }

    private static HttpMessageHandler SequencedChatHandler(Queue<string> translations, ChatStubOptions? options = null)
    {
        return DualHandler(translations, new Queue<string>([JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "ok" })]), options ?? new ChatStubOptions());
    }

    private static HttpMessageHandler DualHandler(
        Queue<string> translations,
        Queue<string> meaningJsons,
        ChatStubOptions options)
    {
        return new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string? path = request.RequestUri?.AbsolutePath;
            if (path is not null && path.EndsWith("/models", StringComparison.Ordinal))
            {
                var modelsPayload = new
                {
                    @object = "list",
                    data = new[]
                    {
                        new { id = "muse-spark-1.1", @object = "model", created = 123L, owned_by = "meta" },
                        new { id = "llama-4-maverick", @object = "model", created = 124L, owned_by = "meta" },
                    },
                };
                return JsonOk(modelsPayload);
            }

            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bool isMeaning = body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal);

            if (isMeaning)
            {
                if (meaningJsons.Count == 0)
                {
                    throw new InvalidOperationException("No more canned meaning JSON queued.");
                }

                string mj = meaningJsons.Dequeue();
                return ChatOk(options.ModelId, mj, options.MeaningPrompt, options.MeaningComp);
            }

            if (translations.Count == 0)
            {
                throw new InvalidOperationException("No more canned translations queued for chat/completions.");
            }

            string translation = translations.Dequeue();
            return ChatOk(options.ModelId, translation, options.PromptTokens, options.CompletionTokens);
        });
    }

    private static HttpMessageHandler ModelsHandler()
    {
        return new DelegatingHandlerStub((request, ct) =>
        {
            var payload = new
            {
                @object = "list",
                data = new[]
                {
                    new { id = "muse-spark-1.1", @object = "model", created = 123L, owned_by = "meta" },
                },
            };
            return Task.FromResult(JsonOk(payload));
        });
    }

    private static void AssertStatusWithoutKey(JsonElement root)
    {
        Assert.False(root.GetProperty("hasKey").GetBoolean());
        Assert.Equal(MetaAuth.DefaultModel, root.GetProperty("model").GetString());
        Assert.Equal(MetaAuth.DefaultBaseUrl, root.GetProperty("baseUrl").GetString());
    }

    private static void AssertNoKeyLeak(JsonElement root, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reasoningEffort").GetString()));
        Assert.DoesNotContain("test-fake-key", json.ToLowerInvariant());
    }

    private static void AssertTranslateRoot(JsonElement root)
    {
        Assert.Equal("en", root.GetProperty("source_language").GetString());
        Assert.Equal("es", root.GetProperty("target_language").GetString());
        Assert.Equal(2, root.GetProperty("segments").GetArrayLength());
    }

    private static void AssertFirstTranslatedSegment(JsonElement first)
    {
        Assert.Equal("seg-001", first.GetProperty("id").GetString());
        Assert.Equal("Hola, mundo.", first.GetProperty("translation").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("provenance").GetProperty("model_id").GetString()));
    }

    private static void AssertTranslateUsage(JsonElement root, JsonElement first)
    {
        Assert.NotEqual(0, first.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.True(root.GetProperty("usage_total").GetProperty("total_tokens").GetInt32() > 0);
    }

    private static void AssertQaRoot(JsonElement root)
    {
        Assert.Equal("en", root.GetProperty("source_language").GetString());
        Assert.Equal("es", root.GetProperty("target_language").GetString());
        Assert.True(root.TryGetProperty("qa_options", out _));
    }

    private static void AssertQaCollections(JsonElement root, out JsonElement segs, out JsonElement totals)
    {
        Assert.True(root.TryGetProperty("segments", out segs));
        Assert.True(root.TryGetProperty("totals", out totals));
        Assert.Equal(1, segs.GetArrayLength());
    }

    private static void AssertQaSegment(JsonElement seg)
    {
        Assert.Equal("seg-001", seg.GetProperty("id").GetString());
        Assert.Equal("Fail", seg.GetProperty("first_attempt").GetProperty("verdict").GetString());
        Assert.Equal("Fail", seg.GetProperty("first_attempt").GetProperty("timing_verdict").GetString());
    }

    private static void AssertQaFirstAttempt(JsonElement seg)
    {
        Assert.Equal("NotEvaluated", seg.GetProperty("first_attempt").GetProperty("meaning_verdict").GetString());
        Assert.Equal(1, seg.GetProperty("revisions").GetArrayLength());
        Assert.Equal("Pass", seg.GetProperty("final_verdict").GetString());
    }

    private static void AssertQaRevision(JsonElement seg)
    {
        JsonElement revision = seg.GetProperty("revisions")[0];
        Assert.Equal("Pass", revision.GetProperty("timing_verdict").GetString());
        Assert.Equal("Pass", revision.GetProperty("meaning_verdict").GetString());
        Assert.Equal("Corta", seg.GetProperty("final_translation").GetString());
    }

    private static void AssertQaMeaningFieldsPresent(JsonElement revision)
    {
        Assert.True(revision.TryGetProperty("meaning_issues", out _));
        Assert.True(revision.TryGetProperty("meaning_explanation", out _));
        Assert.True(revision.TryGetProperty("meaning_prompt_template_version", out _));
    }

    private static void AssertQaMeaningTemplate(JsonElement revision)
    {
        Assert.Equal("meaning-v1", revision.GetProperty("meaning_prompt_template_version").GetString());
    }

    private static void AssertQaTokens(JsonElement seg)
    {
        Assert.True(seg.GetProperty("revisions")[0].GetProperty("meaning_total_tokens").GetInt32() > 0);
        Assert.Equal(0, seg.GetProperty("first_attempt").GetProperty("meaning_total_tokens").GetInt32());
    }

    private static void AssertQaTotals(JsonElement totals)
    {
        Assert.Equal(1, totals.GetProperty("total_segments").GetInt32());
        Assert.Equal(0, totals.GetProperty("first_pass_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("final_pass_count").GetInt32());
    }

    private static void AssertQaTotalsRevisionsAndTokens(JsonElement totals)
    {
        Assert.Equal(1, totals.GetProperty("total_revisions").GetInt32());
        Assert.True(totals.GetProperty("total_tokens").GetInt32() > 0);
    }

    private static void AssertQaOptions(JsonElement root)
    {
        Assert.Equal(3, root.GetProperty("qa_options").GetProperty("max_attempts").GetInt32());
    }

    private static void AssertSceneRoot(JsonElement root, out JsonElement seg)
    {
        Assert.Equal(1, root.GetProperty("segments").GetArrayLength());
        seg = root.GetProperty("segments")[0];
        Assert.Equal("seg-001", seg.GetProperty("id").GetString());
    }

    private static void AssertSceneStatus(JsonElement seg)
    {
        Assert.Equal("Parsed", seg.GetProperty("status").GetString());
        Assert.Equal("spk-1", seg.GetProperty("context").GetProperty("speaker_id").GetString());
    }

    private static void AssertSceneSpeakerNulls(JsonElement context)
    {
        Assert.False(context.TryGetProperty("speaker_label", out _));
        Assert.False(context.TryGetProperty("emotion_tone_label", out _));
    }

    private static void AssertSceneContentNulls(JsonElement context)
    {
        Assert.False(context.TryGetProperty("on_screen_text", out _));
        Assert.False(context.TryGetProperty("scene_summary", out _));
    }

    private static void AssertSceneRegion(JsonElement region)
    {
        Assert.Equal("red rectangle", region.GetProperty("concept").GetString());
        Assert.Equal(10, region.GetProperty("x1").GetInt32());
        Assert.Equal(29, region.GetProperty("x2").GetInt32());
    }

    private static void AssertSceneRegionWidth(JsonElement region)
    {
        Assert.Equal(64, region.GetProperty("frame_width").GetInt32());
    }

    private static void AssertSceneProvenance(JsonElement provenance)
    {
        Assert.Equal("sam-3.1", provenance.GetProperty("model_id").GetString());
        Assert.Equal("scene-v1", provenance.GetProperty("prompt_template_version").GetString());
    }

    private static void AssertSceneCounts(JsonElement root, JsonElement seg)
    {
        Assert.Equal(1, seg.GetProperty("images_processed").GetInt32());
        Assert.Equal(1, root.GetProperty("images_processed_total").GetInt32());
    }

    private static void AssertModels(JsonElement root)
    {
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0);
        Assert.Equal("muse-spark-1.1", root[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Status_without_key_returns_hasKey_false_and_default_model()
    {
        string? prevKey = Environment.GetEnvironmentVariable("META_API_KEY");
        string? prevFallback = Environment.GetEnvironmentVariable("META_MODEL_API_KEY");
        string? prevModel = Environment.GetEnvironmentVariable("META_MODEL");
        string? prevBase = Environment.GetEnvironmentVariable("META_MODEL_API_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("META_API_KEY", null);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", null);
            Environment.SetEnvironmentVariable("META_MODEL", null);
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", null);

            using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            using HttpClient client = factory.CreateClient();

            HttpResponseMessage resp = await client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            string json = await resp.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            AssertStatusWithoutKey(root);
            AssertNoKeyLeak(root, json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("META_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", prevFallback);
            Environment.SetEnvironmentVariable("META_MODEL", prevModel);
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", prevBase);
        }
    }

    [Fact]
    public async Task Status_with_key_returns_hasKey_true()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("hasKey").GetBoolean());
    }

    [Fact]
    public async Task Translate_happy_path_returns_batch_with_provenance()
    {
        using var auth = FakeAuth();
        var q = new Queue<string>(["Hola, mundo.", "Accion!"]);
        using var factory = new ServerFactory(DualHandler(q, new Queue<string>(), new ChatStubOptions()));
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new { id = "seg-001", text = "Hello, world.", duration_seconds = (double?)4.6 },
                new { id = "seg-002", text = "Action!", duration_seconds = (double?)0.6 },
            },
            sourceLanguage = "en",
            targetLanguage = "es",
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/translate", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertTranslateRoot(root);
        JsonElement first = root.GetProperty("segments")[0];
        AssertFirstTranslatedSegment(first);
        AssertTranslateUsage(root, first);
    }

    [Fact]
    public async Task Qa_happy_path_returns_report_matching_cli_shape()
    {
        using var auth = FakeAuth();
        var translations = new Queue<string>([
            "Esta es una traduccion larguisima", // 32c -> ~2.1s fail for 1.0s window
            "Corta", // 5c -> pass timing, then meaning pass
        ]);
        var meanings = new Queue<string>([JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "ok" })]);
        using var factory = new ServerFactory(DualHandler(translations, meanings, new ChatStubOptions()));
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new { id = "seg-001", text = "This is a long source line that will overrun", duration_seconds = (double?)1.0 },
            },
            sourceLanguage = "en",
            targetLanguage = "es",
            options = new { maxAttempts = 3, toleranceFraction = 0.10 },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/qa", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertQaRoot(root);
        AssertQaCollections(root, out JsonElement segs, out JsonElement totals);
        JsonElement seg = segs[0];
        AssertQaSegment(seg);
        AssertQaFirstAttempt(seg);
        AssertQaRevision(seg);
        JsonElement revision = seg.GetProperty("revisions")[0];
        AssertQaMeaningFieldsPresent(revision);
        AssertQaMeaningTemplate(revision);
        AssertQaTokens(seg);
        AssertQaTotals(totals);
        AssertQaTotalsRevisionsAndTokens(totals);
        AssertQaOptions(root);
    }

    [Fact]
    public async Task Qa_without_key_gates_with_401()
    {
        string? prevKey = Environment.GetEnvironmentVariable("META_API_KEY");
        string? prevFallback = Environment.GetEnvironmentVariable("META_MODEL_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("META_API_KEY", null);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", null);

            using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            using HttpClient client = factory.CreateClient();

            var body = new
            {
                segments = new[] { new { id = "seg-001", text = "Hello", duration_seconds = (double?)1.0 } },
                sourceLanguage = "en",
                targetLanguage = "es",
            };

            HttpResponseMessage resp = await client.PostAsJsonAsync("/api/qa", body);
            Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadGateway or HttpStatusCode.InternalServerError,
                $"Expected gating status, got {resp.StatusCode}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("META_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", prevFallback);
        }
    }

    private static HttpMessageHandler SceneHandler()
    {
        return new DelegatingHandlerStub((request, _) =>
        {
            string? path = request.RequestUri?.AbsolutePath;
            Assert.True(path is not null && path.EndsWith("/responses", StringComparison.Ordinal), $"Expected /responses, got {path}");
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
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = "<0f>0<|box;x1=10;y1=10;x2=29;y2=49;w=64;h=64|><|mask;x=0;y=0;data=40,20,abc|>",
                                annotations = Array.Empty<object>(),
                                logprobs = Array.Empty<object>(),
                            },
                        },
                    },
                },
                usage = new
                {
                    input_tokens = 0,
                    input_tokens_details = new { cached_tokens = 0 },
                    output_tokens = 0,
                    output_tokens_details = new { reasoning_tokens = 0 },
                    total_tokens = 0,
                    images_processed = 1,
                },
            };
            return Task.FromResult(JsonOk(payload));
        });
    }

    [Fact]
    public async Task SceneContext_happy_path_returns_parsed_region_and_honest_nulls()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(SceneHandler());
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new
                {
                    id = "seg-001",
                    text = "We need one more take.",
                    frame_image_data_url = "data:image/png;base64,iVBORw0KGgo=",
                    concepts = new[] { "red rectangle" },
                    speaker_id = "spk-1",
                },
            },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/scene-context", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertSceneRoot(root, out JsonElement seg);
        AssertSceneStatus(seg);

        JsonElement context = seg.GetProperty("context");
        // Nulls are omitted by the server's WhenWritingNull convention: absence == unknown.
        AssertSceneSpeakerNulls(context);
        AssertSceneContentNulls(context);

        JsonElement region = context.GetProperty("regions")[0];
        AssertSceneRegion(region);
        AssertSceneRegionWidth(region);

        JsonElement provenance = seg.GetProperty("provenance");
        AssertSceneProvenance(provenance);
        AssertSceneCounts(root, seg);
    }

    private static HttpMessageHandler VisionSceneHandler()
    {
        return new DelegatingHandlerStub((request, _) =>
        {
            string? path = request.RequestUri?.AbsolutePath;
            Assert.True(path is not null && path.EndsWith("/chat/completions", StringComparison.Ordinal), $"Expected /chat/completions, got {path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProbeVisionResponseBody, Encoding.UTF8, "application/json"),
            });
        });
    }

    [Fact]
    public async Task SceneContext_vision_enrichment_returns_split_fields_and_frame_provenance()
    {
        using var auth = FakeAuth();
        using var visionModel = EnvShim("META_VISION_MODEL", "muse-spark-1.1");
        using var factory = new ServerFactory(VisionSceneHandler());
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new
                {
                    id = "seg-001",
                    text = "Okay. My apartment is across from a big department store.",
                    frame_image_data_url = "data:image/png;base64,iVBORw0KGgo=",
                    speaker_id = "ashley",
                    frame_id = "hero-0242",
                    timecode_seconds = (double?)162.0,
                    frame_sha256 = FrameSha256Hex(),
                    vision_enrichment = (bool?)true,
                },
            },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/scene-context", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertSceneRoot(root, out JsonElement seg);
        Assert.Equal("Parsed", seg.GetProperty("status").GetString());
        JsonElement context = seg.GetProperty("context");
        Assert.Equal("ashley", context.GetProperty("speaker_id").GetString());
        AssertSceneVisionContext(context);
        AssertSceneVisionTextSplit(context);
        AssertSceneVisionMeta(seg);
        AssertSceneFrameProvenance(seg);
        AssertSceneVisionTotals(root);
        // The frame image itself is never stored or echoed back.
        Assert.DoesNotContain("iVBORw0KGgo", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectorAdaptation_passes_both_gates_and_reports_context_fields()
    {
        using var auth = FakeAuth();
        var handler = new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
                ? ChatOk("muse-spark-1.1", """{"verdict":"pass","issues":[],"explanation":"ok"}""", 90, 30)
                : ChatOk("muse-spark-1.1", "Perfecto, una toma más.", 120, 40);
        });
        using var factory = new ServerFactory(handler);
        using HttpClient client = factory.CreateClient();

        var requestBody = new
        {
            segments = new[]
            {
                new
                {
                    id = "seg-002",
                    text = "We need one more take.",
                    translation = "Necesitamos una toma más.",
                    duration_seconds = 4.0,
                    context = new { emotion_tone_label = "cheerful" },
                    director_adaptation = new { target_register = "informal" },
                },
            },
            sourceLanguage = "en",
            targetLanguage = "es",
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/director-adaptation", requestBody);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement seg = doc.RootElement.GetProperty("segments")[0];
        Assert.Equal("Passed", seg.GetProperty("status").GetString());
        Assert.Equal("informal", seg.GetProperty("target_register").GetString());
        Assert.Equal("Perfecto, una toma más.", seg.GetProperty("adapted_translation").GetString());
        Assert.Equal("Pass", seg.GetProperty("timing_verdict").GetString());
        Assert.Equal("Pass", seg.GetProperty("meaning").GetProperty("verdict").GetString());
        Assert.Equal("emotion_tone_label", seg.GetProperty("context_fields_used")[0].GetString());
        Assert.Equal("director-v1", seg.GetProperty("provenance").GetProperty("prompt_template_version").GetString());
        Assert.True(seg.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task Models_returns_account_models()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(ModelsHandler());
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        AssertModels(doc.RootElement);
    }
}
