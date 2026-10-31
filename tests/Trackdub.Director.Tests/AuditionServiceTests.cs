using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class AuditionServiceTests
{
    private const string PassJson = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";

    private static string FailJson(string issue) => $$"""{"verdict":"fail","issues":["{{issue}}"],"explanation":"Candidate omits material meaning."}""";

    private sealed class ChatReply
    {
        public required string Content { get; init; }

        public int PromptTokens { get; init; } = 10;

        public int CompletionTokens { get; init; } = 5;
    }

    private static HttpResponseMessage ChatResponse(ChatReply reply)
    {
        var payload = new
        {
            id = $"chatcmpl-test-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = "muse-spark-1.1",
            choices = new[]
            {
                new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = reply.Content } },
            },
            usage = new
            {
                prompt_tokens = reply.PromptTokens,
                completion_tokens = reply.CompletionTokens,
                total_tokens = reply.PromptTokens + reply.CompletionTokens,
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

    private sealed class EnvVar
    {
        public required string Key { get; init; }

        public string? Value { get; init; }
    }

    private static EnvVar MetaKey => new() { Key = MetaAuth.PrimaryApiKeyEnv, Value = "LLM_test-fake-key" };

    private static EnvVar NoElevenLabsKey => new() { Key = ElevenLabsAuth.ApiKeyEnv, Value = "" };

    private static IDisposable EnvShim(EnvVar variable)
    {
        string? previous = Environment.GetEnvironmentVariable(variable.Key);
        Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        return new Restore(new EnvVar { Key = variable.Key, Value = previous });
    }

    private sealed class Restore(EnvVar snapshot) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(snapshot.Key, snapshot.Value);
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

        public PcmFormat Format => new()
        {
            SampleRate = PcmSampleRate,
            Channels = PcmChannels,
            BitsPerSample = PcmBitsPerSample,
        };

        public void EnsureConfigured()
        {
        }

        public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TtsVoice>>(Voices.ToArray());
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(string voiceId, string text, string languageCode, CancellationToken cancellationToken = default)
        {
            SynthCalls.Add((voiceId, text));
            double seconds = _durations.Count > 0 ? _durations.Dequeue() : 1.0;
            int frames = (int)Math.Round(seconds * PcmSampleRate);
            byte[] pcm = new byte[frames * 2];
            byte[] wav = AudioDuration.ToWav(pcm, Format);
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

                    return ChatResponse(new ChatReply
                    {
                        Content = MeaningJsons.Dequeue(),
                        PromptTokens = 7,
                        CompletionTokens = 4,
                    });
                }

                if (Translations.Count == 0)
                {
                    throw new InvalidOperationException("No more canned translations queued.");
                }

                return ChatResponse(new ChatReply { Content = Translations.Dequeue() });
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
        using var shim = EnvShim(MetaKey);
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

        AuditionTakeReport failed = line.Takes[0];
        Assert.Equal(MeaningVerdict.Fail, failed.MeaningVerdict);
        Assert.False(failed.Synthesized);

        AuditionTakeReport passed = line.Takes[1];
        Assert.Equal(MeaningVerdict.Pass, passed.MeaningVerdict);
        Assert.True(passed.Synthesized);

        List<(string VoiceId, string Text)> calls = tts.SynthCalls;
        Assert.Single(calls);
        Assert.Equal("Necesitamos una toma mas.", calls[0].Text);
        Assert.Equal("voice-alba", calls[0].VoiceId);

        Assert.True(line.Cast);
    }

    private sealed class SeedText
    {
        public required string Value { get; init; }

        public required int Length { get; init; }
    }

    private static string Long(SeedText seed)
    {
        // Overlong for the QA estimator (window 4.0s allows 66 chars), so QA
        // revision loop produces several candidates with meaning NotEvaluated.
        var sb = new StringBuilder(seed.Value);
        while (sb.Length < seed.Length)
        {
            sb.Append(seed.Value);
        }

        return sb.ToString()[..seed.Length];
    }

    private enum MeaningPasses
    {
        Three = 3,
    }

    private static MetaFake OverlongMeta(MeaningPasses passes)
    {
        var meta = new MetaFake();
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero uno que sigue. ", Length = 100 }));
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero dos que sigue. ", Length = 100 }));
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero tres que sigue. ", Length = 100 }));
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero cuatro que sigue. ", Length = 100 }));
        for (int i = 0; i < (int)passes; i++)
        {
            meta.MeaningJsons.Enqueue(PassJson);
        }

        return meta;
    }

    [Fact]
    public async Task BestFit_take_wins_among_passing_takes()
    {
        using var shim = EnvShim(MetaKey);
        MetaFake meta = OverlongMeta(MeaningPasses.Three);
        // Window 4.0 (allowed 4.4): 5.0 fails timing; 4.3 passes (err 0.3); 4.05 passes (err 0.05) and wins.
        var tts = new FakeTts([5.0, 4.3, 4.05]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "ashley")], "en", "es");

        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.Equal(3, line.Takes.Length);
        Assert.Equal(3, tts.SynthCalls.Count);

        AuditionTakeReport[] takes = line.Takes;
        Assert.Equal(AuditionTimingVerdict.Fail, takes[0].TimingVerdict);
        Assert.Equal(AuditionTimingVerdict.Pass, takes[1].TimingVerdict);
        Assert.Equal(AuditionTimingVerdict.Pass, takes[2].TimingVerdict);

        AuditionTakeReport winner = takes[2];
        Assert.Equal(3, line.WinnerTakeNumber);
        Assert.Equal("seg-1-take3.wav", line.WinnerFileName);
        Assert.True(winner.IsWinner);

        AuditionTakeReport runnerUp = takes[1];
        Assert.False(runnerUp.IsWinner);
        Assert.True(line.Cast);
        Assert.Equal(1, report.Totals.LinesCast);

        AuditionTotals totals = report.Totals;
        Assert.Equal(3, totals.TakesSynthesized);
        Assert.Equal(7, totals.MetaCalls);
    }

    [Fact]
    public async Task NoWinning_take_reports_line_uncast_with_verdicts_preserved()
    {
        using var shim = EnvShim(MetaKey);
        MetaFake meta = OverlongMeta(MeaningPasses.Three);
        var tts = new FakeTts([6.0, 6.0, 6.0]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "anna")], "en", "es");

        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.False(line.Cast);
        Assert.Null(line.WinnerTakeNumber);
        Assert.Null(line.WinnerFileName);

        AuditionTotals totals = report.Totals;
        Assert.Equal(0, totals.LinesCast);
        Assert.All(line.Takes, t =>
        {
            Assert.Equal(MeaningVerdict.Pass, t.MeaningVerdict);
            Assert.True(t.Synthesized);
            AuditionTimingVerdict timing = t.TimingVerdict;
            Assert.Equal(AuditionTimingVerdict.Fail, timing);
            Assert.False(t.IsWinner);
        });
    }

    [Fact]
    public async Task Character_cap_stops_synthesis_for_the_run()
    {
        using var shim = EnvShim(MetaKey);
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
        AuditionTotals totals = report.Totals;
        Assert.True(totals.ElevenLabsCapHit);
        Assert.False(totals.MetaCapHit);
        Assert.Equal(3000, totals.ElevenLabsCharacters);

        int synthesized = totals.TakesSynthesized;
        Assert.Equal(5, synthesized);
        Assert.Equal(5, tts.SynthCalls.Count);

        AuditionSegmentReport last = report.Segments[^1];
        AuditionTakeReport lastTake = last.Takes[0];
        Assert.False(lastTake.Synthesized);
        Assert.Contains("cap", lastTake.SynthesisNote ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(last.Cast);

        Assert.Equal(5, totals.LinesCast);
    }

    [Fact]
    public async Task MissingApiKey_fails_closed_with_zero_calls()
    {
        using var metaShim = EnvShim(MetaKey);
        using var elevenLabsShim = EnvShim(NoElevenLabsKey);
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
