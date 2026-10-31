using System.Security.Cryptography;
using System.Text;

namespace Trackdub.Director.Core;

/// <summary>
/// Audition casting: cast each character's stock voice once, take up to three
/// candidate texts per segment from the existing translation/QA machinery,
/// synthesize only meaning-passing takes, and let the gates pick the winner.
/// A take wins only when meaning Pass AND measured-audio timing Pass; lines
/// with no winning take stay uncast with every verdict preserved.
///
/// Candidate generation runs each segment through <see cref="QaService"/>
/// (translation lane + revision loop, existing config). Every Meta request in
/// the run is charged to one <see cref="CallBudget"/>; a segment is only sent to
/// QA when the budget can cover its worst case, so a batch stops at the cap
/// with the remaining segments reported uncast. Candidates whose meaning was
/// deliberately skipped by QA timing (NotEvaluated) are meaning-checked here
/// before any synthesis, so every synthesized text carries a fresh meaning verdict.
/// </summary>
public sealed class AuditionService
{
    public const int MaxTakesPerSegment = 3;
    public const int MaxElevenLabsCharacters = 3000;
    public const int MaxMetaCalls = 40;

    private const string SpanishCapableModelId = "eleven_multilingual_v2";
    private const int MaxStemLength = 64;

    private readonly MetaClient _client;
    private readonly ITtsProvider _tts;
    private readonly QaOptions _options;

    public AuditionService(MetaClient client, ITtsProvider tts, QaOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _options = options ?? QaOptions.Default;
    }

    public async Task<AuditionReport> RunAsync(
        IReadOnlyList<AuditionSegmentInput> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        if (segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Text is null))
        {
            throw new ArgumentException("Each segment must have a non-empty 'id' and 'text'.", nameof(segments));
        }

        if (segments.Select(s => s.Id.Trim()).Distinct(StringComparer.Ordinal).Count() != segments.Count)
        {
            throw new ArgumentException("Duplicate segment ids are not allowed.", nameof(segments));
        }

        // Fail closed before any network call: a missing TTS key means zero
        // calls to every lane.
        _tts.EnsureConfigured();

        var budget = new CallBudget(MaxMetaCalls);
        var run = new RunState(budget, _client.WithBudget(budget));
        AuditionCastVoice[] cast = await CastAsync(cancellationToken).ConfigureAwait(false);
        var castBySpeaker = cast.ToDictionary(c => c.SpeakerId, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, QaSegmentReport> qaById = await GenerateCandidatesAsync(segments, sourceLanguage, targetLanguage, run, cancellationToken).ConfigureAwait(false);

        var segmentReports = new List<AuditionSegmentReport>(segments.Count);
        foreach (AuditionSegmentInput segment in segments)
        {
            segmentReports.Add(qaById.TryGetValue(segment.Id, out QaSegmentReport? qa)
                ? await ProcessSegmentAsync(segment, qa, castBySpeaker, sourceLanguage, targetLanguage, run, cancellationToken).ConfigureAwait(false)
                : SkippedSegmentReport(segment, castBySpeaker, sourceLanguage));
        }

        var totals = new AuditionTotals(
            TotalSegments: segments.Count,
            LinesCast: segmentReports.Count(s => s.Cast),
            TakesSynthesized: run.TakesSynthesized,
            MetaCalls: run.MetaCalls,
            TranslationTokens: run.TranslationTokens,
            MeaningTokens: run.MeaningTokens,
            ElevenLabsCharacters: run.ElevenLabsCharacters,
            ElevenLabsCapHit: run.ElevenLabsCapHit,
            MetaCapHit: run.MetaCapHit);

        return new AuditionReport(sourceLanguage, targetLanguage, cast, segmentReports.ToArray(), totals);
    }

    private async Task<AuditionCastVoice[]> CastAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TtsVoice> voices = await _tts.ListVoicesAsync(cancellationToken).ConfigureAwait(false);
        var eligible = voices
            .Where(v => string.Equals(v.Category, "premade", StringComparison.OrdinalIgnoreCase))
            .Where(v => string.Equals(v.Gender, "female", StringComparison.OrdinalIgnoreCase))
            .Where(IsSpanishCapable)
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToArray();
        if (eligible.Length < 2)
        {
            throw new TtsApiException(
                $"Casting failed: the account voice list has {eligible.Length} female-presenting Spanish-capable stock voices; two are required (anna, ashley).");
        }

