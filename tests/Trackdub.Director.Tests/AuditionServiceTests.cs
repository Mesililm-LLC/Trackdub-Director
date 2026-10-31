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
            byte[] wav = AudioDuration.ToWav(pcm, new PcmFormat(PcmSampleRate, PcmChannels, PcmBitsPerSample));
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
        Assert.Equal(("Alba", "Zoe"), (report.Cast.Single(c => c.SpeakerId == "anna").VoiceName, report.Cast.Single(c => c.SpeakerId == "ashley").VoiceName));

        // Only the meaning-passing revision was synthesized (call counts prove the gate).
        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.Equal(2, line.Takes.Length);
        Assert.Equal((MeaningVerdict.Fail, MeaningVerdict.Pass), (line.Takes[0].MeaningVerdict, line.Takes[1].MeaningVerdict));
        Assert.Equal((false, true), (line.Takes[0].Synthesized, line.Takes[1].Synthesized));
        AssertSingleSynthesis(tts, "Necesitamos una toma mas.", "voice-alba");
        Assert.True(line.Cast);
    }

    private static void AssertSingleSynthesis(FakeTts tts, string text, string voiceId)
    {
        Assert.Single(tts.SynthCalls);
        Assert.Equal((text, voiceId), (tts.SynthCalls[0].Text, tts.SynthCalls[0].VoiceId));
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
        Assert.Equal((3, 3), (line.Takes.Length, tts.SynthCalls.Count));
        Assert.Equal((AuditionTimingVerdict.Fail, AuditionTimingVerdict.Pass, AuditionTimingVerdict.Pass), (line.Takes[0].TimingVerdict, line.Takes[1].TimingVerdict, line.Takes[2].TimingVerdict));

        Assert.Equal((3, "seg-1-take3.wav"), (line.WinnerTakeNumber, line.WinnerFileName));
        Assert.Equal((false, true), (line.Takes[1].IsWinner, line.Takes[2].IsWinner));
        Assert.True(line.Cast);

        var totals = report.Totals;
        Assert.Equal((1, 3), (totals.LinesCast, totals.TakesSynthesized));
        // QA made 4 translate calls (initial + 3 revisions); audition meaning-checked 3 candidates.
        Assert.Equal(7, totals.MetaCalls);
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
        Assert.Equal((false, null, null, 0), (line.Cast, line.WinnerTakeNumber, line.WinnerFileName, report.Totals.LinesCast));
        Assert.All(line.Takes, t => Assert.Equal((MeaningVerdict.Pass, AuditionTimingVerdict.Fail, true, false), (t.MeaningVerdict, t.TimingVerdict, t.Synthesized, t.IsWinner)));
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
        var totals = report.Totals;
        Assert.Equal((true, false, 3000), (totals.ElevenLabsCapHit, totals.MetaCapHit, totals.ElevenLabsCharacters));
        Assert.Equal((5, 5), (totals.TakesSynthesized, tts.SynthCalls.Count));
        Assert.Equal(5, totals.LinesCast);

        AuditionSegmentReport last = report.Segments[^1];
        Assert.Equal((false, false), (last.Takes[0].Synthesized, last.Cast));
        Assert.Contains("cap", last.Takes[0].SynthesisNote ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
