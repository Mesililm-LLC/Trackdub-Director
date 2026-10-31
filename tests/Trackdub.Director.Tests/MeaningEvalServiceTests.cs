using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class MeaningEvalServiceTests
{
    /// <summary>Answers each meaning call with the next scripted verdict, or an HTTP 500 for "error".</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _verdicts;

        public ScriptedHandler(params string[] verdicts) => _verdicts = new Queue<string>(verdicts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string verdict = _verdicts.Dequeue();
            if (verdict == "error")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
            }

            string content = JsonSerializer.Serialize(new { verdict, issues = verdict == "fail" ? new[] { "negation flipped" } : Array.Empty<string>(), explanation = "scripted" });
            var payload = new
            {
                id = "chatcmpl-test-eval",
                @object = "chat.completion",
                model = "muse-spark-1.1",
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = 300, completion_tokens = 100, total_tokens = 400 },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeAuth : IDisposable
    {
        private readonly string? _key = Environment.GetEnvironmentVariable("META_API_KEY");
        private readonly string? _baseUrl = Environment.GetEnvironmentVariable("META_MODEL_API_BASE_URL");

        public FakeAuth()
        {
            Environment.SetEnvironmentVariable("META_API_KEY", "test-fake-key");
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("META_API_KEY", _key);
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", _baseUrl);
        }
    }

    private static MeaningEvalCase Seeded(string id) => new(id, "We are not shooting tomorrow.", "Rodamos mañana.", MeaningVerdict.Fail, "negation");

    private static MeaningEvalCase Clean(string id) => new(id, "We are not shooting tomorrow.", "No rodamos mañana.", MeaningVerdict.Pass);

    [Theory]
    [InlineData(MeaningVerdict.Fail, MeaningVerdict.Fail, MeaningEvalOutcome.Caught)]
    [InlineData(MeaningVerdict.Fail, MeaningVerdict.Pass, MeaningEvalOutcome.Missed)]
    [InlineData(MeaningVerdict.Pass, MeaningVerdict.Pass, MeaningEvalOutcome.CleanPass)]
    [InlineData(MeaningVerdict.Pass, MeaningVerdict.Fail, MeaningEvalOutcome.FalseAlarm)]
    [InlineData(MeaningVerdict.Fail, MeaningVerdict.Error, MeaningEvalOutcome.ApiError)]
    [InlineData(MeaningVerdict.Pass, MeaningVerdict.Error, MeaningEvalOutcome.ApiError)]
    public void ScoreMapsLabelAndVerdictToOutcome(MeaningVerdict expected, MeaningVerdict actual, MeaningEvalOutcome outcome)
    {
        Assert.Equal(outcome, MeaningEvalService.Score(expected, actual));
    }

    [Fact]
    public async Task RunScoresEachCaseAndLeavesApiErrorsOutOfBothRates()
    {
        using var auth = new FakeAuth();
        using var client = new MetaClient(new HttpClient(new ScriptedHandler("fail", "pass", "pass", "fail", "error")));
        MeaningEvalCase[] cases = [Seeded("s1"), Seeded("s2"), Clean("c1"), Clean("c2"), Seeded("s3")];

        MeaningEvalReport report = await new MeaningEvalService(client).RunAsync(cases, "en", "es");

        MeaningEvalSummary s = report.Summary;
        Assert.Equal((5, 3, 2), (s.Cases, s.SeededErrors, s.Clean));
        Assert.Equal((1, 1, 1, 1), (s.Caught, s.Missed, s.FalseAlarms, s.ApiErrors));
        Assert.Equal(0.5, s.Recall);
        Assert.Equal(0.5, s.FalsePositiveRate);
        Assert.Equal(1600, s.TotalTokens);
        Assert.Equal(MeaningEvalOutcome.ApiError, report.Results[4].Outcome);
        Assert.Equal(new MeaningEvalErrorTypeScore("negation", 2, 1), Assert.Single(s.ByErrorType));
    }

    [Fact]
    public void RatesAreNullWhenNothingWasJudged()
    {
        MeaningEvalSummary s = MeaningEvalService.Summarize([]);

        Assert.Null(s.Recall);
        Assert.Null(s.FalsePositiveRate);
    }

    [Theory]
    [InlineData("[]", "at least one case")]
    [InlineData("[{\"id\":\"x\",\"source_text\":\"Hi\",\"candidate\":\"\",\"expected\":\"Pass\"}]", "needs id, source_text, and candidate")]
    [InlineData("[{\"id\":\"x\",\"source_text\":\"Hi\",\"candidate\":\"Hola\",\"expected\":\"Error\"}]", "must expect Pass or Fail")]
    [InlineData("not json", "Invalid meaning-eval cases JSON")]
    [InlineData("[null]", "entry 0 is null")]
    public async Task LoadRejectsMalformedCaseFiles(string json, string message)
    {
        string path = Path.Combine(Path.GetTempPath(), $"meaning-eval-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => MeaningEvalService.LoadCasesAsync(path));
            Assert.Contains(message, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
