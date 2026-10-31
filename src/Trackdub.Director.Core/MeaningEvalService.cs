using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Measures the meaning gate on labeled cases: each candidate goes through the
/// same <see cref="MetaClient.CheckMeaningAsync"/> call the QA loop uses, and
/// the verdict is scored against the label. A gate that could not run is
/// recorded as <see cref="MeaningEvalOutcome.ApiError"/> and left out of both
/// rates, so an outage never reads as a catch or a miss.
/// </summary>
public sealed class MeaningEvalService
{
    public const string Stage = "meaning-eval";

    private readonly MetaClient _client;

    public MeaningEvalService(MetaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<MeaningEvalReport> RunAsync(
        IReadOnlyList<MeaningEvalCase> cases,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var results = new List<MeaningEvalCaseResult>(cases.Count);
        foreach (MeaningEvalCase item in cases)
        {
            MeaningCheckResult gate = await JudgeAsync(item, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
            results.Add(new MeaningEvalCaseResult(item.Id, item.SourceText, item.Candidate, item.Expected, item.ErrorType, Score(item.Expected, gate.Verdict), gate));
        }

        return new MeaningEvalReport(
            Stage,
            sourceLanguage,
            targetLanguage,
            MeaningGate.PromptTemplateVersion,
            DateTime.UtcNow.ToString("O"),
            Summarize(results),
            results.ToArray());
    }

    /// <summary>Scores one gate verdict against its label.</summary>
    public static MeaningEvalOutcome Score(MeaningVerdict expected, MeaningVerdict actual) => (expected, actual) switch
    {
        (_, MeaningVerdict.Error or MeaningVerdict.NotEvaluated) => MeaningEvalOutcome.ApiError,
        (MeaningVerdict.Fail, MeaningVerdict.Fail) => MeaningEvalOutcome.Caught,
        (MeaningVerdict.Fail, _) => MeaningEvalOutcome.Missed,
        (_, MeaningVerdict.Fail) => MeaningEvalOutcome.FalseAlarm,
        _ => MeaningEvalOutcome.CleanPass,
    };

    public static MeaningEvalSummary Summarize(IReadOnlyList<MeaningEvalCaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        int Count(MeaningEvalOutcome outcome) => results.Count(r => r.Outcome == outcome);
        int caught = Count(MeaningEvalOutcome.Caught);
        int missed = Count(MeaningEvalOutcome.Missed);
        int falseAlarms = Count(MeaningEvalOutcome.FalseAlarm);
        int cleanPasses = Count(MeaningEvalOutcome.CleanPass);
        MeaningEvalErrorTypeScore[] byType = results
            .Where(r => r.Outcome is MeaningEvalOutcome.Caught or MeaningEvalOutcome.Missed)
            .GroupBy(r => r.ErrorType ?? "unlabeled", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MeaningEvalErrorTypeScore(g.Key, g.Count(), g.Count(r => r.Outcome == MeaningEvalOutcome.Caught)))
            .ToArray();

        return new MeaningEvalSummary(
            results.Count,
            results.Count(r => r.Expected == MeaningVerdict.Fail),
            results.Count(r => r.Expected != MeaningVerdict.Fail),
            caught,
            missed,
            falseAlarms,
            Count(MeaningEvalOutcome.ApiError),
            Ratio(caught, caught + missed),
            Ratio(falseAlarms, falseAlarms + cleanPasses),
            byType,
            results.Sum(r => r.Gate.Usage?.TotalTokens ?? 0));
    }

    /// <summary>Loads cases from a JSON array; expected must be "Pass" or "Fail".</summary>
    public static async Task<IReadOnlyList<MeaningEvalCase>> LoadCasesAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        MeaningEvalCase[]? cases;
        try
        {
            cases = JsonSerializer.Deserialize<MeaningEvalCase[]>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid meaning-eval cases JSON in '{path}': {ex.Message}", ex);
        }

        string? problem = ValidateCases(cases);
        return problem is null ? cases! : throw new InvalidOperationException($"Invalid meaning-eval cases in '{path}': {problem}");
    }

    private static string? ValidateCases(MeaningEvalCase[]? cases)
    {
        if (cases is null || cases.Length == 0)
        {
            return "at least one case is required.";
        }

        int nullIndex = Array.IndexOf(cases, null);
        if (nullIndex >= 0)
        {
            return $"entry {nullIndex} is null.";
        }

        MeaningEvalCase? incomplete = cases.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.SourceText) || string.IsNullOrWhiteSpace(c.Candidate));
        if (incomplete is not null)
        {
            return $"case '{incomplete.Id}' needs id, source_text, and candidate.";
        }

        MeaningEvalCase? badLabel = cases.FirstOrDefault(c => c.Expected is not (MeaningVerdict.Pass or MeaningVerdict.Fail));
        return badLabel is null ? null : $"case '{badLabel.Id}' must expect Pass or Fail.";
    }

    private async Task<MeaningCheckResult> JudgeAsync(MeaningEvalCase item, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.CheckMeaningAsync(
                new MetaClient.MeaningCheckRequest(item.SourceText, item.Candidate, sourceLanguage, targetLanguage),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is MetaApiException or HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return MeaningGate.ForError(ex.Message, MetaAuth.GetModel(), null, EndpointHost());
        }
    }

    private static string EndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : baseUrl;
    }

    private static double? Ratio(int numerator, int denominator) =>
        denominator == 0 ? null : Math.Round((double)numerator / denominator, 4);
}
