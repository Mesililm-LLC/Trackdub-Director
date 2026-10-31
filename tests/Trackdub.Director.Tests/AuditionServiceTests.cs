using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class AuditionServiceTests
{
    private const string PassJson = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";

    private static string FailJson(string issue) => $$"""{"verdict":"fail","issues":["{{issue}}"],"explanation":"Candidate omits material meaning."}""";

    private static HttpResponseMessage ChatResponse(string content, int promptTokens = 10, int completionTokens = 5)
    {
        var payload = new
        {
            id = $"chatcmpl-test-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = "muse-spark-1.1",
            choices = new[]
            {
                new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } },
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
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

    /// <summary>Fake TTS: deterministic WAV bytes of queued durations; records every call.</summary>
    private sealed class FakeTts : ITtsProvider
    {
        private readonly Queue<double> _durations;

        public FakeTts(IEnumerable<double> durations, IEnumerable<TtsVoice>? voices = null)
        {
            _durations = new Queue<double>(durations);
            Voices = voices?.ToList() ?? [.. DefaultVoices()];
        }

        public List<TtsVoice> Voices { get; } = [];

        public List<(string VoiceId, string Text)> SynthCalls { get; } = [];

        public int PcmSampleRate => 16000;

        public int PcmChannels => 1;

        public int PcmBitsPerSample => 16;

        public void EnsureConfigured()
        {
        }

        public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TtsVoice>>(Voices.ToArray());
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(string voiceId, string text, CancellationToken cancellationToken = default)
        {
            SynthCalls.Add((voiceId, text));
            double seconds = _durations.Count > 0 ? _durations.Dequeue() : 1.0;
            int frames = (int)Math.Round(seconds * PcmSampleRate);
            byte[] pcm = new byte[frames * 2];
            byte[] wav = AudioDuration.ToWav(pcm, PcmSampleRate, PcmChannels, PcmBitsPerSample);
            return Task.FromResult(new TtsSynthesisResult(wav, text.Length));
        }

        public static TtsVoice[] DefaultVoices()
        {
            return
            [
                new TtsVoice("male-1", "Adam", "premade", "male", ["eleven_multilingual_v2"]),
                new TtsVoice("cloned-1", "Clone Carla", "cloned", "female", ["eleven_multilingual_v2"]),
                new TtsVoice("turbo-1", "Turbo Ana", "premade", "female", ["eleven_turbo_v2"]),
                new TtsVoice("voice-zoe", "Zoe", "premade", "female", ["eleven_multilingual_v2"]),
                new TtsVoice("voice-alba", "Alba", "premade", "female", ["eleven_multilingual_v2"]),
            ];
        }
    }

    /// <summary>Counting fake Meta handler: translations and meaning JSONs from queues.</summary>
    private sealed class MetaFake
    {
        public Queue<string> Translations = new();
        public Queue<string> MeaningJsons = new();
        public int Calls;

        public MetaClient BuildClient()
        {
            var handler = new DelegatingHandlerStub(async (request, cancellationToken) =>
            {
                Calls++;
                string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
                {
                    if (MeaningJsons.Count == 0)
                    {
                        throw new InvalidOperationException("No more canned meaning JSON queued.");
                    }

                    return ChatResponse(MeaningJsons.Dequeue(), promptTokens: 7, completionTokens: 4);
                }

                if (Translations.Count == 0)
                {
                    throw new InvalidOperationException("No more canned translations queued.");
                }

                return ChatResponse(Translations.Dequeue());
            });
            return new MetaClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) });
        }
    }

    private static AuditionService BuildService(MetaFake meta, FakeTts tts)
    {
        return new AuditionService(meta.BuildClient(), tts, QaOptions.Default);
    }

    [Fact]
    public async Task MeaningFail_texts_are_never_synthesized()
    {
        using var shim = EnvShim(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
        var meta = new MetaFake();
        meta.Translations.Enqueue("Una toma mas.");
        meta.Translations.Enqueue("Necesitamos una toma mas.");
        meta.MeaningJsons.Enqueue(FailJson("dropped subject Necesitamos"));
        meta.MeaningJsons.Enqueue(PassJson);
        var tts = new FakeTts([1.0]);

        AuditionService service = BuildService(meta, tts);
        AuditionReport report = await service.RunAsync(
            [new AuditionSegmentInput("seg-1", "We need one more take.", 2.0, "anna")], "en", "es");

        // Casting picks the two eligible stock voices in name order.
        Assert.Equal("Alba", report.Cast.Single(c => c.SpeakerId == "anna").VoiceName);
        Assert.Equal("Zoe", report.Cast.Single(c => c.SpeakerId == "ashley").VoiceName);

        // Only the meaning-passing revision was synthesized (call counts prove the gate).
        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.Equal(2, line.Takes.Length);
        Assert.Equal(MeaningVerdict.Fail, line.Takes[0].MeaningVerdict);
        Assert.False(line.Takes[0].Synthesized);
        Assert.Equal(MeaningVerdict.Pass, line.Takes[1].MeaningVerdict);
        Assert.True(line.Takes[1].Synthesized);
        Assert.Single(tts.SynthCalls);
        Assert.Equal("Necesitamos una toma mas.", tts.SynthCalls[0].Text);
        Assert.Equal("voice-alba", tts.SynthCalls[0].VoiceId);
        Assert.True(line.Cast);
    }

    private static string Long(string seed, int length)
    {
        // Overlong for the QA estimator (window 4.0s allows 66 chars), so QA
        // revision loop produces several candidates with meaning NotEvaluated.
        var sb = new StringBuilder(seed);
        while (sb.Length < length)
        {
            sb.Append(seed);
        }

        return sb.ToString()[..length];
    }

    private static MetaFake OverlongMeta(int meaningPasses)
    {
        var meta = new MetaFake();
        meta.Translations.Enqueue(Long("Frase larga numero uno que sigue. ", 100));
        meta.Translations.Enqueue(Long("Frase larga numero dos que sigue. ", 100));
        meta.Translations.Enqueue(Long("Frase larga numero tres que sigue. ", 100));
        meta.Translations.Enqueue(Long("Frase larga numero cuatro que sigue. ", 100));
        for (int i = 0; i < meaningPasses; i++)
        {
            meta.MeaningJsons.Enqueue(PassJson);
        }

        return meta;
    }

    [Fact]
    public async Task BestFit_take_wins_among_passing_takes()
    {
        using var shim = EnvShim(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
        MetaFake meta = OverlongMeta(meaningPasses: 3);
        // Window 4.0 (allowed 4.4): 5.0 fails timing; 4.3 passes (err 0.3); 4.05 passes (err 0.05) and wins.
        var tts = new FakeTts([5.0, 4.3, 4.05]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "ashley")], "en", "es");

        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.Equal(3, line.Takes.Length);
        Assert.Equal(3, tts.SynthCalls.Count);
        Assert.Equal(AuditionTimingVerdict.Fail, line.Takes[0].TimingVerdict);
        Assert.Equal(AuditionTimingVerdict.Pass, line.Takes[1].TimingVerdict);
        Assert.Equal(AuditionTimingVerdict.Pass, line.Takes[2].TimingVerdict);
        Assert.Equal(3, line.WinnerTakeNumber);
        Assert.Equal("seg-1-take3.wav", line.WinnerFileName);
        Assert.True(line.Takes[2].IsWinner);
        Assert.False(line.Takes[1].IsWinner);
        Assert.True(line.Cast);
        Assert.Equal(1, report.Totals.LinesCast);
        Assert.Equal(3, report.Totals.TakesSynthesized);
        // QA made 4 translate calls (initial + 3 revisions); audition meaning-checked 3 candidates.
        Assert.Equal(7, report.Totals.MetaCalls);
    }

    [Fact]
    public async Task NoWinning_take_reports_line_uncast_with_verdicts_preserved()
    {
        using var shim = EnvShim(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
        MetaFake meta = OverlongMeta(meaningPasses: 3);
        var tts = new FakeTts([6.0, 6.0, 6.0]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "anna")], "en", "es");

        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.False(line.Cast);
        Assert.Null(line.WinnerTakeNumber);
        Assert.Null(line.WinnerFileName);
        Assert.Equal(0, report.Totals.LinesCast);
        Assert.All(line.Takes, t =>
        {
            Assert.Equal(MeaningVerdict.Pass, t.MeaningVerdict);
            Assert.True(t.Synthesized);
            Assert.Equal(AuditionTimingVerdict.Fail, t.TimingVerdict);
            Assert.False(t.IsWinner);
        });
    }

    [Fact]
    public async Task Character_cap_stops_synthesis_for_the_run()
    {
        using var shim = EnvShim(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
        var meta = new MetaFake();
        var segments = new List<AuditionSegmentInput>();
        for (int i = 1; i <= 6; i++)
        {
            meta.Translations.Enqueue(new string((char)('a' + i), 600));
            meta.MeaningJsons.Enqueue(PassJson);
            segments.Add(new AuditionSegmentInput($"seg-{i}", $"Line number {i}.", 45.0, i % 2 == 1 ? "anna" : "ashley"));
        }
        var tts = new FakeTts([45.0, 45.0, 45.0, 45.0, 45.0, 45.0]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(segments, "en", "es");

        // Five 600-char takes reach exactly 3000; the sixth would exceed it.
        Assert.True(report.Totals.ElevenLabsCapHit);
        Assert.False(report.Totals.MetaCapHit);
        Assert.Equal(3000, report.Totals.ElevenLabsCharacters);
        Assert.Equal(5, report.Totals.TakesSynthesized);
        Assert.Equal(5, tts.SynthCalls.Count);
        AuditionSegmentReport last = report.Segments[^1];
        Assert.False(last.Takes[0].Synthesized);
        Assert.Contains("cap", last.Takes[0].SynthesisNote ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(last.Cast);
        Assert.Equal(5, report.Totals.LinesCast);
    }

    [Fact]
    public async Task MissingApiKey_fails_closed_with_zero_calls()
    {
        using var metaShim = EnvShim(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
        using var elevenLabsShim = EnvShim(ElevenLabsAuth.ApiKeyEnv, "");
        var meta = new MetaFake();
        meta.Translations.Enqueue("Hola.");
        meta.MeaningJsons.Enqueue(PassJson);
        using var provider = new ElevenLabsTtsProvider(new HttpClient(new DelegatingHandlerStub((_, _) =>
            throw new InvalidOperationException("ElevenLabs HTTP must not be called without a key."))));
        var service = new AuditionService(meta.BuildClient(), provider, QaOptions.Default);

        await Assert.ThrowsAsync<TtsApiException>(() =>
            service.RunAsync([new AuditionSegmentInput("seg-1", "Hello.", 1.0, "anna")], "en", "es"));

        Assert.Equal(0, meta.Calls);
    }
}
