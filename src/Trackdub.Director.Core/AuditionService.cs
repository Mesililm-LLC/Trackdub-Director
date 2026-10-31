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
///
/// When the QA candidates produce no winning take and budget remains, the lane
/// requests up to <see cref="MaxAlternateVariants"/> alternate phrasings of the
/// segment's best meaning-passing text (template translate-variants-v1); every
/// alternate is meaning-gated before synthesis exactly like existing takes. As a
/// final option, the best meaning-passing text is re-synthesized once at the
/// provider speed setting <see cref="SpeedVariantFactor"/> and measured like any
/// other take. Synthesis stops at <see cref="MaxSynthesizedTakesPerSegment"/>
/// takes per segment all-in. A segment with no passing take after all of this is
/// reported uncast; forcing a winner is a bug.
/// </summary>
public sealed class AuditionService
{
    public const int MaxTakesPerSegment = 3;
    public const int MaxSynthesizedTakesPerSegment = 5;
    public const int MaxAlternateVariants = 2;
    public const double SpeedVariantFactor = 1.15;
    public const string VariantsPromptTemplateVersion = "translate-variants-v1";
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
        ValidateSegments(segments);

        // Fail closed before any network call: a missing TTS key means zero
        // calls to every lane.
        _tts.EnsureConfigured();

        // The worst case per segment must fit the run budget, or the run would stop before its first segment.
        if (WorstCaseCallsPerSegment() > MaxMetaCalls)
        {
            throw new ArgumentException($"QA_MAX_ATTEMPTS is too high: audition needs at most {MaxMetaCalls} Meta calls per run.", nameof(segments));
        }

