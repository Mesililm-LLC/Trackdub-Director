using System.Net;
using System.Text;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Safety checks for audition output: winning-take file names and the per-run Meta call budget.
/// </summary>
public sealed class AuditionSafetyTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void TakeFileName_keeps_plain_ids_unchanged()
    {
        Assert.Equal("seg-001-take2.wav", AuditionService.TakeFileName("seg-001", 2));
    }

    [Theory]
    [InlineData("../../etc/cron.d/evil")]
    [InlineData("/absolute/path")]
    [InlineData("..\\..\\outside")]
    [InlineData("C:evil")]
    public void TakeFileName_never_contains_a_separator_or_parent_reference(string segmentId)
    {
        string name = AuditionService.TakeFileName(segmentId, 1);

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain("..", name);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.EndsWith("-take1.wav", name, StringComparison.Ordinal);
    }

    [Fact]
    public void TakeFileName_keeps_ids_that_sanitize_to_the_same_stem_apart()
    {
        Assert.NotEqual(AuditionService.TakeFileName("a/b", 1), AuditionService.TakeFileName("a_b", 1));
    }

    [Fact]
    public void TakeFileName_keeps_long_clean_ids_that_share_a_prefix_apart()
    {
        string prefix = new('a', 64);
        Assert.NotEqual(AuditionService.TakeFileName(prefix + "x", 1), AuditionService.TakeFileName(prefix + "y", 1));
    }

    [Fact]
    public void TakeFileName_never_gives_a_clean_id_the_name_of_a_sanitized_one()
    {
        // The clean id spelled with the hashed name's '.' replaced by '-' must not collide with it.
        string hashed = AuditionService.TakeFileName("a/b", 1);
        string clean = hashed[..^"-take1.wav".Length].Replace('.', '-');

        Assert.NotEqual(hashed, AuditionService.TakeFileName(clean, 1));
    }

    [Fact]
    public void CallBudget_refuses_requests_once_the_limit_is_reached()
    {
        var budget = new CallBudget(2);

        budget.Reserve();
        budget.Reserve();

        Assert.Equal(2, budget.Used);
        Assert.Equal(0, budget.Remaining);
        Assert.Throws<MetaBudgetExhaustedException>(() => budget.Reserve());
        Assert.Equal(2, budget.Used);
    }

    [Fact]
    public async Task Budgeted_client_refuses_before_sending_when_the_budget_is_spent()
    {
        string? previous = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
        try
        {
            var handler = new CountingHandler();
            using var http = new HttpClient(handler);
            MetaClient client = new MetaClient(http).WithBudget(new CallBudget(0));
            var request = new MetaClient.ChatRequest([new MetaClient.ChatMessage("user", "hello")]);

            await Assert.ThrowsAsync<MetaBudgetExhaustedException>(() => client.ChatCompletionAsync(request));

            Assert.Equal(0, handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previous);
        }
    }
}
