using System.Text.Json;
using Trackdub.Director.Core;

internal static class QaCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? input = null;
        string? output = null;
        string from = "en";
        string to = "es";
        int maxAttempts = 3;
        double tolerance = 0.10;
        double cps = DurationEstimator.DefaultCharactersPerSecond;
        bool hasMaxAttempts = false;
        bool hasTolerance = false;
        bool hasCps = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" when i + 1 < args.Length:
                    input = args[i + 1];
                    i++;
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[i + 1];
                    i++;
                    break;
                case "--from" when i + 1 < args.Length:
                    from = args[i + 1];
                    i++;
                    break;
                case "--to" when i + 1 < args.Length:
                    to = args[i + 1];
                    i++;
                    break;
                case "--max-attempts" when i + 1 < args.Length:
                    if (!int.TryParse(args[i + 1], out maxAttempts) || maxAttempts < 0)
                    {
                        Console.Error.WriteLine("error: --max-attempts must be a non-negative integer.");
                        return 1;
                    }

                    hasMaxAttempts = true;
                    i++;
                    break;
                case "--tolerance" when i + 1 < args.Length:
                    if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tolerance) || tolerance < 0)
                    {
                        Console.Error.WriteLine("error: --tolerance must be a non-negative number (fraction, e.g. 0.10 for 10%).");
                        return 1;
                    }

                    hasTolerance = true;
                    i++;
                    break;
                case "--cps" when i + 1 < args.Length:
                    if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cps) || cps <= 0)
                    {
                        Console.Error.WriteLine("error: --cps must be a positive number (characters per second).");
                        return 1;
                    }

                    hasCps = true;
                    i++;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}' for 'qa'.");
                    PrintHelp();
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("error: --input <segments.json> is required.");
            PrintHelp();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("error: --output <report.json> is required.");
            PrintHelp();
            return 1;
        }

        string resolvedInput = Path.GetFullPath(input);
        string resolvedOutput = Path.GetFullPath(output);

        if (!File.Exists(resolvedInput))
        {
            Console.Error.WriteLine($"error: input file not found: {resolvedInput}");
            return 1;
        }

        // Build options: CLI overrides env, env overrides defaults
        QaOptions envOptions = QaOptions.FromEnvironment();
        var cpsOverrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in envOptions.CharactersPerSecondByLanguage)
        {
            cpsOverrides[kv.Key] = kv.Value;
        }

        if (hasCps)
        {
            // Apply CLI cps to the target language (the one whose timing we actually check)
            cpsOverrides[to.Trim().ToLowerInvariant()] = cps;
        }

        var options = new QaOptions
        {
            MaxAttempts = hasMaxAttempts ? maxAttempts : envOptions.MaxAttempts,
            ToleranceFraction = hasTolerance ? tolerance : envOptions.ToleranceFraction,
            CharactersPerSecondByLanguage = cpsOverrides,
            ReasoningEffort = envOptions.ReasoningEffort,
        };

        try
        {
            IReadOnlyList<QaSegmentInput> segments = await QaService.LoadSegmentsAsync(resolvedInput).ConfigureAwait(false);
            Console.WriteLine($"QA: {segments.Count} segment(s) {from}->{to} | max-attempts={options.MaxAttempts} tolerance={options.ToleranceFraction:P0} cps={options.GetCharactersPerSecond(to):0.#} reasoning={options.ReasoningEffort ?? "(default)"}");

            using var client = new MetaClient();
            var service = new QaService(client, options);
            QaReport report = await service.RunAsync(segments, from, to).ConfigureAwait(false);
            await service.WriteReportAsync(resolvedOutput, report).ConfigureAwait(false);

            PrintSummary(report);
            Console.WriteLine($"Report written to {resolvedOutput}");
            return 0;
        }
        catch (MetaApiException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    public static void PrintSummary(QaReport report)
    {
        Console.WriteLine($"Segments: {report.Totals.TotalSegments} | First-pass: {report.Totals.FirstPassCount}/{report.Totals.TotalSegments} ({report.Totals.FirstPassRate:P0}) | Final: {report.Totals.FinalPassCount}/{report.Totals.TotalSegments} ({report.Totals.FinalPassRate:P0}) | Revisions: {report.Totals.TotalRevisions} | Tokens: {report.Totals.TotalTokens} (prompt {report.Totals.TotalPromptTokens} + completion {report.Totals.TotalCompletionTokens})");
        foreach (QaSegmentReport seg in report.Segments)
        {
            string flag = seg.FinalVerdict == QaVerdict.Pass ? "PASS" : "FAIL";
            string firstFlag = seg.FirstAttempt.Verdict == QaVerdict.Pass ? "pass" : "fail";
            Console.WriteLine($"  {seg.Id}: window {seg.WindowSeconds:0.00}s | first {firstFlag} ({seg.FirstAttempt.EstimatedSeconds:0.00}s, {seg.FirstAttempt.Translation.Length}c) | final {flag} ({seg.FinalTranslation.Length}c, {seg.TokenUsage.TotalTokens} tok) | revisions {seg.Revisions.Length}");
            if (seg.Revisions.Length > 0)
            {
                foreach (QaAttemptReport rev in seg.Revisions)
                {
                    Console.WriteLine($"    rev #{rev.AttemptNumber}: {rev.Verdict.ToString().ToLowerInvariant()} {rev.EstimatedSeconds:0.00}s \"{Trunc(rev.Translation, 72)}\"");
                }
            }
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director qa --input <segments.json> --output <report.json> [options]");
        Console.WriteLine();
        Console.WriteLine("Run translate + QA timing check over a segments file. Each segment may carry");
        Console.WriteLine("an optional duration_seconds (dubbing window). When absent, the window is");
        Console.WriteLine("estimated from the source line's spoken duration at the configured CPS.");
        Console.WriteLine("A deterministic duration estimator (chars / cps) checks each translation;");
        Console.WriteLine("failures are bounced back to the model for revision (preserving meaning,");
        Console.WriteLine("fitting the window) up to --max-attempts. Never silently accepted.");
        Console.WriteLine();
        Console.WriteLine("  --input <path>        Input JSON: array of {id, text, duration_seconds?} (required).");
        Console.WriteLine("  --output <path>       Output report JSON path (required).");
        Console.WriteLine("  --from <code>         Source language code. Default: en");
        Console.WriteLine("  --to <code>           Target language code. Default: es");
        Console.WriteLine("  --max-attempts <n>    Max revision attempts after initial. Default: 3");
        Console.WriteLine("  --tolerance <frac>    Timing tolerance fraction. Default: 0.10 (10%)");
        Console.WriteLine("  --cps <n>             Target language characters per second. Default: 15");
        Console.WriteLine("  -h, --help            Show this help.");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
        Console.WriteLine("  META_MODEL                              Default: muse-spark-1.1");
        Console.WriteLine("  META_REASONING_EFFORT                   low|medium|high. Default: low (short translation)");
        Console.WriteLine("  QA_MAX_ATTEMPTS, QA_TOLERANCE, QA_CPS_<LANG>  Optional overrides for qa options.");
        Console.WriteLine();
        Console.WriteLine("Report JSON: per segment the window, first-attempt estimate+verdict, each");
        Console.WriteLine("revision attempt, final verdict, and token usage. Totals: first-pass rate,");
        Console.WriteLine("final pass rate, total revisions, total tokens.");
    }
}
