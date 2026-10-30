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
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request);
    }

    private sealed record EnvSetting(string Key, string Value);

    private sealed class Restore : IDisposable
    {
        private readonly EnvSetting _setting;
        private readonly string? _previous;

        public Restore(EnvSetting setting, string? previous)
        {
            _setting = setting;
            _previous = previous;
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_setting.Key, _previous);
    }

    private sealed class Composite : IDisposable
    {
        private readonly IDisposable[] _items;

        public Composite(params IDisposable[] items) => _items = items;

        public void Dispose()
        {
            foreach (IDisposable item in _items)
            {
                item.Dispose();
            }
        }
    }

    private static IDisposable EnvShim(EnvSetting setting)
    {
        string? previous = Environment.GetEnvironmentVariable(setting.Key);
        Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        return new Restore(setting, previous);
    }

    private static IDisposable FakeAuth()
    {
        return new Composite(
            EnvShim(new EnvSetting("META_API_KEY", "test-fake-key")),
            EnvShim(new EnvSetting("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1")),
            EnvShim(new EnvSetting("META_SCENE_MODEL", "sam-3.1")));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) => Task.FromResult(Json(status, body));

    private static MetaClient.ChatRequest Chat() => new([new MetaClient.ChatMessage("user", "hello")]);

    private static MetaClient.SegmentationRequest Segmentation() => new(FrameDataUrl, "red rectangle");

    private static string ResponsesBody(string status, string outputJson) =>
        $"{{\"status\":\"{status}\",\"output\":{outputJson}}}";

    [Fact]
    public async Task Chat_http_error_surfaces_the_status_code()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}")));
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
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.OK, body)));
        var client = new MetaClient(http);

        await Assert.ThrowsAsync<MetaApiException>(() => client.ChatCompletionAsync(Chat()));
    }

    [Fact]
    public async Task Responses_http_error_surfaces_the_status_code()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("HTTP 500", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_error_object_in_a_200_response_is_rejected()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.OK, "{\"error\":{\"message\":\"quota exceeded\"}}")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("quota exceeded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_non_completed_status_is_rejected()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.OK, ResponsesBody("failed", "[]"))));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("not completed (status: failed)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_non_object_root_is_rejected()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.OK, "[]")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("Unexpected Meta Model API response shape", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_without_an_output_text_part_is_malformed_not_a_concept_miss()
    {
        using var auth = FakeAuth();
        string body = ResponsesBody("completed", "[{\"type\":\"message\",\"content\":[{\"type\":\"reasoning\",\"text\":\"thinking\"}]}]");
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.OK, body)));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("missing output_text part", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_explicit_empty_output_text_is_a_concept_miss()
    {
        using var auth = FakeAuth();
        string body = ResponsesBody("completed", "[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"\"}]}]");
        using var http = new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.OK, body)));
        var client = new MetaClient(http);

        MetaClient.SegmentationResponseResult result = await client.SegmentConceptAsync(Segmentation());

        Assert.Equal(string.Empty, result.OutputText);
    }

    [Fact]
    public async Task Responses_transport_failure_is_wrapped_with_the_transport_message()
    {
        using var auth = FakeAuth();
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection reset")));
        var client = new MetaClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.SegmentConceptAsync(Segmentation()));

        Assert.Contains("connection reset", ex.Message, StringComparison.Ordinal);
    }
}
