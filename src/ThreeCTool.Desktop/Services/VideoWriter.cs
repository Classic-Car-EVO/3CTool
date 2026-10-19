using System.Diagnostics;
using System.Globalization;

namespace ThreeCTool.Desktop.Services;

/// <summary>
/// Encodes raw frames (BGRA bytes, top row first) into an MP4 file through an ffmpeg process. Disposing before
/// <see cref="FinishAsync"/> stops ffmpeg and deletes the partial file.
/// </summary>
public sealed class VideoWriter : IAsyncDisposable
{
    public const string NotFoundMessage =
        "Video export needs ffmpeg, which was not found. Install it (Linux: sudo apt install ffmpeg; Windows: winget install ffmpeg) and try again.";

    private readonly Process process;
    private readonly Task<string> errors;
    private readonly string path;
    private bool finished;

    private VideoWriter(Process process, string path, int width, int height)
    {
        this.process = process;
        this.path = path;
        Width = width;
        Height = height;
        errors = process.StandardError.ReadToEndAsync();
    }

    public int Width { get; }

    public int Height { get; }

    public int FrameBytes => Width * Height * 4;

    public int FramesWritten { get; private set; }

    /// <summary>ffmpeg next to the application (bundled) or on the PATH; null when there is none.</summary>
    public static string? FindFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var folders = new[] { AppContext.BaseDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return folders.Select(f => Path.Combine(f.Trim('"'), name)).FirstOrDefault(File.Exists);
    }

    /// <param name="width">Even, like <paramref name="height"/> (4:2:0 chroma).</param>
    public static async Task<VideoWriter> StartAsync(string path, int width, int height, int framesPerSecond, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException($"The video size must be even, got {width} × {height}.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSecond, 1);
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(NotFoundMessage);
        var encoder = await ChooseEncoderAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-video_size", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
            "-framerate", framesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-an", .. encoder, "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            "-f", "mp4", path,
        ];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        return new VideoWriter(process, path, width, height);
    }

    public async Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (frame.Length != FrameBytes)
        {
            throw new ArgumentException($"A frame has {FrameBytes} bytes, got {frame.Length}.", nameof(frame));
        }

        try
        {
            await process.StandardInput.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // ffmpeg quit (bad output path, full disk): its own message says why.
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("Video encoding failed: " + await ErrorsAsync().ConfigureAwait(false));
        }

        FramesWritten++;
    }

    /// <summary>Ends the stream and waits until ffmpeg has written the file.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException("Video encoding failed: " + await ErrorsAsync().ConfigureAwait(false));
        }

        finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!finished)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a partial file left behind is harmless.
            }
        }

        process.Dispose();
    }

    /// <summary>H.264 when ffmpeg has an encoder for it, else MPEG-4 Part 2 (every build has that one).</summary>
    private static async Task<string[]> ChooseEncoderAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-encoders");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var errorText = process.StandardError.ReadToEndAsync(cancellationToken);
        var list = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await errorText.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var names = list.Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length > 1).Select(p => p[1]).ToHashSet();
        return names.Contains("libx264") ? ["-c:v", "libx264", "-preset", "medium", "-crf", "20"]
            : names.Contains("libopenh264") ? ["-c:v", "libopenh264", "-b:v", "8M"]
            : names.Contains("mpeg4") ? ["-c:v", "mpeg4", "-q:v", "3"]
            : throw new InvalidOperationException("The installed ffmpeg has no MP4 video encoder.");
    }

    private async Task<string> ErrorsAsync()
    {
        var text = (await errors.ConfigureAwait(false)).Trim();
        return text.Length == 0 ? $"ffmpeg exited with code {process.ExitCode}." : text;
    }
}
