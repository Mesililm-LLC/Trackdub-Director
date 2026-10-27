using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class TranslationServiceTests
{
    private static bool HasApiKey =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("META_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("META_MODEL_API_KEY"));

    private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        string json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpMessageHandler FakeChatHandler(
        string modelId,
        Func<int, string> translationForIndex,
        int promptTokens = 10,
        int completionTokens = 5)
    {
        int callIndex = 0;
        return new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // Basic sanity: request targets chat/completions
            Assert.Contains("/chat/completions", request.RequestUri?.ToString() ?? string.Empty);

            int idx = callIndex++;
            string translation = translationForIndex(idx);

            var responsePayload = new
            {
                id = $"chatcmpl-test-{idx}",
                @object = "chat.completion",
                created = 1234567890L,
                model = modelId,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        finish_reason = "stop",
                        message = new { role = "assistant", content = translation },
                    },
                },
                usage = new
                {
                    prompt_tokens = promptTokens + idx,
                    completion_tokens = completionTokens + idx,
                    total_tokens = promptTokens + completionTokens + (idx * 2),
                },
            };

            return JsonResponse(responsePayload);
        });
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    [Fact]
    public async Task TranslateBatch_records_provenance_and_usage_per_line()
    {
        // No real API key needed; inject fake handler. But MetaClient still reads env for key,
        // so set a fake key via env shim or pass through handler that short-circuits before auth.
        // Easiest: set env for this test, restore after.
        string? prevKey = Environment.GetEnvironmentVariable("META_API_KEY");
        string? prevFallback = Environment.GetEnvironmentVariable("META_MODEL_API_KEY");
        string? prevModel = Environment.GetEnvironmentVariable("META_MODEL");
        string? prevBase = Environment.GetEnvironmentVariable("META_MODEL_API_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("META_API_KEY", "test-fake-key");
            Environment.SetEnvironmentVariable("META_MODEL", "muse-spark-1.1");
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1");

            string[] canned = ["Hola, mundo.", "Accion!", "Silencio en el set."];

            var handler = FakeChatHandler("muse-spark-1.1", idx => canned[idx]);
            using var httpClient = new HttpClient(handler);
            using var metaClient = new MetaClient(httpClient);
            var service = new TranslationService(metaClient);

            var inputs = new[]
            {
                new TranslationSegmentInput("seg-001", "Hello, world."),
                new TranslationSegmentInput("seg-002", "Action!"),
                new TranslationSegmentInput("seg-003", "Quiet on set."),
            };

            TranslationBatchOutput batch = await service.TranslateBatchAsync(inputs, "en", "es");

            Assert.Equal("en", batch.SourceLanguage);
            Assert.Equal("es", batch.TargetLanguage);
            Assert.Equal(3, batch.Segments.Length);

            // Per-line provenance and usage
            for (int i = 0; i < batch.Segments.Length; i++)
            {
                TranslatedSegmentOutput seg = batch.Segments[i];
                Assert.Equal(inputs[i].Id, seg.Id);
                Assert.Equal(inputs[i].Text, seg.Text);
                Assert.Equal(canned[i], seg.Translation);

                Assert.Equal("muse-spark-1.1", seg.Provenance.ModelId);
                Assert.Equal("api.meta.ai", seg.Provenance.EndpointHost);
                Assert.Equal(TranslationService.PromptTemplateVersion, seg.Provenance.PromptTemplateVersion);
                Assert.False(string.IsNullOrWhiteSpace(seg.Provenance.TimestampUtc));
                Assert.True(DateTime.TryParse(seg.Provenance.TimestampUtc, out DateTime parsed));
                Assert.True(
                    seg.Provenance.TimestampUtc.EndsWith('Z') || seg.Provenance.TimestampUtc.Contains("+00:00", StringComparison.Ordinal),
                    $"Expected UTC timestamp, got '{seg.Provenance.TimestampUtc}'");

                // Usage from canned response, with per-call increment
                Assert.Equal(10 + i, seg.Usage.PromptTokens);
                Assert.Equal(5 + i, seg.Usage.CompletionTokens);
                Assert.Equal(15 + (i * 2), seg.Usage.TotalTokens);
            }

            // Totals
            Assert.Equal(10 + 11 + 12, batch.UsageTotal.PromptTokens);
            Assert.Equal(5 + 6 + 7, batch.UsageTotal.CompletionTokens);
            Assert.Equal(15 + 17 + 19, batch.UsageTotal.TotalTokens);
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
    public async Task TranslateBatch_round_trips_through_json_files()
    {
        string? prevKey = Environment.GetEnvironmentVariable("META_API_KEY");
        string? prevModel = Environment.GetEnvironmentVariable("META_MODEL");
        string? prevBase = Environment.GetEnvironmentVariable("META_MODEL_API_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("META_API_KEY", "test-fake-key");
            Environment.SetEnvironmentVariable("META_MODEL", "muse-spark-1.1");
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1");

            var handler = FakeChatHandler("muse-spark-1.1", idx => $"Translated {idx}");
            using var httpClient = new HttpClient(handler);
            using var metaClient = new MetaClient(httpClient);
            var service = new TranslationService(metaClient);

            string tmpInput = Path.Combine(Path.GetTempPath(), $"td-test-input-{Guid.NewGuid():N}.json");
            string tmpOutput = Path.Combine(Path.GetTempPath(), $"td-test-output-{Guid.NewGuid():N}.json");

            try
            {
                var inputs = new[]
                {
                    new TranslationSegmentInput("a", "Hello"),
                    new TranslationSegmentInput("b", "Goodbye"),
                };
                string inJson = JsonSerializer.Serialize(inputs, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(tmpInput, inJson);

                IReadOnlyList<TranslationSegmentInput> loaded = await service.LoadSegmentsAsync(tmpInput);
                Assert.Equal(2, loaded.Count);

                TranslationBatchOutput batch = await service.TranslateBatchAsync(loaded, "en", "es");
                await service.WriteOutputAsync(tmpOutput, batch);

                Assert.True(File.Exists(tmpOutput));
                string outJson = await File.ReadAllTextAsync(tmpOutput);
                var deserialized = JsonSerializer.Deserialize<TranslationBatchOutput>(outJson);
                Assert.NotNull(deserialized);
                Assert.Equal(2, deserialized!.Segments.Length);
                Assert.Equal("Translated 0", deserialized.Segments[0].Translation);
                Assert.Equal("Translated 1", deserialized.Segments[1].Translation);
                Assert.All(deserialized.Segments, s => Assert.False(string.IsNullOrWhiteSpace(s.Provenance.ModelId)));
                Assert.All(deserialized.Segments, s => Assert.True(s.Usage.TotalTokens > 0));
            }
            finally
            {
                if (File.Exists(tmpInput))
                {
                    File.Delete(tmpInput);
                }

                if (File.Exists(tmpOutput))
                {
                    File.Delete(tmpOutput);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("META_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("META_MODEL", prevModel);
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", prevBase);
        }
    }

    [Fact]
    public async Task Live_translate_batch_runs_only_with_api_key()
    {
        if (!HasApiKey)
        {
            return;
        }

        using var client = new MetaClient();
        var service = new TranslationService(client);

        var inputs = new[]
        {
            new TranslationSegmentInput("seg-001", "Hello, world."),
            new TranslationSegmentInput("seg-002", "Action!"),
        };

        TranslationBatchOutput batch = await service.TranslateBatchAsync(inputs, "en", "es");

        Assert.Equal(2, batch.Segments.Length);
        foreach (TranslatedSegmentOutput seg in batch.Segments)
        {
            Assert.False(string.IsNullOrWhiteSpace(seg.Translation));
            Assert.False(string.IsNullOrWhiteSpace(seg.Provenance.ModelId));
            Assert.Equal("api.meta.ai", seg.Provenance.EndpointHost);
            Assert.False(string.IsNullOrWhiteSpace(seg.Provenance.TimestampUtc));
            Assert.Equal(TranslationService.PromptTemplateVersion, seg.Provenance.PromptTemplateVersion);
            Assert.True(seg.Usage.PromptTokens >= 0);
            Assert.True(seg.Usage.CompletionTokens >= 0);
            Assert.True(seg.Usage.TotalTokens >= 0);
        }

        Assert.True(batch.UsageTotal.TotalTokens > 0);
    }
}
