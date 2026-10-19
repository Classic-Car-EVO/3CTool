using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.Tests;

/// <summary>Runs only when ffmpeg is installed.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (VideoWriter.FindFfmpeg() is null)
        {
            Skip = "ffmpeg not installed";
        }
    }
}

public sealed partial class VideoWriterTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("videowriter").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [FfmpegFact]
    public async Task Write_EncodesEveryFrameIntoAnMp4()
    {
        var path = Path.Combine(root, "out.mp4");
        await using (var writer = await VideoWriter.StartAsync(path, 64, 48, 10))
        {
            Assert.Equal(64 * 48 * 4, writer.FrameBytes);
            var frame = new byte[writer.FrameBytes];
            for (var k = 0; k < 12; k++)
            {
                Array.Fill(frame, (byte)(k * 20));
                await writer.WriteFrameAsync(frame);
            }

            Assert.Equal(12, writer.FramesWritten);
            await writer.FinishAsync();
        }

        Assert.True(File.Exists(path));
        Assert.Equal(12, await CountFramesAsync(path));
    }

    [FfmpegFact]
    public async Task Dispose_BeforeFinish_DeletesThePartialFile()
    {
        var path = Path.Combine(root, "partial.mp4");
        await using (var writer = await VideoWriter.StartAsync(path, 32, 32, 5))
        {
            await writer.WriteFrameAsync(new byte[writer.FrameBytes]);
            await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteFrameAsync(new byte[10]));
        }

        Assert.False(File.Exists(path));
    }

    [FfmpegFact]
    public async Task Finish_ReportsFfmpegErrors()
    {
        var path = Path.Combine(root, "missing", "out.mp4");
        await using var writer = await VideoWriter.StartAsync(path, 32, 32, 5);
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            for (var k = 0; k < 100; k++)
            {
                await writer.WriteFrameAsync(new byte[writer.FrameBytes]);
            }

            await writer.FinishAsync();
        });
        Assert.StartsWith("Video encoding failed: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_OddSize_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(() => VideoWriter.StartAsync(Path.Combine(root, "odd.mp4"), 63, 48, 10));

    /// <summary>Decoded video frames in a file, counted by ffmpeg.</summary>
    internal static async Task<int> CountFramesAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
        var info = new ProcessStartInfo(VideoWriter.FindFfmpeg()!) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-hide_banner", "-i", path, "-map", "0:v:0", "-f", "null", "-" })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var text = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        var matches = FrameCount().Matches(text);
        Assert.NotEmpty(matches);
        return int.Parse(matches[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FrameCount();
}
