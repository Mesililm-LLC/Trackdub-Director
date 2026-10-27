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

    private static HttpMessageHandler SequencedHandler(
        string modelId,
        Queue<string> translations,
        int promptTokens = 10,
        int completionTokens = 5)
    {
        return new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Assert.Contains("/chat/completions", request.RequestUri?.ToString() ?? string.Empty);

            if (translations.Count == 0)
            {
                throw new InvalidOperationException("No more canned translations queued.");
            }

            string translation = translations.Dequeue();

            var payload = new
            {
                id = $"chatcmpl-test-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = 1234567890L,
                model = modelId,
                choices = new[]
                {
                    new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = translation } },
                },
                usage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = completionTokens,
                    total_tokens = promptTokens + completionTokens,
                },
            };

            return JsonResponse(payload);
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
        // Set all keys the client reads; restore on dispose
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

    [Fact]
    public async Task Line_passes_first_try()
    {
        using var _ = FakeAuth();

        // Window 4.2s at 15 cps = 63 chars max, 69 with tolerance. Short translation fits.
        var q = new Queue<string>(["Hola."]); // 5 chars -> 0.33s fits 4.2s
        using var http = new HttpClient(SequencedHandler("muse-spark-1.1", q));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-a", "Hello.", DurationSeconds: 4.2) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        Assert.Single(report.Segments);
        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Pass, seg.FirstAttempt.Verdict);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Empty(seg.Revisions);
        Assert.Equal(1, report.Totals.TotalSegments);
        Assert.Equal(1, report.Totals.FirstPassCount);
        Assert.Equal(1, report.Totals.FinalPassCount);
        Assert.Equal(0, report.Totals.TotalRevisions);
        Assert.Equal("Hola.", seg.FirstAttempt.Translation);
        Assert.Equal("Hola.", seg.FinalTranslation);
        Assert.True(report.Totals.TotalTokens > 0);
    }

    [Fact]
    public async Task Line_fails_then_revised_and_passes()
    {
        using var _ = FakeAuth();

        // Window 1.0s at 15 cps = 15 chars, tol 16. First translation too long, revision shortened.
        var q = new Queue<string>([
            "Esta es una traduccion muy larga que no cabe", // 44c -> 2.93s -> fail
            "Traduccion corta", // 16c -> 1.07s -> with 10% toler 1.1s -> pass
        ]);
        using var http = new HttpClient(SequencedHandler("muse-spark-1.1", q));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-b", "This is a long source line that will overrun", DurationSeconds: 1.0) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Single(seg.Revisions);
        Assert.Equal(QaVerdict.Pass, seg.Revisions[0].Verdict);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
        Assert.Equal(0, report.Totals.FirstPassCount);
        Assert.Equal(1, report.Totals.FinalPassCount);
        Assert.Equal(1, report.Totals.TotalRevisions);
        Assert.Equal("Traduccion corta", seg.FinalTranslation);
        // Token usage summed across both attempts
        Assert.Equal(20, seg.TokenUsage.PromptTokens);
        Assert.Equal(10, seg.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task Line_never_fits_reported_failed_after_max_attempts()
    {
        using var _ = FakeAuth();

        // Window 0.4s at 15 cps -> max 6c (6 with tol). All translations too long -> never fits.
        var q = new Queue<string>([
            "Traduccion larguisima imposible de acortar sin perder sentido",
            "Traduccion larguisima",
            "Traduccion larga",
            "Texto largo",
        ]);
        using var http = new HttpClient(SequencedHandler("muse-spark-1.1", q));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 3, ToleranceFraction = 0.10 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-c", "Hello world", DurationSeconds: 0.4) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Equal(3, seg.Revisions.Length);
        foreach (QaAttemptReport rev in seg.Revisions)
        {
            Assert.Equal(QaVerdict.Fail, rev.Verdict);
        }

        Assert.Equal(QaVerdict.Fail, seg.FinalVerdict);
        Assert.Equal(0, report.Totals.FinalPassCount);
        Assert.Equal(3, report.Totals.TotalRevisions);
        // Last attempted translation is the final one, even though it failed
        Assert.Equal("Texto largo", seg.FinalTranslation);
        // 4 model calls (initial + 3 revisions)
        Assert.Equal(40, seg.TokenUsage.PromptTokens);
        Assert.Equal(20, seg.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task Window_estimated_from_source_when_duration_absent()
    {
        using var _ = FakeAuth();

        // No duration_seconds -> window = estimate from source text length
        // Source "Hi" = 2 chars -> at 15 cps = 0.133s window. Translation "Hola" = 4c -> 0.267s -> needs revision
        var q = new Queue<string>(["Hola", "Hi"]);
        using var http = new HttpClient(SequencedHandler("muse-spark-1.1", q));
        using var client = new MetaClient(http);
        var opts = new QaOptions { MaxAttempts = 2, ToleranceFraction = 0.0 };
        var svc = new QaService(client, opts);

        var segs = new[] { new QaSegmentInput("seg-d", "Hi", DurationSeconds: null) };
        QaReport report = await svc.RunAsync(segs, "en", "es");

        QaSegmentReport seg = report.Segments[0];
        Assert.Equal(QaVerdict.Fail, seg.FirstAttempt.Verdict);
        Assert.Single(seg.Revisions);
        Assert.Equal(QaVerdict.Pass, seg.FinalVerdict);
    }

    [Fact]
    public async Task Live_qa_runs_only_with_api_key()
    {
        bool hasKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("META_API_KEY"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("META_MODEL_API_KEY"));
        if (!hasKey)
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