        return
        [
            new AuditionCastVoice("anna", eligible[0].Id, eligible[0].Name),
            new AuditionCastVoice("ashley", eligible[1].Id, eligible[1].Name),
        ];
    }

    private static bool IsSpanishCapable(TtsVoice voice)
    {
        // When the provider advertises per-voice models, require the
        // multilingual model; when it advertises none, the synthesis request's
        // model decides and per-take errors surface honestly instead.
        return voice.ModelIds is not { Length: > 0 } models
            || models.Contains(SpanishCapableModelId, StringComparer.Ordinal);
    }

    // Worst case for one segment: an initial translation and up to MaxAttempts
    // revisions, each with a meaning check, plus a meaning check for every candidate
    // that QA left unevaluated.
    private int WorstCaseCallsPerSegment() => 2 * (_options.MaxAttempts + 1) + MaxTakesPerSegment;

    private async Task<Dictionary<string, QaSegmentReport>> GenerateCandidatesAsync(
        IReadOnlyList<AuditionSegmentInput> segments,
        string sourceLanguage,
        string targetLanguage,
        RunState run,
        CancellationToken cancellationToken)
    {
        int reserve = WorstCaseCallsPerSegment();
        var qaById = new Dictionary<string, QaSegmentReport>(StringComparer.Ordinal);
        foreach (AuditionSegmentInput segment in segments)
        {
            if (run.Budget.Remaining < reserve)
            {
                run.MetaCapHit = true;
                break;
            }

            QaSegmentReport qa;
            try
            {
                QaReport report = await new QaService(run.Client, _options)
                    .RunAsync([new QaSegmentInput(segment.Id, segment.Text, segment.DurationSeconds)], sourceLanguage, targetLanguage, cancellationToken)
                    .ConfigureAwait(false);
                qa = report.Segments.Single();
            }
            catch (MetaBudgetExhaustedException)
            {
                run.MetaCapHit = true;
                break;
            }

            AccountQaCalls(qa, run);
            qaById[segment.Id] = qa;
        }

        return qaById;
    }

    private static void AccountQaCalls(QaSegmentReport segment, RunState run)
    {
        // Account every Meta call QA made: one translate/revise call per
        // attempt plus one meaning call per evaluated attempt.
        foreach (QaAttemptReport attempt in AllAttempts(segment))
        {
            run.MetaCalls++;
            run.TranslationTokens += attempt.TotalTokens;
            run.MeaningTokens += attempt.MeaningTotalTokens;
            if (attempt.MeaningVerdict != MeaningVerdict.NotEvaluated)
            {
                run.MetaCalls++;
            }
        }
    }

    private static IEnumerable<QaAttemptReport> AllAttempts(QaSegmentReport segment)
    {
        yield return segment.FirstAttempt;
        foreach (QaAttemptReport revision in segment.Revisions)
        {
            yield return revision;
        }
    }

    // Keeps at most MaxTakesPerSegment attempts: the first ones, as before. A passing attempt
    // beyond that window replaces the last kept attempt that did not pass, so a passing final
    // revision is never dropped for a failure. Kept attempts keep their original positions.
    private static QaAttemptReport[] SelectCandidates(QaSegmentReport segment)
    {
        List<QaAttemptReport> all = AllAttempts(segment).ToList();
        if (all.Count <= MaxTakesPerSegment)
        {
            return all.ToArray();
        }

        var kept = all.Take(MaxTakesPerSegment).ToList();
        foreach (QaAttemptReport passing in all.Skip(MaxTakesPerSegment).Where(a => a.Verdict == QaVerdict.Pass))
        {
            int slot = kept.FindLastIndex(a => a.Verdict != QaVerdict.Pass);
            if (slot < 0)
            {
                break;
            }

            kept[slot] = passing;
        }

        return kept.ToArray();
    }

    // A segment that never reached QA because the budget ran out. It is reported
    // uncast, with no takes, rather than dropped from the batch.
    private AuditionSegmentReport SkippedSegmentReport(
        AuditionSegmentInput segment,
        IReadOnlyDictionary<string, AuditionCastVoice> castBySpeaker,
        string sourceLanguage)
    {
        double windowSeconds = QaService.ResolveWindowSeconds(
            new QaSegmentInput(segment.Id, segment.Text, segment.DurationSeconds), _options, sourceLanguage);
        return new AuditionSegmentReport(
            segment.Id,
            segment.Text,
            NullIfBlank(segment.SpeakerId),
            ResolveCastVoice(segment, castBySpeaker),
            windowSeconds,
            [],
            Cast: false,
            WinnerTakeNumber: null,
            WinnerFileName: null);
    }

    private async Task<AuditionSegmentReport> ProcessSegmentAsync(
        AuditionSegmentInput segment,
        QaSegmentReport qa,
        IReadOnlyDictionary<string, AuditionCastVoice> castBySpeaker,
        string sourceLanguage,
        string targetLanguage,
        RunState run,
        CancellationToken cancellationToken)
    {
        AuditionCastVoice? castVoice = ResolveCastVoice(segment, castBySpeaker);
        QaAttemptReport[] candidates = SelectCandidates(qa);
        var takes = new List<TakeWork>(candidates.Length);

        foreach (QaAttemptReport attempt in candidates)
        {
            takes.Add(await ResolveMeaningAsync(segment, attempt, sourceLanguage, targetLanguage, run, cancellationToken).ConfigureAwait(false));
        }

        foreach (TakeWork take in takes)
        {
            await SynthesizeAsync(segment, take, castVoice, qa.WindowSeconds, run, cancellationToken).ConfigureAwait(false);
        }

        TakeWork? winner = PickWinner(takes, qa.WindowSeconds);
        AuditionTakeReport[] reports = takes.Select(t => t.ToReport(winner, segment.Id)).ToArray();
        return new AuditionSegmentReport(
            segment.Id,
            segment.Text,
            NullIfBlank(segment.SpeakerId),
            castVoice,
            qa.WindowSeconds,
            reports,
            Cast: winner is not null,
            WinnerTakeNumber: winner?.TakeNumber,
            WinnerFileName: winner?.FileName(segment.Id));
    }

    private static AuditionCastVoice? ResolveCastVoice(
        AuditionSegmentInput segment,
        IReadOnlyDictionary<string, AuditionCastVoice> castBySpeaker)
    {
        string? speaker = NullIfBlank(segment.SpeakerId);
        return speaker is not null && castBySpeaker.TryGetValue(speaker, out AuditionCastVoice? voice) ? voice : null;
    }

    private async Task<TakeWork> ResolveMeaningAsync(
        AuditionSegmentInput segment,
        QaAttemptReport attempt,
        string sourceLanguage,
        string targetLanguage,
        RunState run,
        CancellationToken cancellationToken)
    {
        var work = new TakeWork(attempt);
        if (attempt.MeaningVerdict != MeaningVerdict.NotEvaluated)
        {
            work.MeaningVerdict = attempt.MeaningVerdict;
            work.MeaningIssues = attempt.MeaningIssues;
            work.MeaningExplanation = attempt.MeaningExplanation;
            work.MeaningTokens = attempt.MeaningTotalTokens;
            return work;
        }

        // QA deliberately skipped this text (estimated timing failed). The
        // audition gate still needs a verdict before any synthesis decision.
        if (run.MetaCapHit)
        {
            work.MeaningVerdict = MeaningVerdict.NotEvaluated;
            return work;
        }

        MeaningCheckResult meaning;
        try
        {
            meaning = await run.Client.CheckMeaningAsync(
                new MetaClient.MeaningCheckRequest(segment.Text, attempt.Translation, sourceLanguage, targetLanguage),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MetaBudgetExhaustedException)
        {
            // The budget refused the call before it was sent, so it is not counted.
            run.MetaCapHit = true;
            work.MeaningVerdict = MeaningVerdict.NotEvaluated;
            return work;
        }
        catch (Exception ex) when (
            ex is MetaApiException or HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            meaning = MeaningGate.ForError(ex.Message, attempt.ModelId, null, GetEndpointHost());
        }

        run.MetaCalls++;
        run.MeaningTokens += meaning.Usage?.TotalTokens ?? 0;
        work.MeaningVerdict = meaning.Verdict;
        work.MeaningIssues = meaning.Issues;
        work.MeaningExplanation = meaning.Explanation;
        work.MeaningTokens = meaning.Usage?.TotalTokens ?? 0;
        return work;
    }

    private async Task SynthesizeAsync(
        AuditionSegmentInput segment,
        TakeWork take,
        AuditionCastVoice? castVoice,
        double windowSeconds,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (take.MeaningVerdict != MeaningVerdict.Pass)
        {
            take.Note = "Meaning gate did not pass; not synthesized.";
            return;
        }

        if (castVoice is null)
        {
            take.Note = "No cast voice for this speaker; not synthesized.";
            return;
        }

        if (run.ElevenLabsCapHit || run.MetaCapHit)
        {
            take.Note = "Run cap already hit; synthesis stopped.";
            return;
        }

        if (run.ElevenLabsCharacters + take.Attempt.Translation.Length > MaxElevenLabsCharacters)
        {
            run.ElevenLabsCapHit = true;
            take.Note = $"ElevenLabs character cap ({MaxElevenLabsCharacters}) would be exceeded; synthesis stopped for the run.";
            return;
        }

        TtsSynthesisResult result;
        try
        {
            result = await _tts.SynthesizeAsync(castVoice.VoiceId, take.Attempt.Translation, cancellationToken).ConfigureAwait(false);
        }
        catch (TtsApiException ex)
        {
            take.Note = $"Synthesis failed: {Truncate(ex.Message)}";
            return;
        }
        catch (HttpRequestException ex)
        {
            take.Note = $"Synthesis failed: {Truncate(ex.Message)}";
            return;
        }

        take.Synthesized = true;
        take.Characters = result.CharactersBilled;
        run.ElevenLabsCharacters += result.CharactersBilled;
        run.TakesSynthesized++;
        take.MeasuredSeconds = AudioDuration.Seconds(result.AudioBytes, _tts.PcmSampleRate, _tts.PcmChannels, _tts.PcmBitsPerSample);
        take.AudioBytes = AudioDuration.ToWav(result.AudioBytes, _tts.PcmSampleRate, _tts.PcmChannels, _tts.PcmBitsPerSample);
        take.TimingVerdict = DurationEstimator.Fits(take.MeasuredSeconds.Value, windowSeconds, _options.ToleranceFraction)
            ? AuditionTimingVerdict.Pass
            : AuditionTimingVerdict.Fail;
    }

    private static TakeWork? PickWinner(IReadOnlyList<TakeWork> takes, double windowSeconds)
    {
        TakeWork? best = null;
        double bestError = double.MaxValue;
        foreach (TakeWork take in takes)
        {
            if (take.MeaningVerdict != MeaningVerdict.Pass || take.TimingVerdict != AuditionTimingVerdict.Pass || take.MeasuredSeconds is null)
            {
                continue;
            }

            double error = Math.Abs(take.MeasuredSeconds.Value - windowSeconds);
            if (error < bestError)
            {
                bestError = error;
                best = take;
            }
        }

        return best;
    }

    /// <summary>
    /// File name for a winning take. Segment ids come from the request, so the stem keeps only
    /// ASCII letters, digits, '-' and '_'. An id that had to change, or that is too long for the
    /// stem, gets a short hash suffix, so distinct ids cannot collapse onto one file.
    /// </summary>
    public static string TakeFileName(string segmentId, int takeNumber)
    {
        string sanitized = new(segmentId
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray());
        string stem = new(sanitized.Take(MaxStemLength).ToArray());
        if (sanitized != segmentId || sanitized.Length > MaxStemLength)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
            stem = $"{stem}-{Convert.ToHexString(digest, 0, 4).ToLowerInvariant()}";
        }

        return $"{stem}-take{takeNumber}.wav";
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string GetEndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : baseUrl;
    }

    private static string Truncate(string message)
    {
        return message.Length > 300 ? message[..300] : message;
    }

    private sealed class RunState
    {
        public RunState(CallBudget budget, MetaClient client)
        {
            Budget = budget;
            Client = client;
        }

        public CallBudget Budget { get; }

        // Meta client that charges every request in this run to Budget.
        public MetaClient Client { get; }

        public int MetaCalls;
        public int TranslationTokens;
        public int MeaningTokens;
        public int ElevenLabsCharacters;
        public int TakesSynthesized;
        public bool ElevenLabsCapHit;
        public bool MetaCapHit;
    }

    private sealed class TakeWork
    {
        public TakeWork(QaAttemptReport attempt)
        {
            Attempt = attempt;
        }

        public QaAttemptReport Attempt { get; }

        public int TakeNumber => Attempt.AttemptNumber;

        public MeaningVerdict MeaningVerdict { get; set; } = MeaningVerdict.NotEvaluated;

        public string[]? MeaningIssues { get; set; }

        public string? MeaningExplanation { get; set; }

        public int MeaningTokens { get; set; }

        public bool Synthesized { get; set; }

        public double? MeasuredSeconds { get; set; }

        public AuditionTimingVerdict TimingVerdict { get; set; } = AuditionTimingVerdict.NotSynthesized;

        public int Characters { get; set; }

        public string? Note { get; set; }

        public byte[]? AudioBytes { get; set; }

        public string FileName(string segmentId)
        {
            return TakeFileName(segmentId, TakeNumber);
        }

        public AuditionTakeReport ToReport(TakeWork? winner, string segmentId)
        {
            bool isWinner = ReferenceEquals(this, winner);
            return new AuditionTakeReport(
                TakeNumber,
                Attempt.Kind,
                Attempt.Translation,
                MeaningVerdict,
                MeaningIssues,
                MeaningExplanation,
                Synthesized,
                MeasuredSeconds,
                Attempt.WindowSeconds,
                TimingVerdict,
                isWinner,
                AudioFileName: Synthesized ? FileName(segmentId) : null,
                SynthesisNote: Note,
                TranslationTokens: Attempt.TotalTokens,
                MeaningTokens: MeaningTokens,
                ElevenLabsCharacters: Characters,
                AudioBytes: AudioBytes);
        }
    }
}
