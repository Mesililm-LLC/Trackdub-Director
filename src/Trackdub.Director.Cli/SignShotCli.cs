using System.Globalization;
using System.Text.Json;
using Trackdub.Director.Core;

/// <summary>
/// sign-shot: extend one localised sign frame to every frame of a shot, but
/// only when the static-shot gate passes on the whole window. Pass 1 measures
/// every frame in the window and writes nothing on refusal; pass 2 composites
/// and re-encodes the full video, copying its audio.
/// </summary>
internal static class SignShotCli
{
    private const string Stage = "static-shot-composite";

    private static readonly string[] RequiredOptions = ["--video", "--reference", "--localised", "--box", "--start", "--end", "--report"];
    private static readonly string[] OptionalOptions = ["--output", "--min-ssim", "--ffmpeg", "--ffprobe"];

    private sealed record Options(
        string Video,
        string Reference,
        string Localised,
        LocalisationBox Box,
        StaticShotWindow Window,
        string? Output,
        string Report,
        double MinSsim,
        FfmpegTools Tools);

    /// <summary>Parsed options, or the reason parsing failed.</summary>
    private sealed record ParseResult(Options? Options, string? Error)
    {
        public static ParseResult Fail(string error) => new(null, error);
    }

    /// <summary>What the gate decided and, when it composited, what it wrote.</summary>
    private sealed record GateOutcome(StaticShotStatus Status, string? Reason, int FramesComposited, int? OutsideBoxDiffMax, string? Output);

