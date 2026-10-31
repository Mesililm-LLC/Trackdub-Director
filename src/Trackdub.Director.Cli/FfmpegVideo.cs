using System.Diagnostics;
using System.Globalization;

/// <summary>Paths to the ffmpeg and ffprobe executables.</summary>
internal sealed record FfmpegTools(string Ffmpeg, string Ffprobe);

/// <summary>One external tool invocation; arguments are passed as a list, never re-quoted.</summary>
internal sealed record ToolCommand(string Exe, IReadOnlyList<string> Args, bool RedirectInput = false);

/// <summary>Size and frame rate of a video's first stream, parsed from ffprobe's csv line.</summary>
internal sealed record VideoStream(int Width, int Height, string FpsRational)
{
    public static VideoStream Parse(string csv)
    {
        string[] parts = csv.Trim().Split(',');
        int[] size = parts.Take(2)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
            .ToArray();
        return parts.Length == 3 && size.All(d => d > 0)
            ? new VideoStream(size[0], size[1], parts[2])
            : throw new InvalidOperationException($"ffprobe returned an unexpected stream description: '{csv.Trim()}'.");
    }

    public double Fps
    {
        get
        {
            double[] parts = FpsRational.Split('/')
                .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : double.NaN)
                .ToArray();
            double fps = parts.Length == 2 ? parts[0] / parts[1] : double.NaN;
            return double.IsFinite(fps) ? fps : throw new InvalidOperationException($"Unrecognised frame rate '{FpsRational}'.");
        }
    }
}

/// <summary>
/// One video opened through ffmpeg for the sign-shot command. Frames move as
/// raw RGB24 over pipes, so no image codec runs in this process and the gate
/// sees exactly the pixels ffmpeg decoded. ffmpeg is an external tool, not a
/// package reference; the command fails with the reason when it is missing.
/// </summary>
internal sealed class FfmpegVideo
{
    private readonly FfmpegTools _tools;
    private readonly string _path;
    private readonly VideoStream _stream;

    private FfmpegVideo(FfmpegTools tools, string path, VideoStream stream)
    {
        _tools = tools;
        _path = path;
        _stream = stream;
        Fps = stream.Fps;
    }

    public int Width => _stream.Width;

    public int Height => _stream.Height;

    public double Fps { get; }

    private int FrameBytes => checked(Width * Height * 3);

    /// <summary>Probes the first video stream's size and frame rate.</summary>
    public static FfmpegVideo Open(FfmpegTools tools, string path)
    {
        string csv = RunToString(new ToolCommand(tools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate", "-of", "csv=p=0", path]));
        return new FfmpegVideo(tools, path, VideoStream.Parse(csv));
    }

    /// <summary>Decodes every frame as RGB24. Each frame is a fresh array, so callers may keep it.</summary>
    public IEnumerable<(int Index, byte[] Rgb)> ReadFrames()
    {
        using Process process = Start(new ToolCommand(_tools.Ffmpeg, ["-v", "error", "-i", _path, "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]));
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Stream stdout = process.StandardOutput.BaseStream;
        byte[] buffer = new byte[FrameBytes];
        int index = 0;
        while (ReadFull(stdout, buffer))
        {
            yield return (index++, (byte[])buffer.Clone());
        }

        process.WaitForExit();
        ThrowIfFailed(process, stderr);
    }

    /// <summary>
    /// Starts an encoder that takes RGB24 frames on stdin and copies this
    /// video's audio. The caller writes frames, then calls <see cref="Encoder.Finish"/>.
    /// </summary>
    public Encoder StartEncoder(string output)
    {
        Process process = Start(new ToolCommand(_tools.Ffmpeg,
        [
            "-v", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{Width}x{Height}", "-framerate", _stream.FpsRational, "-i", "pipe:0",
            "-i", _path,
            "-map", "0:v", "-map", "1:a?",
            "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "copy", "-shortest",
            output,
        ], RedirectInput: true));
        return new Encoder(process, new FileInfo(output));
    }

    /// <summary>
    /// Encoder process. If it is disposed before <see cref="Finish"/> (an
    /// exception mid-composite), ffmpeg is killed and the partial output is
    /// deleted rather than closed out into a truncated video.
    /// </summary>
    internal sealed class Encoder : IDisposable
    {
        private readonly Process _process;
        private readonly FileInfo _output;
        private readonly Task<string> _stderr;
        private bool _finished;

        public Encoder(Process process, FileInfo output)
        {
            _process = process;
            _output = output;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public void Write(byte[] rgb) => _process.StandardInput.BaseStream.Write(rgb);

        public void Finish()
        {
            _process.StandardInput.BaseStream.Flush();
            _process.StandardInput.Close();
            _process.WaitForExit();
            _finished = true;
            ThrowIfFailed(_process, _stderr);
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Abandon();
            }

            _process.Dispose();
        }

        private void Abandon()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }

            _output.Refresh();
            if (_output.Exists)
            {
                _output.Delete();
            }
        }
    }

    private bool ReadFull(Stream stream, byte[] buffer)
    {
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read != 0 && read < FrameBytes)
        {
            throw new InvalidOperationException($"ffmpeg ended mid-frame ({read} of {FrameBytes} bytes).");
        }

        return read != 0;
    }

    private static string RunToString(ToolCommand command)
    {
        using Process process = Start(command);
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        ThrowIfFailed(process, stderr);
        return stdout;
    }

    private static Process Start(ToolCommand command)
    {
        var info = new ProcessStartInfo(command.Exe)
        {
            RedirectStandardOutput = !command.RedirectInput,
            RedirectStandardError = true,
            RedirectStandardInput = command.RedirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in command.Args)
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            return Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{command.Exe}'.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{command.Exe}' ({ex.Message}). Install ffmpeg or pass --ffmpeg/--ffprobe.", ex);
        }
    }

    private static void ThrowIfFailed(Process process, Task<string> stderr)
    {
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(process.StartInfo.FileName)} exited with code {process.ExitCode}: {stderr.GetAwaiter().GetResult().Trim()}");
        }
    }
}
