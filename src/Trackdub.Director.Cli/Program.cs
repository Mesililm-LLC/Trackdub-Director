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

    if (verb != "smoke")
    {
        Console.Error.WriteLine($"Unknown command '{args[0]}'. Expected 'smoke'.");
        PrintUsage();
        return 1;
    }

    // smoke: optional --text <line> (also accepts positional trailing text for convenience)
    string text = DefaultSmokeText;
    for (int i = 1; i < args.Length; i++)
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
            // Allow: trackdub-director smoke "custom line"
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

static void PrintUsage()
{
    Console.WriteLine("Usage: trackdub-director <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  smoke    Smoke test: one real Meta Model API call, English to Spanish.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -h, --help   Show help.");
    Console.WriteLine();
    Console.WriteLine("Run 'trackdub-director smoke --help' for command details.");
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
