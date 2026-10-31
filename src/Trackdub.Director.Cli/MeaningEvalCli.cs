using System.Text.Json;
using Trackdub.Director.Core;

/// <summary>
/// meaning-eval: run the meaning gate over labeled cases and report catches,
/// misses, false alarms, recall, and false-positive rate.
/// </summary>
internal static class MeaningEvalCli
{
    private sealed record Options(string Input, string Output, string From, string To);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        Options? options = Parse(args, out string? error);
        if (options is null)
        {
            Console.Error.WriteLine($"error: {error}");
            PrintHelp();
            return 1;
        }

        try
        {
            IReadOnlyList<MeaningEvalCase> cases = await MeaningEvalService.LoadCasesAsync(options.Input).ConfigureAwait(false);
            using var client = new MetaClient();
            MeaningEvalReport report = await new MeaningEvalService(client).RunAsync(cases, options.From, options.To).ConfigureAwait(false);
            await File.WriteAllTextAsync(options.Output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            PrintSummary(report.Summary);
            Console.WriteLine($"Report written to {options.Output}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static Options? Parse(string[] args, out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["--from"] = "en", ["--to"] = "es" };
        string[] known = ["--input", "--output", "--from", "--to"];
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!known.Contains(args[i]) || i + 1 >= args.Length)
            {
                error = $"Unexpected argument '{args[i]}' for 'meaning-eval'.";
                return null;
            }

            values[args[i]] = args[i + 1];
        }

        string? missing = new[] { "--input", "--output" }.FirstOrDefault(k => !values.ContainsKey(k));
        error = missing is null ? null : $"{missing} is required.";
        return missing is null ? new Options(values["--input"], values["--output"], values["--from"], values["--to"]) : null;
    }

    private static void PrintSummary(MeaningEvalSummary s)
    {
        Console.WriteLine($"Meaning eval: {s.Cases} cases ({s.SeededErrors} seeded errors, {s.Clean} clean), {s.ApiErrors} API errors");
        Console.WriteLine($"  caught {s.Caught}, missed {s.Missed}, recall {Percent(s.Recall)}");
        Console.WriteLine($"  false alarms {s.FalseAlarms}, false-positive rate {Percent(s.FalsePositiveRate)}");
        foreach (MeaningEvalErrorTypeScore t in s.ByErrorType)
        {
            Console.WriteLine($"  {t.ErrorType}: {t.Caught}/{t.Cases}");
        }

        Console.WriteLine($"  tokens {s.TotalTokens}");
    }

    private static string Percent(double? value) => value is null ? "-" : $"{value.Value:P1}";

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director meaning-eval --input <cases.json> --output <report.json> [--from en] [--to es]");
        Console.WriteLine();
        Console.WriteLine("Runs the meaning gate (the same call the QA loop makes) over labeled cases:");
        Console.WriteLine("[{ id, source_text, candidate, expected: \"Pass\"|\"Fail\", error_type?, note? }].");
        Console.WriteLine("Reports catches, misses, false alarms, recall, and false-positive rate; API errors count in neither rate.");
        Console.WriteLine();
        Console.WriteLine("Environment: META_API_KEY (or META_MODEL_API_KEY) required; META_MODEL, META_MODEL_API_BASE_URL optional.");
    }
}
