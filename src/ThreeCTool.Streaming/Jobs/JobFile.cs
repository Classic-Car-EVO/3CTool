using System.Security.Cryptography;
using System.Text;

namespace ThreeCTool.Streaming.Jobs;

/// <summary>
/// A G-code file on local storage, streamed line by line (1-based line numbers). Indexing computes the SHA-256
/// and a byte-offset checkpoint every <see cref="CheckpointInterval"/> lines so resuming does not re-read the file.
/// </summary>
public sealed class JobFile
{
    public const int CheckpointInterval = 1024;

    private readonly List<long> checkpoints;

    private JobFile(string path, string sha256, int lineCount, List<long> checkpoints)
    {
        Path = path;
        Sha256 = sha256;
        LineCount = lineCount;
        this.checkpoints = checkpoints;
    }

    public string Path { get; }

    public string Sha256 { get; }

    public int LineCount { get; }

    public static async Task<JobFile> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var checkpoints = new List<long> { 0 };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long offset = 0;
        var lines = 0;
        var lastByte = (byte)'\n';
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        lines++;
                        if (lines % CheckpointInterval == 0)
                        {
                            checkpoints.Add(offset + i + 1);
                        }
                    }
                }

                offset += read;
                lastByte = buffer[read - 1];
            }
        }

        if (lastByte != (byte)'\n')
        {
            lines++;
        }

        return new JobFile(path, Convert.ToHexStringLower(hash.GetHashAndReset()), lines, checkpoints);
    }

    /// <summary>Enumerates (line number, text) from <paramref name="firstLine"/> to the end.</summary>
    public IEnumerable<(int Line, string Text)> ReadLines(int firstLine = 1)
    {
        firstLine = Math.Max(firstLine, 1);
        var checkpoint = Math.Min((firstLine - 1) / CheckpointInterval, checkpoints.Count - 1);
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        stream.Position = checkpoints[checkpoint];
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: checkpoint == 0);
        var line = (checkpoint * CheckpointInterval) + 1;
        while (reader.ReadLine() is { } text)
        {
            if (line >= firstLine)
            {
                yield return (line, text);
            }

            line++;
        }
    }

    public string? ReadLine(int line) => line < 1 || line > LineCount ? null : ReadLines(line).First().Text;
}
