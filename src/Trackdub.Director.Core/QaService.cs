using System.Text.Json;

namespace Trackdub.Director.Core;

public sealed class QaService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly MetaClient _client;
    private readonly QaOptions _options;

    public QaService(MetaClient client, QaOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? QaOptions.Default;
    }

    public QaService(QaOptions? options = null)
        : this(new MetaClient(), options)
    {
    }

    public QaOptions Options => _options;

    /// <summary>
    /// Resolve the timing window for a segment: explicit duration_seconds when present,
    /// otherwise an estimate from the source line's own spoken duration.
    /// </summary>
    public static double ResolveWindowSeconds(QaSegmentInput segment, QaOptions options, string sourceLanguage)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);

        if (segment.DurationSeconds.HasValue)
        {
            if (segment.DurationSeconds.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segment.DurationSeconds), "Duration must be non-negative.");
            }

            return segment.DurationSeconds.Value;
        }

        double cps = options.GetCharactersPerSecond(sourceLanguage);
        return DurationEstimator.EstimateSeconds(segment.Text, cps);
    }

    public async Task<QaReport> RunAsync(
        IReadOnlyList<QaSegmentInput> segments,
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

        string endpointHost = GetEndpointHost();
        var segmentReports = new List<QaSegmentReport>(segments.Count);

        int totalPrompt = 0;
        int totalCompletion = 0;
        int totalTokens = 0;

        foreach (QaSegmentInput segment in segments)
        {
            SegmentContext context = CreateSegmentContext(segment, sourceLanguage, targetLanguage, endpointHost);
            QaSegmentReport report = await ProcessSegmentAsync(context, cancellationToken).ConfigureAwait(false);

            segmentReports.Add(report);

            totalPrompt += report.TokenUsage.PromptTokens;
            totalCompletion += report.TokenUsage.CompletionTokens;
            totalTokens += report.TokenUsage.TotalTokens;
        }

        int firstPass = segmentReports.Count(s => s.FirstAttempt.Verdict == QaVerdict.Pass);
        int finalPass = segmentReports.Count(s => s.FinalVerdict == QaVerdict.Pass);
        int totalRevisions = segmentReports.Sum(s => s.Revisions.Length);

        var totals = new QaTotals(
            segments.Count,
            firstPass,
            finalPass,
            totalRevisions,
            FirstPassRate: segments.Count == 0 ? 0 : (double)firstPass / segments.Count,
            FinalPassRate: segments.Count == 0 ? 0 : (double)finalPass / segments.Count,
            TotalTokens: totalTokens,
            TotalPromptTokens: totalPrompt,
            TotalCompletionTokens: totalCompletion);

        var reportOut = new QaReport(
            SourceLanguage: sourceLanguage,
            TargetLanguage: targetLanguage,
            QaOptions: new QaReportOptions(
                MaxAttempts: _options.MaxAttempts,
                ToleranceFraction: _options.ToleranceFraction,
                TargetCharactersPerSecond: _options.GetCharactersPerSecond(targetLanguage),
                SourceCharactersPerSecond: _options.GetCharactersPerSecond(sourceLanguage),
                ReasoningEffort: _options.ReasoningEffort),
            Segments: segmentReports.ToArray(),
            Totals: totals);

        return reportOut;
    }

    private SegmentContext CreateSegmentContext(
        QaSegmentInput segment,
        string sourceLanguage,
        string targetLanguage,
        string endpointHost)
    {
        double windowSeconds = ResolveWindowSeconds(segment, _options, sourceLanguage);
        double targetCps = _options.GetCharactersPerSecond(targetLanguage);
        int maxChars = DurationEstimator.MaxCharsForWindow(
            windowSeconds * (1.0 + _options.ToleranceFraction), targetCps);
        if (maxChars < 1)
        {
            maxChars = 1;
        }

        return new SegmentContext(segment, sourceLanguage, targetLanguage, endpointHost, windowSeconds, targetCps, maxChars);
    }

    private async Task<QaSegmentReport> ProcessSegmentAsync(
        SegmentContext context,
        CancellationToken cancellationToken)
    {
        // Initial translation
        MetaClient.ChatCompletionResult initial = await _client.TranslateWithUsageAsync(
            new MetaClient.TranslationRequest(context.Segment.Text, context.SourceLanguage, context.TargetLanguage), cancellationToken).ConfigureAwait(false);

        string currentTranslation = initial.Content;
        var tokenAccum = new TokenAccum(initial.Usage);

        double est = DurationEstimator.EstimateSeconds(currentTranslation, context.TargetCharactersPerSecond);
        bool fits = DurationEstimator.Fits(est, context.WindowSeconds, _options.ToleranceFraction);

        var initialAttempt = new AttemptInput(1, QaAttemptKind.Initial, currentTranslation, est,
            context.WindowSeconds, context.MaxChars, fits, initial.Usage, initial.Model);
        QaAttemptReport firstAttempt = await EvaluateAttemptAsync(
            initialAttempt, context, tokenAccum, cancellationToken).ConfigureAwait(false);

        if (firstAttempt.Verdict == QaVerdict.Pass)
        {
            return CreateSegmentReport(context, new SegmentOutcome(
                firstAttempt, [], QaVerdict.Pass, currentTranslation, tokenAccum.ToUsage()));
        }

        // Meaning Error is terminal: do not enter revision loop (does not consume revision attempts)
        if (firstAttempt.MeaningVerdict == MeaningVerdict.Error)
        {
            return CreateSegmentReport(context, new SegmentOutcome(
                firstAttempt, [], QaVerdict.Fail, currentTranslation, tokenAccum.ToUsage()));
        }

        IReadOnlyList<string>? lastMeaningIssues =
            firstAttempt.MeaningVerdict == MeaningVerdict.Fail ? firstAttempt.MeaningIssues : null;
        var revisionState = new RevisionState(currentTranslation, lastMeaningIssues, tokenAccum);
        RevisionResult result = await RunRevisionLoopAsync(context, revisionState, cancellationToken).ConfigureAwait(false);

        return CreateSegmentReport(context, new SegmentOutcome(
            firstAttempt, result.Revisions, result.FinalVerdict, result.Translation, tokenAccum.ToUsage()));
    }

    private async Task<RevisionResult> RunRevisionLoopAsync(
        SegmentContext context,
        RevisionState state,
        CancellationToken cancellationToken)
    {
        var revisions = new List<QaAttemptReport>();
        QaVerdict finalVerdict = QaVerdict.Fail;
        string currentTranslation = state.Translation;
        IReadOnlyList<string>? lastMeaningIssues = state.LastMeaningIssues;

        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            MetaClient.ChatCompletionResult revision = await _client.ReviseTranslationForTimingAsync(
                new MetaClient.TimingRevisionRequest(
                    context.Segment.Text, currentTranslation, context.SourceLanguage, context.TargetLanguage,
                    context.WindowSeconds, context.MaxChars, lastMeaningIssues),
                cancellationToken).ConfigureAwait(false);

            currentTranslation = revision.Content;
            state.Tokens.Add(revision.Usage);

            double revEst = DurationEstimator.EstimateSeconds(currentTranslation, context.TargetCharactersPerSecond);
            bool revFits = DurationEstimator.Fits(revEst, context.WindowSeconds, _options.ToleranceFraction);

            var revisionAttempt = new AttemptInput(1 + attempt, QaAttemptKind.Revision, currentTranslation,
                revEst, context.WindowSeconds, context.MaxChars, revFits, revision.Usage, revision.Model);
            QaAttemptReport revAttempt = await EvaluateAttemptAsync(
                revisionAttempt, context, state.Tokens, cancellationToken).ConfigureAwait(false);

            revisions.Add(revAttempt);

            if (revAttempt.MeaningVerdict == MeaningVerdict.Error)
            {
                finalVerdict = QaVerdict.Fail;
                break;
            }

            if (revAttempt.Verdict == QaVerdict.Pass)
            {
                finalVerdict = QaVerdict.Pass;
                break;
            }

            if (revAttempt.MeaningVerdict == MeaningVerdict.Fail)
            {
                lastMeaningIssues = revAttempt.MeaningIssues;
            }
        }

        return new RevisionResult(revisions.ToArray(), finalVerdict, currentTranslation);
    }

    private static QaSegmentReport CreateSegmentReport(SegmentContext context, SegmentOutcome outcome)
    {
        return new QaSegmentReport(
            context.Segment.Id, context.Segment.Text, context.WindowSeconds,
            outcome.FirstAttempt, outcome.Revisions, outcome.FinalVerdict, outcome.Translation,
            outcome.TokenUsage);
    }

    private async Task<QaAttemptReport> EvaluateAttemptAsync(
        AttemptInput attempt, SegmentContext context, TokenAccum tokenAccum, CancellationToken cancellationToken)
    {
        MeaningCheckResult? meaning = null;
        if (attempt.Fits)
        {
            try
            {
                meaning = await _client.CheckMeaningAsync(
                    new MetaClient.MeaningCheckRequest(
                        context.Segment.Text, attempt.Translation, context.SourceLanguage, context.TargetLanguage),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is MetaApiException or HttpRequestException or InvalidOperationException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                meaning = MeaningGate.ForError(ex.Message, attempt.ModelId, null, context.EndpointHost);
            }

            tokenAccum.Add(meaning.Usage);
        }

        return CreateAttemptReport(attempt, context, meaning);
    }

    private static QaAttemptReport CreateAttemptReport(
        AttemptInput attempt, SegmentContext context, MeaningCheckResult? meaning)
    {
        TokenCounts usage = ToCounts(attempt.Usage);
        TokenCounts meaningUsage = ToCounts(meaning?.Usage);
        return new QaAttemptReport(
            AttemptNumber: attempt.Number,
            Kind: attempt.Kind,
            Translation: attempt.Translation,
            EstimatedSeconds: attempt.EstimatedSeconds,
            WindowSeconds: attempt.WindowSeconds,
            MaxChars: attempt.MaxChars,
            Verdict: meaning?.Verdict == MeaningVerdict.Pass ? QaVerdict.Pass : QaVerdict.Fail,
            PromptTokens: usage.Prompt,
            CompletionTokens: usage.Completion,
            TotalTokens: usage.Total,
            ModelId: attempt.ModelId,
            TimingVerdict: attempt.Fits ? QaVerdict.Pass : QaVerdict.Fail,
            MeaningVerdict: meaning?.Verdict ?? MeaningVerdict.NotEvaluated,
            MeaningIssues: meaning?.Issues,
            MeaningExplanation: meaning?.Explanation,
            MeaningModelId: meaning?.ModelId,
            MeaningPromptTemplateVersion: meaning?.PromptTemplateVersion,
            MeaningTimestampUtc: meaning?.TimestampUtc,
            MeaningPromptTokens: meaningUsage.Prompt,
            MeaningCompletionTokens: meaningUsage.Completion,
            MeaningTotalTokens: meaningUsage.Total,
            MeaningEndpointHost: meaning is null ? null : context.EndpointHost);
    }

    private static TokenCounts ToCounts(MetaClient.TokenUsage? usage)
    {
        return new TokenCounts(
            usage?.PromptTokens ?? 0,
            usage?.CompletionTokens ?? 0,
            usage?.TotalTokens ?? 0);
    }

    public async Task WriteReportAsync(
        string outputPath,
        QaReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(report);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<QaSegmentInput>> LoadSegmentsAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        string json = await File.ReadAllTextAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        QaSegmentInput[]? segments;
        try
        {
            segments = JsonSerializer.Deserialize<QaSegmentInput[]>(json, opts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid segments JSON in '{inputPath}': {ex.Message}", ex);
        }

        if (segments is null)
        {
            throw new InvalidOperationException($"Empty or null JSON in '{inputPath}'. Expected an array of {{id, text, duration_seconds}}.");
        }

        for (int i = 0; i < segments.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(segments[i].Id))
            {
                throw new InvalidOperationException($"Segment at index {i} has no id.");
            }

            if (segments[i].Text is null)
            {
                throw new InvalidOperationException($"Segment '{segments[i].Id}' has no text.");
            }
        }

        return segments;
    }

    private static string GetEndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return uri.Host;
        }

        return baseUrl;
    }

    private sealed record SegmentContext(
        QaSegmentInput Segment,
        string SourceLanguage,
        string TargetLanguage,
        string EndpointHost,
        double WindowSeconds,
        double TargetCharactersPerSecond,
        int MaxChars);

    private sealed record SegmentOutcome(
        QaAttemptReport FirstAttempt,
        QaAttemptReport[] Revisions,
        QaVerdict FinalVerdict,
        string Translation,
        QaTokenUsage TokenUsage);

    private sealed record RevisionState(
        string Translation,
        IReadOnlyList<string>? LastMeaningIssues,
        TokenAccum Tokens);

    private sealed record RevisionResult(
        QaAttemptReport[] Revisions,
        QaVerdict FinalVerdict,
        string Translation);

    private sealed record AttemptInput(
        int Number, QaAttemptKind Kind, string Translation, double EstimatedSeconds,
        double WindowSeconds, int MaxChars, bool Fits, MetaClient.TokenUsage? Usage, string ModelId);

    private readonly record struct TokenCounts(int Prompt, int Completion, int Total);

    private sealed class TokenAccum
    {
        public int Prompt;
        public int Completion;
        public int Total;

        public TokenAccum(MetaClient.TokenUsage? usage)
        {
            Prompt = usage?.PromptTokens ?? 0;
            Completion = usage?.CompletionTokens ?? 0;
            Total = usage?.TotalTokens ?? (Prompt + Completion);
        }

        public void Add(MetaClient.TokenUsage? usage)
        {
            Prompt += usage?.PromptTokens ?? 0;
            Completion += usage?.CompletionTokens ?? 0;
            Total += usage?.TotalTokens ?? ((usage?.PromptTokens ?? 0) + (usage?.CompletionTokens ?? 0));
        }

        public QaTokenUsage ToUsage() => new(Prompt, Completion, Total);
    }
}
