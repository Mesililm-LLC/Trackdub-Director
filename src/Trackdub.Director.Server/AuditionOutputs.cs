using System.Text.Json;
using Trackdub.Director.Core;

// Writes winning audition takes and the manifest when AUDITION_OUTPUT_DIR is set.
internal static class AuditionOutputs
{
    // Persists winning WAVs + the tracked manifest only when AUDITION_OUTPUT_DIR
    // names the dataset folder (live capture runs). API tests leave it unset so
    // no fake-run files ever land in the repo.
    internal static async Task TryWriteAuditionOutputsAsync(AuditionReport report, CancellationToken ct)
    {
        string? outputDir = Environment.GetEnvironmentVariable("AUDITION_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        string auditionDir = Path.Combine(outputDir, "media", "audition");
        Directory.CreateDirectory(auditionDir);
        foreach (AuditionSegmentReport segment in report.Segments)
        {
            AuditionTakeReport? winner = segment.Takes.FirstOrDefault(t => t.IsWinner);
            if (winner?.AudioBytes is { } bytes && segment.WinnerFileName is not null)
            {
                await WriteWinnerAsync(auditionDir, segment.WinnerFileName, bytes, ct).ConfigureAwait(false);
            }
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        string manifest = JsonSerializer.Serialize(report, options);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "audition-manifest.json"), manifest, ct).ConfigureAwait(false);
    }

    // The file name comes from AuditionService.TakeFileName, which keeps it inside one folder. The check
    // here is a second guard: the resolved path must stay under the audition directory.
    internal static async Task WriteWinnerAsync(string auditionDir, string fileName, byte[] bytes, CancellationToken ct)
    {
        string root = Path.GetFullPath(auditionDir) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(auditionDir, fileName));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Winning take file name resolves outside the audition directory.", nameof(fileName));
        }

        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
    }
}