    public static int Run(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        ParseResult parsed = Parse(args);
        if (parsed.Options is not { } options)
        {
            Console.Error.WriteLine($"error: {parsed.Error}");
            PrintHelp();
            return 1;
        }

        try
        {
            StaticShotReport report = Execute(options);
            File.WriteAllText(options.Report, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            PrintSummary(report);
            Console.WriteLine($"  report -> {options.Report}");
            return report.Status == StaticShotStatus.Refused ? 2 : 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static StaticShotReport Execute(Options options)
    {
        RasterImage reference = WicImageDecoder.Decode(File.ReadAllBytes(options.Reference));
        RasterImage localised = WicImageDecoder.Decode(File.ReadAllBytes(options.Localised));
        StaticShotCompositor.Creation creation = StaticShotCompositor.Create(reference, localised, options.Box, options.MinSsim);
        StaticShotCompositor compositor = creation.Compositor ?? throw new InvalidOperationException(creation.Error);

        FfmpegVideo video = FfmpegVideo.Open(options.Tools, options.Video);
        if (video.Width != reference.Width || video.Height != reference.Height)
        {
            throw new InvalidOperationException($"Video is {video.Width}x{video.Height}; reference frame is {reference.Width}x{reference.Height}.");
        }

        StaticShotFrame[] frames = Measure(video, options.Window, compositor);
        GateOutcome outcome = Decide(frames, options) ?? Composite(video, options, compositor, frames.Length);
        return BuildReport(options, video, frames, outcome);
    }

    /// <summary>The outcome when no composite should be written (refused, or measure-only); null means composite.</summary>
    private static GateOutcome? Decide(StaticShotFrame[] frames, Options options)
    {
        if (frames.Length == 0)
        {
            return Refused("No video frames fall inside the window.");
        }

        StaticShotFrame? failure = StaticShotCompositor.FirstFailure(frames);
        if (failure is not null)
        {
            return Refused($"Frame {failure.FrameIndex} (t={failure.TimeSeconds:0.000}s) box SSIM {failure.Ssim:0.######} (largest block luma shift {failure.MaxBlockLumaShift:0.####}) fails the {options.MinSsim:0.00} mean SSIM / {StaticShotCompositor.MaxBlockLumaShift:0} luma-shift gate: the sign region changed, so one repaint cannot stand in for this shot.");
        }

        return options.Output is null ? new GateOutcome(StaticShotStatus.Passed, null, 0, null, null) : null;
    }

    private static GateOutcome Refused(string reason) => new(StaticShotStatus.Refused, reason, 0, null, null);

    private static StaticShotFrame[] Measure(FfmpegVideo video, StaticShotWindow window, StaticShotCompositor compositor)
    {
        var frames = new List<StaticShotFrame>();
        foreach ((int index, byte[] rgb) in video.ReadFrames())
        {
            double t = index / video.Fps;
            if (window.Contains(t))
            {
                BoxSsim ssim = compositor.MeasureSsim(new RasterImage(video.Width, video.Height, rgb));
                // Metrics are stored unrounded so the report agrees with the pass flag computed from them.
                frames.Add(new StaticShotFrame(index, Math.Round(t, 3), ssim.Mean, ssim.MaxBlockLumaShift, compositor.Passes(ssim)));
            }
        }

        return frames.ToArray();
    }

    /// <summary>
    /// Pass 2. Every in-window frame is re-gated before it is encoded, and the
    /// count must match pass 1, so a file that changed between passes aborts
    /// the run (the encoder then deletes its partial output).
    /// </summary>
    private static GateOutcome Composite(FfmpegVideo video, Options options, StaticShotCompositor compositor, int measuredFrames)
    {
        int composited = 0;
        int diffMax = 0;
        using FfmpegVideo.Encoder encoder = video.StartEncoder(options.Output!);
        foreach ((int index, byte[] rgb) in video.ReadFrames())
        {
            if (!options.Window.Contains(index / video.Fps))
            {
                encoder.Write(rgb);
                continue;
            }

            var frame = new RasterImage(video.Width, video.Height, rgb);
            if (!compositor.Passes(compositor.MeasureSsim(frame)))
            {
                throw new InvalidOperationException($"Frame {index} failed the gate on the compositing pass; the video changed after it was measured. Nothing was written.");
            }

            RasterImage output = compositor.Composite(frame, out int diff);
            diffMax = Math.Max(diffMax, diff);
            composited++;
            encoder.Write(output.Rgb);
        }

        if (composited != measuredFrames)
        {
            throw new InvalidOperationException($"The compositing pass saw {composited} in-window frames; the measuring pass saw {measuredFrames}. Nothing was written.");
        }

        encoder.Finish();
        return new GateOutcome(StaticShotStatus.Composited, null, composited, diffMax, options.Output);
    }

    private static StaticShotReport BuildReport(Options options, FfmpegVideo video, StaticShotFrame[] frames, GateOutcome outcome)
    {
        StaticShotFrame? min = frames.MinBy(f => f.Ssim);
        StaticShotFrame? worstShift = frames.MaxBy(f => f.MaxBlockLumaShift);
        return new StaticShotReport(
            Stage,
            outcome.Status,
            outcome.Reason,
            options.Video,
            options.Reference,
            options.Localised,
            options.Box,
            options.Window,
            Math.Round(video.Fps, 3),
            StaticShotCompositor.MetricDescription,
            options.MinSsim,
            StaticShotCompositor.MaxBlockLumaShift,
            frames.Length,
            min?.Ssim,
            min?.FrameIndex,
            frames.Length == 0 ? null : Math.Round(frames.Average(f => f.Ssim), 4),
            worstShift?.MaxBlockLumaShift,
            worstShift?.FrameIndex,
            StaticShotCompositor.FirstFailure(frames),
            outcome.FramesComposited,
            outcome.OutsideBoxDiffMax,
            ImageCalls: 0,
            outcome.Output,
            [
                "image_calls is 0: this stage reuses one already-localised frame; that repaint's Muse Image cost is recorded in the localisation manifest.",
                "outside_box_diff_max is counted on decoded RGB frames before encoding; the h264 output is lossy everywhere, including outside the box.",
                "Frame times assume a constant frame rate (index / fps).",
            ],
            DateTime.UtcNow.ToString("O"),
            frames);
    }

    private static ParseResult Parse(string[] args)
    {
        string? stray = args.Where((a, i) => i % 2 == 0 && !a.StartsWith("--", StringComparison.Ordinal)).FirstOrDefault();
        if (stray is not null || args.Length % 2 != 0)
        {
            return ParseResult.Fail($"Unexpected argument '{stray ?? args[^1]}'.");
        }

        Dictionary<string, string> values = args.Chunk(2)
            .GroupBy(pair => pair[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);
        string? keyError = ValidateKeys(values) ?? ValidateOutput(values);
        return keyError is null ? BuildOptions(values) : ParseResult.Fail(keyError);
    }

    private static string? ValidateKeys(Dictionary<string, string> values)
    {
        string? unknown = values.Keys.FirstOrDefault(k => !RequiredOptions.Contains(k) && !OptionalOptions.Contains(k));
        string? missing = RequiredOptions.FirstOrDefault(k => !values.ContainsKey(k));
        return unknown is not null ? $"Unknown option '{unknown}' for 'sign-shot'."
            : missing is not null ? $"{missing} is required."
            : null;
    }

    private static ParseResult BuildOptions(Dictionary<string, string> values)
    {
        LocalisationBox? box = ParseBox(values);
        StaticShotWindow? window = ParseWindow(values);
        double? minSsim = values.TryGetValue("--min-ssim", out string? raw) ? ParseNumber(raw) : StaticShotCompositor.DefaultMinSsim;
        string? error = box is null ? "--box must be x1,y1,x2,y2 (integers, x2/y2 exclusive)."
            : window is null ? "--start and --end must be seconds with end > start >= 0."
            : minSsim is null ? "--min-ssim must be a number."
            : null;
        if (error is not null)
        {
            return ParseResult.Fail(error);
        }

        var tools = new FfmpegTools(values.GetValueOrDefault("--ffmpeg", "ffmpeg"), values.GetValueOrDefault("--ffprobe", "ffprobe"));
        return new ParseResult(new Options(
            values["--video"], values["--reference"], values["--localised"], box!, window!,
            values.GetValueOrDefault("--output"), values["--report"], minSsim!.Value, tools), null);
    }

    private static LocalisationBox? ParseBox(Dictionary<string, string> values)
    {
        int[] parts = values["--box"].Split(',').Select(p => int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1).ToArray();
        return parts.Length == 4 && parts.All(p => p >= 0) ? new LocalisationBox(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    private static StaticShotWindow? ParseWindow(Dictionary<string, string> values)
    {
        double? start = ParseNumber(values["--start"]);
        double? end = ParseNumber(values["--end"]);
        return start >= 0 && end > start ? new StaticShotWindow(start.Value, end.Value) : null;
    }

    private static double? ParseNumber(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;

    /// <summary>
    /// Refuses an --output that resolves to the input video: the encoder runs
    /// with -y and reads its audio from the input, so it would truncate the
    /// source. Paths are compared after full-path normalisation (case-insensitive
    /// on Windows); hard links to the same file are not detected.
    /// </summary>
    private static string? ValidateOutput(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("--output", out string? output))
        {
            return null;
        }

        string? outputPath = TryGetFullPath(output);
        string? videoPath = TryGetFullPath(values["--video"]);
        if (outputPath is null || videoPath is null)
        {
            return "--output and --video must be valid file paths.";
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(outputPath, videoPath, comparison) ? "--output must not be the input video." : null;
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void PrintSummary(StaticShotReport report)
    {
        Console.WriteLine($"Static-shot gate: {report.Status}");
        Console.WriteLine($"  frames checked {report.FramesChecked}, SSIM min {Format(report.SsimMin)} (frame {report.SsimMinFrame?.ToString(CultureInfo.InvariantCulture) ?? "-"}), mean {Format(report.SsimMean)}, threshold {report.MinSsimThreshold:0.00}");
        if (report.Reason is not null)
        {
            Console.WriteLine($"  {report.Reason}");
        }

        if (report.Output is not null)
        {
            Console.WriteLine($"  composited {report.FramesComposited} frames, outside_box_diff max {report.OutsideBoxDiffMax} -> {report.Output}");
        }
    }

    private static string Format(double? value) => value?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "-";

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director sign-shot --video <clip.mp4> --reference <frame.jpg> --localised <frame.png>");
        Console.WriteLine("         --box x1,y1,x2,y2 --start <s> --end <s> --report <report.json> [--output <out.mp4>] [--min-ssim 0.95]");
        Console.WriteLine();
        Console.WriteLine("Extends one localised sign frame across a whole shot, only if the static-shot gate passes:");
        Console.WriteLine("every frame in [start, end) must keep the box region at or above --min-ssim luma SSIM against");
        Console.WriteLine("the reference frame. Refused shots write a report and no video. Exit code 2 means refused.");
        Console.WriteLine();
        Console.WriteLine("  --localised   Full frame produced by the visual lane from --reference (must match it outside the box).");
        Console.WriteLine("  --output      Omit to measure only.");
        Console.WriteLine("  --ffmpeg/--ffprobe   Tool paths. Default: ffmpeg / ffprobe on PATH.");
    }
}
