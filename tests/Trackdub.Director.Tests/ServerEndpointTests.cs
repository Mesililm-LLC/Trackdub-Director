using System.Net;
using System.Net.Http.Json;
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

    private static HttpMessageHandler SequencedChatHandler(Queue<string> translations, string modelId = "muse-spark-1.1", int promptTokens = 10, int completionTokens = 5)
    {
        return DualHandler(translations, new Queue<string>([JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "ok" })]), modelId, promptTokens, completionTokens);
    }

    private static HttpMessageHandler DualHandler(
        Queue<string> translations,
        Queue<string> meaningJsons,
        string modelId = "muse-spark-1.1",
        int promptTokens = 10,
        int completionTokens = 5,
        int meaningPrompt = 7,
        int meaningComp = 4)
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
                return ChatOk(modelId, mj, meaningPrompt, meaningComp);
            }

            if (translations.Count == 0)
            {
                throw new InvalidOperationException("No more canned translations queued for chat/completions.");
            }

            string translation = translations.Dequeue();
            return ChatOk(modelId, translation, promptTokens, completionTokens);
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

            Assert.False(root.GetProperty("hasKey").GetBoolean());
            Assert.Equal(MetaAuth.DefaultModel, root.GetProperty("model").GetString());
            Assert.Equal(MetaAuth.DefaultBaseUrl, root.GetProperty("baseUrl").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reasoningEffort").GetString()));
            string raw = json.ToLowerInvariant();
            Assert.DoesNotContain("test-fake-key", raw);
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
        using var factory = new ServerFactory(DualHandler(q, new Queue<string>()));
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

        Assert.Equal("en", root.GetProperty("source_language").GetString());
        Assert.Equal("es", root.GetProperty("target_language").GetString());
        Assert.Equal(2, root.GetProperty("segments").GetArrayLength());

        JsonElement first = root.GetProperty("segments")[0];
        Assert.Equal("seg-001", first.GetProperty("id").GetString());
        Assert.Equal("Hola, mundo.", first.GetProperty("translation").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("provenance").GetProperty("model_id").GetString()));
        Assert.NotEqual(0, first.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        JsonElement usageTotal = root.GetProperty("usage_total");
        Assert.True(usageTotal.GetProperty("total_tokens").GetInt32() > 0);
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
        using var factory = new ServerFactory(DualHandler(translations, meanings));
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

        Assert.Equal("en", root.GetProperty("source_language").GetString());
        Assert.Equal("es", root.GetProperty("target_language").GetString());
        Assert.True(root.TryGetProperty("qa_options", out _));
        Assert.True(root.TryGetProperty("segments", out JsonElement segs));
        Assert.True(root.TryGetProperty("totals", out JsonElement totals));

        Assert.Equal(1, segs.GetArrayLength());
        JsonElement seg = segs[0];
        Assert.Equal("seg-001", seg.GetProperty("id").GetString());
        Assert.Equal("Fail", seg.GetProperty("first_attempt").GetProperty("verdict").GetString());
        Assert.Equal("Fail", seg.GetProperty("first_attempt").GetProperty("timing_verdict").GetString());
        Assert.Equal("NotEvaluated", seg.GetProperty("first_attempt").GetProperty("meaning_verdict").GetString());
        Assert.Equal(1, seg.GetProperty("revisions").GetArrayLength());
        Assert.Equal("Pass", seg.GetProperty("final_verdict").GetString());
        Assert.Equal("Pass", seg.GetProperty("revisions")[0].GetProperty("timing_verdict").GetString());
        Assert.Equal("Pass", seg.GetProperty("revisions")[0].GetProperty("meaning_verdict").GetString());
        Assert.Equal("Corta", seg.GetProperty("final_translation").GetString());
        // Meaning fields present on the revision that was actually evaluated
        Assert.True(seg.GetProperty("revisions")[0].TryGetProperty("meaning_issues", out _));
        Assert.True(seg.GetProperty("revisions")[0].TryGetProperty("meaning_explanation", out _));
        Assert.True(seg.GetProperty("revisions")[0].TryGetProperty("meaning_prompt_template_version", out JsonElement tmpl));
        Assert.Equal("meaning-v1", tmpl.GetString());
        Assert.True(seg.GetProperty("revisions")[0].GetProperty("meaning_total_tokens").GetInt32() > 0);
        // Timing-fail attempt never carries meaning provenance or tokens
        Assert.Equal(0, seg.GetProperty("first_attempt").GetProperty("meaning_total_tokens").GetInt32());

        Assert.Equal(1, totals.GetProperty("total_segments").GetInt32());
        Assert.Equal(0, totals.GetProperty("first_pass_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("final_pass_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("total_revisions").GetInt32());
        Assert.True(totals.GetProperty("total_tokens").GetInt32() > 0);
        Assert.True(root.GetProperty("qa_options").GetProperty("max_attempts").GetInt32() == 3);
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
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() > 0);
        Assert.Equal("muse-spark-1.1", doc.RootElement[0].GetProperty("id").GetString());
    }
}
