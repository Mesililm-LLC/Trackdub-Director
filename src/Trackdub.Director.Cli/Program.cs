using System.Text.Json;
using Trackdub.Director.Core;

const string DefaultSmokeText = "The director checks every line before it reaches the audience.";

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    string verb = args[0].ToLowerInvariant();
    if (verb is "-h" or "--help" or "help")
    {
        PrintUsage();
        return 0;
    }

    return verb switch
    {
        "smoke" => await RunSmokeAsync(args[1..]).ConfigureAwait(false),
        "translate" => await RunTranslateAsync(args[1..]).ConfigureAwait(false),
        "models" => await RunModelsAsync(args[1..]).ConfigureAwait(false),
        _ => UnknownCommand(args[0]),
    };
}

static int UnknownCommand(string raw)
{
    Console.Error.WriteLine($"Unknown command '{raw}'. Expected 'smoke', 'translate', or 'models'.");
    PrintUsage();
    return 1;
}

static async Task<int> RunSmokeAsync(string[] args)
{
    string text = DefaultSmokeText;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--text" && i + 1 < args.Length)
        {
            text = args[i + 1];
            i++;
        }
        else if (args[i] is "-h" or "--help")
        {
            PrintSmokeHelp();
            return 0;
        }
        else if (!args[i].StartsWith('-'))
        {
            text = args[i];
        }
        else
        {
            Console.Error.WriteLine($"Unknown option '{args[i]}' for 'smoke'.");
            PrintSmokeHelp();
            return 1;
        }
    }

    if (string.IsNullOrWhiteSpace(text))
    {
        Console.Error.WriteLine("error: --text must be a non-empty line.");
        return 1;
    }

    try
    {
        using var client = new MetaClient();
        string result = await client.TranslateToSpanishAsync(text).ConfigureAwait(false);
        Console.WriteLine($"EN: {text}");
        Console.WriteLine($"ES: {result}");
        return 0;
    }
    catch (MetaApiException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunTranslateAsync(string[] args)
{
    string? input = null;
    string? output = null;
    string from = "en";
    string to = "es";

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
            case "-h":
            case "--help":
                PrintTranslateHelp();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown option '{args[i]}' for 'translate'.");
                PrintTranslateHelp();
                return 1;
        }
    }

    if (string.IsNullOrWhiteSpace(input))
    {
        Console.Error.WriteLine("error: --input <segments.json> is required.");
        PrintTranslateHelp();
        return 1;
    }

    if (string.IsNullOrWhiteSpace(output))
    {
        Console.Error.WriteLine("error: --output <translated.json> is required.");
        PrintTranslateHelp();
        return 1;
    }

    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
    {
        Console.Error.WriteLine("error: --from and --to must be non-empty language codes.");
        return 1;
    }

    string resolvedInput = Path.GetFullPath(input);
    string resolvedOutput = Path.GetFullPath(output);

    if (!File.Exists(resolvedInput))
    {
        Console.Error.WriteLine($"error: input file not found: {resolvedInput}");
        return 1;
    }

    try
    {
        using var client = new MetaClient();
        var service = new TranslationService(client);

        IReadOnlyList<TranslationSegmentInput> segments = await service.LoadSegmentsAsync(resolvedInput).ConfigureAwait(false);
        Console.WriteLine($"Translating {segments.Count} segment(s) from {from} to {to}...");

        TranslationBatchOutput batch = await service.TranslateBatchAsync(segments, from, to).ConfigureAwait(false);
        await service.WriteOutputAsync(resolvedOutput, batch).ConfigureAwait(false);

        Console.WriteLine($"Wrote {batch.Segments.Length} translated segment(s) to {resolvedOutput}");
        Console.WriteLine($"Usage total: prompt={batch.UsageTotal.PromptTokens} completion={batch.UsageTotal.CompletionTokens} total={batch.UsageTotal.TotalTokens}");
        Console.WriteLine($"Model: {batch.Segments[0].Provenance.ModelId} via {batch.Segments[0].Provenance.EndpointHost} (template {batch.Segments[0].Provenance.PromptTemplateVersion})");
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

static async Task<int> RunModelsAsync(string[] args)
{
    if (args.Length > 0)
    {
        if (args[0] is "-h" or "--help")
        {
            PrintModelsHelp();
            return 0;
        }

        Console.Error.WriteLine($"Unknown option '{args[0]}' for 'models'.");
        PrintModelsHelp();
        return 1;
    }

    try
    {
        using var client = new MetaClient();
        IReadOnlyList<MetaClient.ModelInfo> models = await client.ListModelsAsync().ConfigureAwait(false);

        Console.WriteLine($"Available models ({models.Count}):");
        foreach (MetaClient.ModelInfo m in models)
        {
            Console.WriteLine($"  {m.Id} (owned_by: {m.OwnedBy})");
        }

        string llama = models.FirstOrDefault(m => m.Id.Contains("llama", StringComparison.OrdinalIgnoreCase))?.Id ?? "(none)";
        Console.WriteLine();
        Console.WriteLine($"Llama model IDs: {string.Join(", ", models.Where(m => m.Id.Contains("llama", StringComparison.OrdinalIgnoreCase)).Select(m => m.Id).DefaultIfEmpty("(none)"))}");
        Console.WriteLine($"Default META_MODEL: {MetaAuth.GetModel()} ({(models.Any(m => m.Id == MetaAuth.GetModel()) ? "verified against account" : "not in listed models, may still be valid")})");
        Console.WriteLine($"Preferred Llama for translation default: {llama}");

        var json = JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine();
        Console.WriteLine(json);
        return 0;
    }
    catch (MetaApiException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("Usage: trackdub-director <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  smoke      Smoke test: one real Meta Model API call, English to Spanish.");
    Console.WriteLine("  translate  Batch translate a segments JSON file.");
    Console.WriteLine("  models     List models available to this account (GET /models).");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -h, --help   Show help.");
    Console.WriteLine();
    Console.WriteLine("Run 'trackdub-director <command> --help' for command details.");
}

static void PrintSmokeHelp()
{
    Console.WriteLine("Usage: trackdub-director smoke [--text <line>]");
    Console.WriteLine();
    Console.WriteLine("Smoke test: one real Meta Model API call, English to Spanish.");
    Console.WriteLine();
    Console.WriteLine($"  --text <line>   English line to translate. Default: \"{DefaultSmokeText}\"");
    Console.WriteLine("  -h, --help      Show this help.");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required. Never committed; see .env.example.");
    Console.WriteLine("  META_MODEL                              Default: muse-spark-1.1");
    Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
}

static void PrintTranslateHelp()
{
    Console.WriteLine("Usage: trackdub-director translate --input <segments.json> --output <translated.json> [--from en] [--to es]");
    Console.WriteLine();
    Console.WriteLine("Batch translate a JSON array of {id, text} segments through the Meta Model API.");
    Console.WriteLine("Each output segment includes translation, provenance (model, host, timestamp, template version), and usage (token counts).");
    Console.WriteLine();
    Console.WriteLine("  --input <path>    Input JSON file: array of {id, text} (required).");
    Console.WriteLine("  --output <path>   Output JSON file to write (required).");
    Console.WriteLine("  --from <code>     Source language code. Default: en");
    Console.WriteLine("  --to <code>       Target language code. Default: es");
    Console.WriteLine("  -h, --help        Show this help.");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
    Console.WriteLine("  META_MODEL                              Default: muse-spark-1.1 (verified via 'models' command).");
    Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
}

static void PrintModelsHelp()
{
    Console.WriteLine("Usage: trackdub-director models");
    Console.WriteLine();
    Console.WriteLine("List models available to this account via GET /models.");
    Console.WriteLine("Reports Llama model IDs and whether the current META_MODEL default is among them.");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
    Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
}
