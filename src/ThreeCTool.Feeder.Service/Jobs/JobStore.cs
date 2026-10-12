using System.Collections.Concurrent;
using System.Security.Cryptography;

using Google.Protobuf;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Security;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Streaming.Jobs;

namespace ThreeCTool.Feeder.Service.Jobs;

/// <summary>Uploaded G-code files with their analysis and preview, under <see cref="FeederOptions.JobsDirectory"/>.</summary>
public sealed class JobStore : IDisposable
{
    private const int MaxNameLength = 200;

    private readonly FeederDatabase database;
    private readonly FeederOptions options;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, JobFile> files = new();
    private readonly ConcurrentDictionary<string, JobAnalysis> analyses = new();
    private readonly SemaphoreSlim analysisGate = new(1, 1);

    public JobStore(FeederDatabase database, FeederOptions options, TimeProvider time)
    {
        this.database = database;
        this.options = options;
        this.time = time;
        Directory.CreateDirectory(options.JobsDirectory);
        foreach (var stale in Directory.EnumerateFiles(options.JobsDirectory, "*.tmp"))
        {
            File.Delete(stale);
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<JobRow> List() => database.ListJobs();

    public JobRow Get(string jobId) => database.GetJob(jobId) ?? throw new KeyNotFoundException($"Job '{jobId}' not found.");

    /// <summary>
    /// Writes the upload to a temporary file, checks size and SHA-256 against the header, then analyses it. A file that
    /// is already on the feeder (same SHA-256) keeps its id and gets the new header.
    /// </summary>
    public async Task<JobRow> UploadAsync(UploadHeader header, IAsyncEnumerable<ByteString> data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(data);
        header = Validate(header);
        var tempPath = Path.Combine(options.JobsDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long written = 0;
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await foreach (var chunk in data.WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        written += chunk.Length;
                        if (written > header.Size)
                        {
                            throw new ArgumentException($"Upload is larger than the announced {header.Size} bytes.");
                        }

                        hash.AppendData(chunk.Span);
                        await stream.WriteAsync(chunk.Memory, cancellationToken).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                if (written != header.Size)
                {
                    throw new ArgumentException($"Upload ended after {written} of {header.Size} bytes.");
                }

                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(sha, header.Sha256, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Upload SHA-256 does not match the header; the file was corrupted in transfer.");
                }
            }

            if (database.FindJobBySha256(header.Sha256) is { } existing)
            {
                database.UpdateJobHeader(existing.Id, header);
                Changed?.Invoke(this, EventArgs.Empty);
                return existing with { Header = header };
            }

            var id = Guid.CreateVersion7().ToString("N");
            var path = JobPath(id);
            File.Move(tempPath, path);
            JobFile file;
            try
            {
                file = await JobFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(file.Sha256, header.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Job file changed on disk after upload.");
                }

                await AnalyzeAsync(id, file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                DeleteFiles(id);
                throw;
            }

            var row = new JobRow(id, header, file.LineCount, time.GetUtcNow());
            database.InsertJob(row);
            files[id] = file;
            Changed?.Invoke(this, EventArgs.Empty);
            return row;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Opens (and re-hashes, once per process) the job file; a changed file is refused.</summary>
    public async Task<JobFile> OpenAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = Get(jobId);
        if (files.TryGetValue(jobId, out var cached))
        {
            return cached;
        }

        var path = JobPath(jobId);
        if (!File.Exists(path))
        {
            throw new FeederStateException($"The file of job '{job.Header.Name}' is missing on the feeder.");
        }

        var file = await JobFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(file.Sha256, job.Header.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FeederStateException($"The file of job '{job.Header.Name}' was modified on the feeder (SHA-256 mismatch).");
        }

        return files.GetOrAdd(jobId, file);
    }

    public async Task<JobAnalysis> GetAnalysisAsync(string jobId, CancellationToken cancellationToken)
    {
        if (analyses.TryGetValue(jobId, out var cached))
        {
            return cached;
        }

        var path = AnalysisPath(jobId);
        if (File.Exists(path))
        {
            try
            {
                return analyses.GetOrAdd(jobId, JobAnalysis.Load(path));
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
            {
                // Rebuilt below.
            }
        }

        var file = await OpenAsync(jobId, cancellationToken).ConfigureAwait(false);
        return (await AnalyzeAsync(jobId, file, cancellationToken).ConfigureAwait(false)).Analysis;
    }

    public async Task<Preview> GetPreviewAsync(string jobId, CancellationToken cancellationToken)
    {
        Get(jobId);
        var path = PreviewPath(jobId);
        Preview preview;
        if (File.Exists(path))
        {
            try
            {
                preview = Preview.Parser.ParseFrom(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                preview.JobId = jobId;
                return preview;
            }
            catch (InvalidProtocolBufferException)
            {
                // Rebuilt below.
            }
        }

        var file = await OpenAsync(jobId, cancellationToken).ConfigureAwait(false);
        preview = (await AnalyzeAsync(jobId, file, cancellationToken).ConfigureAwait(false)).Preview;
        preview.JobId = jobId;
        return preview;
    }

    public void Delete(string jobId)
    {
        Get(jobId);
        database.DeleteJob(jobId);
        files.TryRemove(jobId, out _);
        analyses.TryRemove(jobId, out _);
        DeleteFiles(jobId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<(JobAnalysis Analysis, Preview Preview)> AnalyzeAsync(string jobId, JobFile file, CancellationToken cancellationToken)
    {
        await analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (analysis, preview) = await Task.Run(() => JobAnalysis.Analyze(file, options, cancellationToken), cancellationToken).ConfigureAwait(false);
            WriteAtomically(AnalysisPath(jobId), analysis.Save);
            WriteAtomically(PreviewPath(jobId), p => File.WriteAllBytes(p, preview.ToByteArray()));
            analyses[jobId] = analysis;
            return (analysis, preview);
        }
        finally
        {
            analysisGate.Release();
        }
    }

    private static void WriteAtomically(string path, Action<string> write)
    {
        var temp = path + ".tmp";
        write(temp);
        File.Move(temp, path, overwrite: true);
    }

    private UploadHeader Validate(UploadHeader header)
    {
        header = header.Clone();
        var name = Path.GetFileName(header.Name?.Trim() ?? string.Empty);
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException("Job name must be 1-200 printable characters.");
        }

        header.Name = name;
        if (header.Size <= 0 || header.Size > options.MaxUploadBytes)
        {
            throw new ArgumentException($"Job size must be between 1 byte and {options.MaxUploadBytes} bytes.");
        }

        header.Sha256 = header.Sha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (header.Sha256.Length != 64 || !header.Sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("Header SHA-256 must be 64 hexadecimal digits.");
        }

        return header;
    }

    private string JobPath(string id) => Path.Combine(options.JobsDirectory, id + ".nc");

    private string AnalysisPath(string id) => Path.Combine(options.JobsDirectory, id + ".analysis");

    private string PreviewPath(string id) => Path.Combine(options.JobsDirectory, id + ".preview");

    private void DeleteFiles(string id)
    {
        foreach (var path in new[] { JobPath(id), AnalysisPath(id), PreviewPath(id) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void Dispose() => analysisGate.Dispose();
}