        var budget = new CallBudget(MaxMetaCalls);
        var run = new RunState(budget, _client.WithBudget(budget), sourceLanguage, targetLanguage);
        AuditionCastVoice[] cast = await CastAsync(cancellationToken).ConfigureAwait(false);
        var castBySpeaker = cast.ToDictionary(c => c.SpeakerId, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, QaSegmentReport> qaById = await GenerateCandidatesAsync(segments, run, cancellationToken).ConfigureAwait(false);

        var segmentReports = new List<AuditionSegmentReport>(segments.Count);
        foreach (AuditionSegmentInput segment in segments)
        {
            AuditionCastVoice? castVoice = ResolveCastVoice(segment, castBySpeaker);
            segmentReports.Add(qaById.TryGetValue(segment.Id, out QaSegmentReport? qa)
                ? await ProcessSegmentAsync(new SegmentCandidates(segment, qa, castVoice), run, cancellationToken).ConfigureAwait(false)
                : SkippedSegmentReport(segment, castVoice, run));
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

    private static void ValidateSegments(IReadOnlyList<AuditionSegmentInput> segments)
    {
        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        if (segments.Any(IsIncomplete))
        {
            throw new ArgumentException("Each segment must have a non-empty 'id' and 'text'.", nameof(segments));
        }

        if (segments.Select(s => s.Id.Trim()).Distinct(StringComparer.Ordinal).Count() != segments.Count)
        {
            throw new ArgumentException("Duplicate segment ids are not allowed.", nameof(segments));
        }
    }

    private static bool IsIncomplete(AuditionSegmentInput segment)
    {
        return segment is null || string.IsNullOrWhiteSpace(segment.Id) || string.IsNullOrWhiteSpace(segment.Text);
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
    // that QA left unevaluated, plus one variants call and a meaning check per alternate.
    // The speed variant reuses an already-passing text and costs no Meta call.
    private int WorstCaseCallsPerSegment() => 2 * (_options.MaxAttempts + 1) + MaxTakesPerSegment + 1 + MaxAlternateVariants;

    private async Task<Dictionary<string, QaSegmentReport>> GenerateCandidatesAsync(
        IReadOnlyList<AuditionSegmentInput> segments,
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
                qa = await RunQaForSegmentAsync(segment, run, cancellationToken).ConfigureAwait(false);
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

    private async Task<QaSegmentReport> RunQaForSegmentAsync(
        AuditionSegmentInput segment,
        RunState run,
        CancellationToken cancellationToken)
    {
        QaReport report = await new QaService(run.Client, _options)
            .RunAsync([new QaSegmentInput(segment.Id, segment.Text, segment.DurationSeconds)], run.SourceLanguage, run.TargetLanguage, cancellationToken)
            .ConfigureAwait(false);
        return report.Segments.Single();
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
        AuditionCastVoice? castVoice,
        RunState run)
    {
        double windowSeconds = QaService.ResolveWindowSeconds(
            new QaSegmentInput(segment.Id, segment.Text, segment.DurationSeconds), _options, run.SourceLanguage);
        return new AuditionSegmentReport(
            segment.Id,
            segment.Text,
            NullIfBlank(segment.SpeakerId),
            castVoice,
            windowSeconds,
            [],
            Cast: false,
            WinnerTakeNumber: null,
            WinnerFileName: null,
            Note: run.MetaCapHit ? "Not checked: the Meta call budget ran out before this segment's QA." : null);
    }

    private async Task<AuditionSegmentReport> ProcessSegmentAsync(
        SegmentCandidates work,
        RunState run,
        CancellationToken cancellationToken)
    {
        QaAttemptReport[] candidates = SelectCandidates(work.Qa);
        var takes = new List<TakeWork>(candidates.Length);

        foreach (QaAttemptReport attempt in candidates)
        {
            takes.Add(await ResolveMeaningAsync(work.Segment, attempt, run, cancellationToken).ConfigureAwait(false));
        }

        foreach (TakeWork take in takes)
        {
            take.Cast = work.Cast;
            await SynthesizeAsync(take, run, cancellationToken).ConfigureAwait(false);
        }

        TakeWork? winner = PickWinner(takes, work.Qa.WindowSeconds);
        string? note = null;
        if (winner is null)
        {
            note = await AddAlternatesAsync(work, takes, run, cancellationToken).ConfigureAwait(false);
            winner = PickWinner(takes, work.Qa.WindowSeconds);
        }

        if (winner is null)
        {
            string? speedNote = await AddSpeedVariantAsync(work, takes, run, cancellationToken).ConfigureAwait(false);
            note ??= speedNote;
            winner = PickWinner(takes, work.Qa.WindowSeconds);
        }

        AuditionTakeReport[] reports = takes.Select(t => t.ToReport(winner, work.Segment.Id)).ToArray();
        return new AuditionSegmentReport(
            work.Segment.Id,
            work.Segment.Text,
            NullIfBlank(work.Segment.SpeakerId),
            work.Cast,
            work.Qa.WindowSeconds,
            reports,
            Cast: winner is not null,
            WinnerTakeNumber: winner?.TakeNumber,
            WinnerFileName: winner?.FileName(work.Segment.Id),
            Note: note);
    }

    // The best meaning-passing take: measured timing closest to the window wins;
    // an unsynthesized take sorts last. Null when no take has passed the meaning gate,
    // in which case there is no trustworthy text to vary or speed up.
    private static TakeWork? BestMeaningPassingTake(IReadOnlyList<TakeWork> takes, double windowSeconds)
    {
        return takes
            .Where(t => t.MeaningVerdict == MeaningVerdict.Pass)
            .OrderBy(t => t.MeasuredSeconds is null ? double.MaxValue : Math.Abs(t.MeasuredSeconds.Value - windowSeconds))
            .FirstOrDefault();
    }

    private static bool SegmentSynthFull(IReadOnlyList<TakeWork> takes)
    {
        return takes.Count(t => t.Synthesized) >= MaxSynthesizedTakesPerSegment;
    }

    // Requests up to MaxAlternateVariants phrasings of the segment's best text when no
    // candidate won. Every alternate is meaning-gated through the same path as QA
    // candidates before any synthesis. Returns a segment note when alternates could
    // not be produced; null when they were (or were not needed).
    private async Task<string?> AddAlternatesAsync(
        SegmentCandidates work,
        List<TakeWork> takes,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (work.Cast is null)
        {
            return "No cast voice for this speaker; alternates not requested.";
        }

        TakeWork? best = BestMeaningPassingTake(takes, work.Qa.WindowSeconds);
        if (best is null)
        {
            return "No meaning-passing text to base alternates on.";
        }

        if (SegmentSynthFull(takes))
        {
            return $"Per-segment take cap ({MaxSynthesizedTakesPerSegment}) reached; no alternates requested.";
        }

        MetaClient.VariantsCompletionResult variants;
        try
        {
            variants = await run.Client.TranslateVariantsAsync(
                new MetaClient.VariantsRequest(work.Segment.Text, best.Attempt.Translation, run.SourceLanguage, run.TargetLanguage, MaxAlternateVariants),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MetaBudgetExhaustedException)
        {
            // Refused before sending: not counted, reported as a cap hit.
            run.MetaCapHit = true;
            return "Meta call budget ran out before the alternates request.";
        }
        catch (Exception ex) when (
            ex is MetaApiException or HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The request was reserved and sent before it failed, so it counts.
            run.MetaCalls++;
            return $"Alternates request failed: {Truncate(ex.Message)}";
        }

        // Counted only once the call was sent.
        run.MetaCalls++;
        int variantsTokens = variants.Usage?.TotalTokens ?? 0;
        run.TranslationTokens += variantsTokens;

        HashSet<string> existingTexts = takes.Select(t => t.Attempt.Translation.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int nextNumber = takes.Max(t => t.TakeNumber) + 1;
        bool tokensAttributed = false;
        foreach (string text in variants.Variants)
        {
            if (!existingTexts.Add(text.Trim()))
            {
                continue;
            }

            if (SegmentSynthFull(takes))
            {
                break;
            }

            // The single variants call's tokens ride on the first alternate take created.
            int tokenAllocation = tokensAttributed ? 0 : variantsTokens;
            QaAttemptReport attempt = SyntheticAttempt(work, run, nextNumber++, QaAttemptKind.Alternate, text, tokenAllocation);
            tokensAttributed = true;
            TakeWork take = await ResolveMeaningAsync(work.Segment, attempt, run, cancellationToken).ConfigureAwait(false);
            take.Cast = work.Cast;
            await SynthesizeAsync(take, run, cancellationToken).ConfigureAwait(false);
            takes.Add(take);
        }

        if (!tokensAttributed && variantsTokens > 0)
        {
            return $"Alternates request returned only duplicate texts; its {variantsTokens} tokens are counted at run level only.";
        }

        return null;
    }

    // Final option per segment: the best meaning-passing text re-synthesized once at
    // SpeedVariantFactor. No new Meta call (the text already passed the gate); the
    // duration is measured from the returned audio like any other take.
    private async Task<string?> AddSpeedVariantAsync(
        SegmentCandidates work,
        List<TakeWork> takes,
        RunState run,
        CancellationToken cancellationToken)
    {
        TakeWork? best = BestMeaningPassingTake(takes, work.Qa.WindowSeconds);
        if (best is null || !best.Synthesized)
        {
            return null;
        }

        int nextNumber = takes.Max(t => t.TakeNumber) + 1;
        QaAttemptReport attempt = SyntheticAttempt(work, run, nextNumber, QaAttemptKind.SpeedVariant, best.Attempt.Translation, 0);
        var take = new TakeWork(attempt)
        {
            Cast = work.Cast,
        };
        // The text passed the gate already; carry the verdict with zero new tokens.
        take.ApplyMeaning(MeaningVerdict.Pass, best.MeaningIssues, best.MeaningExplanation, 0);
        if (SegmentSynthFull(takes))
        {
            take.Note = $"Per-segment take cap ({MaxSynthesizedTakesPerSegment}) reached; speed variant not synthesized.";
            takes.Add(take);
            return take.Note;
        }

        // Labeled only once the take will actually be synthesized at the factor.
        take.SpeedFactor = SpeedVariantFactor;
        await SynthesizeAsync(take, run, cancellationToken, SpeedVariantFactor).ConfigureAwait(false);
        takes.Add(take);
        return null;
    }

    // A take-shaped attempt for a text that did not come out of the QA loop
    // (alternate phrasing or speed variant). Estimated seconds and max chars are
    // computed with the same deterministic estimator QA uses; verdicts start
    // unevaluated so the meaning gate runs before any synthesis decision.
    private QaAttemptReport SyntheticAttempt(
        SegmentCandidates work,
        RunState run,
        int attemptNumber,
        QaAttemptKind kind,
        string text,
        int translationTokens)
    {
        double cps = _options.GetCharactersPerSecond(run.TargetLanguage);
        double window = work.Qa.WindowSeconds;
        return new QaAttemptReport(
            attemptNumber,
            kind,
            text,
            DurationEstimator.EstimateSeconds(text, cps),
            window,
            DurationEstimator.MaxCharsForWindow(window * (1.0 + _options.ToleranceFraction), cps),
            QaVerdict.Fail,
            translationTokens,
            0,
            translationTokens,
            MetaAuth.GetModel(),
            TimingVerdict: QaVerdict.Fail,
            MeaningVerdict: MeaningVerdict.NotEvaluated);
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
        RunState run,
        CancellationToken cancellationToken)
    {
        var work = new TakeWork(attempt);
        if (attempt.MeaningVerdict != MeaningVerdict.NotEvaluated)
        {
            work.ApplyMeaning(attempt.MeaningVerdict, attempt.MeaningIssues, attempt.MeaningExplanation, attempt.MeaningTotalTokens);
            return work;
        }

        // QA deliberately skipped this text (estimated timing failed). The
        // audition gate still needs a verdict before any synthesis decision.
        // Only the budget decides whether the check runs; a refused call returns null below.
        MeaningCheckResult? meaning = await CheckMeaningAsync(segment, attempt, run, cancellationToken).ConfigureAwait(false);
        if (meaning is null)
        {
            return work;
        }

        // Counted only once the call was sent: a budget refusal returned null above.
        run.MetaCalls++;
        int tokens = meaning.Usage?.TotalTokens ?? 0;
        run.MeaningTokens += tokens;
        work.ApplyMeaning(meaning.Verdict, meaning.Issues, meaning.Explanation, tokens);
        return work;
    }

    // Null when the run's budget is spent: the take then stays NotEvaluated.
    private async Task<MeaningCheckResult?> CheckMeaningAsync(
        AuditionSegmentInput segment,
        QaAttemptReport attempt,
        RunState run,
        CancellationToken cancellationToken)
    {
        try
        {
            return await run.Client.CheckMeaningAsync(
                new MetaClient.MeaningCheckRequest(segment.Text, attempt.Translation, run.SourceLanguage, run.TargetLanguage),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MetaBudgetExhaustedException)
        {
            // The budget refused the call before it was sent, so it is not counted.
            run.MetaCapHit = true;
            return null;
        }
        catch (Exception ex) when (
            ex is MetaApiException or HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return MeaningGate.ForError(ex.Message, attempt.ModelId, null, GetEndpointHost());
        }
    }

    private async Task SynthesizeAsync(TakeWork take, RunState run, CancellationToken cancellationToken, double? speed = null)
    {
        string? skipReason = SkipReason(take, run);
        if (skipReason is not null)
        {
            take.Note = skipReason;
            return;
        }

        AuditionCastVoice voice = take.Cast!;
        TtsSynthesisResult result;
        try
        {
            result = await _tts.SynthesizeAsync(voice.VoiceId, take.Attempt.Translation, run.TargetLanguage, speed, cancellationToken).ConfigureAwait(false);
        }
        catch (TtsApiException ex) when (!ex.IsAuthFailure)
        {
            take.Note = $"Synthesis failed: {Truncate(ex.Message)}";
            return;
        }
        catch (HttpRequestException ex)
        {
            take.Note = $"Synthesis failed: {Truncate(ex.Message)}";
            return;
        }

        RecordSynthesis(take, result, run);
    }

    // The reason a take is not synthesized, or null when it should be. Hitting the
    // character cap also stops synthesis for the rest of the run.
    private string? SkipReason(TakeWork take, RunState run)
    {
        if (take.MeaningVerdict != MeaningVerdict.Pass)
        {
            // NotEvaluated with the Meta cap hit means the budget ran out before this take's meaning check.
            return run.MetaCapHit && take.MeaningVerdict == MeaningVerdict.NotEvaluated
                ? "Meta call budget ran out before the meaning check; not synthesized."
                : "Meaning gate did not pass; not synthesized.";
        }

        if (take.Cast is null)
        {
            return "No cast voice for this speaker; not synthesized.";
        }

        // Only the ElevenLabs cap stops synthesis. The Meta cap limits new meaning checks, so takes that already
        // passed the gate must still be synthesized.
        if (run.ElevenLabsCapHit)
        {
            return "ElevenLabs cap already hit; synthesis stopped.";
        }

        if (run.ElevenLabsCharacters + take.Attempt.Translation.Length > MaxElevenLabsCharacters)
        {
            run.ElevenLabsCapHit = true;
            return $"ElevenLabs character cap ({MaxElevenLabsCharacters}) would be exceeded; synthesis stopped for the run.";
        }

        return null;
    }

    private PcmFormat TtsFormat => new()
    {
        SampleRate = _tts.PcmSampleRate,
        Channels = _tts.PcmChannels,
        BitsPerSample = _tts.PcmBitsPerSample,
    };

    private void RecordSynthesis(TakeWork take, TtsSynthesisResult result, RunState run)
    {
        take.Synthesized = true;
        take.Characters = result.CharactersBilled;
        run.ElevenLabsCharacters += result.CharactersBilled;
        run.TakesSynthesized++;
        take.MeasuredSeconds = AudioDuration.Seconds(result.AudioBytes, TtsFormat);
        take.AudioBytes = AudioDuration.ToWav(result.AudioBytes, TtsFormat);
        take.TimingVerdict = DurationEstimator.Fits(take.MeasuredSeconds.Value, take.WindowSeconds, _options.ToleranceFraction)
            ? AuditionTimingVerdict.Pass
            : AuditionTimingVerdict.Fail;
    }

    private static TakeWork? PickWinner(IReadOnlyList<TakeWork> takes, double windowSeconds)
    {
        TakeWork? best = null;
        double bestError = double.MaxValue;
        foreach (TakeWork take in takes.Where(IsWinnable))
        {
            double error = Math.Abs(take.MeasuredSeconds!.Value - windowSeconds);
            if (error < bestError)
            {
                bestError = error;
                best = take;
            }
        }

        return best;
    }

    private static bool IsWinnable(TakeWork take)
    {
        return take.MeaningVerdict == MeaningVerdict.Pass
            && take.TimingVerdict == AuditionTimingVerdict.Pass
            && take.MeasuredSeconds is not null;
    }

    /// <summary>
    /// File name for a winning take. Segment ids come from the request, so the stem keeps only
    /// ASCII letters, digits, '-' and '_'. An id that had to change, or that is too long for the
    /// stem, gets a hash suffix after a '.', which a clean id can never contain. A hashed name
    /// therefore never equals a clean one, and the 64-bit hash keeps distinct ids apart.
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
            stem = $"{stem}.{Convert.ToHexString(digest, 0, 8).ToLowerInvariant()}";
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

    // One segment's QA result, with the cast voice resolved for its speaker.
    private sealed record SegmentCandidates(AuditionSegmentInput Segment, QaSegmentReport Qa, AuditionCastVoice? Cast);

    private sealed class RunState
    {
        public RunState(CallBudget budget, MetaClient client, string sourceLanguage, string targetLanguage)
        {
            Budget = budget;
            Client = client;
            SourceLanguage = sourceLanguage;
            TargetLanguage = targetLanguage;
        }

        public CallBudget Budget { get; }

        // Meta client that charges every request in this run to Budget.
        public MetaClient Client { get; }

        public string SourceLanguage { get; }

        public string TargetLanguage { get; }

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

        public double WindowSeconds => Attempt.WindowSeconds;

        public AuditionCastVoice? Cast { get; set; }

        public MeaningVerdict MeaningVerdict { get; private set; } = MeaningVerdict.NotEvaluated;

        public string[]? MeaningIssues { get; private set; }

        public string? MeaningExplanation { get; private set; }

        public int MeaningTokens { get; private set; }

        public bool Synthesized { get; set; }

        public double? MeasuredSeconds { get; set; }

        public AuditionTimingVerdict TimingVerdict { get; set; } = AuditionTimingVerdict.NotSynthesized;

        public int Characters { get; set; }

        public string? Note { get; set; }

        public byte[]? AudioBytes { get; set; }

        public double? SpeedFactor { get; set; }

        public void ApplyMeaning(MeaningVerdict verdict, string[]? issues, string? explanation, int tokens)
        {
            MeaningVerdict = verdict;
            MeaningIssues = issues;
            MeaningExplanation = explanation;
            MeaningTokens = tokens;
        }

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
                SpeedFactor: SpeedFactor,
                AudioBytes: AudioBytes);
        }
    }
}
