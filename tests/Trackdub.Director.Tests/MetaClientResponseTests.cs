using System.Net;
using System.Text;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Response-shape tests for <see cref="MetaClient"/>: upstream HTTP errors, error objects in
/// successful responses, malformed bodies, and the concept-miss versus malformed distinction
/// on the Responses path. Each test pins one failure mode with a fake handler.
/// </summary>
public sealed class MetaClientResponseTests
{
    private const string FrameDataUrl = "data:image/png;base64,iVBORw0KGgo=";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request, cancellationToken);
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public EnvScope(IReadOnlyDictionary<string, string> values)
        {
            foreach (KeyValuePair<string, string> entry in values)
            {
                _previous[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> entry in _previous)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }

    private static IDisposable FakeAuth()
    {
        return new EnvScope(new Dictionary<string, string>
        {
            ["META_API_KEY"] = "test-fake-key",
            ["META_MODEL_API_BASE_URL"] = "https://api.meta.ai/v1",
            ["META_SCENE_MODEL"] = "sam-3.1",
        });
    }

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) => Task.FromResult(new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    });

    private static MetaClient.ChatRequest Chat() => new([new MetaClient.ChatMessage("user", "hello")]);

    private static MetaClient.SegmentationRequest Segmentation() => new(FrameDataUrl, "red rectangle");

    private static string ResponsesBody(string outputJson) =>
        $"{{\"status\":\"completed\",\"output\":{outputJson}}}";

    [Fact]
    public async Task Chat_http_error_surfaces_the_status_code()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.ChatCompletionAsync(Chat()));

        Assert.Contains("HTTP 500", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"   \"}}]}")]
    public async Task Chat_malformed_success_body_is_rejected(string body)
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.OK, body)));
        var client = new MetaClient(http);

        await Assert.ThrowsAsync<MetaApiException>(() => client.ChatCompletionAsync(Chat()));
    }

    [Fact]
    public async Task Responses_http_error_surfaces_the_status_code()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("HTTP 500", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_error_object_in_a_200_response_is_rejected()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.OK, "{\"error\":{\"message\":\"quota exceeded\"}}")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("quota exceeded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_non_completed_status_is_rejected()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.OK, "{\"status\":\"failed\",\"output\":[]}")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("not completed (status: failed)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_non_object_root_is_rejected()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.OK, "[]")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("Unexpected Meta Model API response shape", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_without_an_output_text_part_is_malformed_not_a_concept_miss()
    {
        using var auth = FakeAuth();
        string body = ResponsesBody("[{\"type\":\"message\",\"content\":[{\"type\":\"reasoning\",\"text\":\"thinking\"}]}]");
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.OK, body)));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("missing output_text part", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_explicit_empty_output_text_is_a_concept_miss()
    {
        using var auth = FakeAuth();
        string body = ResponsesBody("[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"\"}]}]");
        using var http = new HttpClient(new StubHandler((_, _) => Respond(HttpStatusCode.OK, body)));
        var client = new MetaClient(http);

        MetaClient.SegmentationResponseResult result = await client.SegmentConceptAsync(Segmentation());

        Assert.Equal(string.Empty, result.OutputText);
    }

    [Fact]
    public async Task Responses_transport_failure_is_wrapped_with_the_transport_message()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("connection reset")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("connection reset", ex.Message, StringComparison.Ordinal);
    }
}
