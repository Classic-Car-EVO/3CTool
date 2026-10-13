using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Google.Protobuf;

using ThreeCTool.Core.Projects;
using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Desktop.Services;

/// <summary>Operation sections of a posted file, for mapping feedback per strategy (see <see cref="FeederTags.Sections"/>).</summary>
public static class GcodeSections
{
    private const string Marker = "(Operation: ";

    /// <summary>
    /// One section per <c>(Operation: name, Strategy)</c> comment: from that line (1-based) to the line before the
    /// next operation comment, or to the end of the file.
    /// </summary>
    public static IReadOnlyList<(int FirstLine, int LastLine, StrategyType Strategy)> Parse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var starts = new List<(int Line, StrategyType Strategy)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal) || !line.EndsWith(')'))
            {
                continue;
            }

            var body = line[Marker.Length..^1];
            var comma = body.LastIndexOf(", ", StringComparison.Ordinal);
            if (comma >= 0 && Enum.TryParse<StrategyType>(body[(comma + 2)..], ignoreCase: false, out var strategy) && Enum.IsDefined(strategy))
            {
                starts.Add((i + 1, strategy));
            }
        }

        var sections = new List<(int, int, StrategyType)>();
        for (var i = 0; i < starts.Count; i++)
        {
            var last = i + 1 < starts.Count ? starts[i + 1].Line - 1 : lines.Count;
            sections.Add((starts[i].Line, last, starts[i].Strategy));
        }

        return sections;
    }

    /// <summary>Lines as the feeder counts them (a final newline does not start another line).</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        return [.. lines.Take(count).Select(l => l.TrimEnd('\r'))];
    }
}

/// <summary>What the feeder shows about a file and returns with feedback.</summary>
public sealed record UploadMetadata(
    string ProjectName,
    string SetupName,
    Guid SetupId,
    string ToolDescription,
    Guid? ToolId,
    string MaterialName,
    Guid? MaterialId,
    double PlannedRpm,
    double EstimatedSeconds,
    string ConfigHash);

/// <summary>Uploads posted files to a feeder (paired or the embedded direct feeder).</summary>
public static class JobUploader
{
    private const int ChunkSize = 64 * 1024;

    public static UploadMetadata Describe(ProjectSession session, GcodeFile file)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(file);
        var setup = session.Setups.FirstOrDefault(s => s.Id == file.SetupId);
        var tool = file.ToolId is { } id ? session.FindTool(id) : null;
        return new UploadMetadata(
            session.Project.Name,
            setup is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{setup.Order}: {setup.Name}"),
            file.SetupId,
            tool is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{tool.Name}, {tool.Type}, D{tool.Diameter:0.###} mm, {tool.FluteCount} flutes"),
            tool?.Id,
            session.Material?.Name ?? string.Empty,
            session.Material?.Id,
            file.Rpm ?? 0,
            file.EstimatedSeconds,
            session.Machine.Controller.ConfigHash ?? string.Empty);
    }

    /// <summary>
    /// Uploads <paramref name="path"/> after checking it still has <paramref name="expectedSha256"/> (the file the
    /// project posted). The feeder verifies the hash again on receipt.
    /// </summary>
    public static async Task<JobInfo> UploadAsync(
        Feeder.Contracts.Feeder.FeederClient client,
        string path,
        string expectedSha256,
        UploadMetadata metadata,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(metadata);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' changed since it was posted; post the project again.");
        }

        var header = new UploadHeader
        {
            Name = Path.GetFileName(path),
            Size = bytes.LongLength,
            Sha256 = sha,
            ProjectName = metadata.ProjectName,
            SetupName = metadata.SetupName,
            ToolDescription = metadata.ToolDescription,
            Material = metadata.MaterialName,
            PlannedRpm = metadata.PlannedRpm,
            EstimatedSeconds = metadata.EstimatedSeconds,
            ConfigHash = metadata.ConfigHash,
        };
        header.Tags[FeederTags.SetupId] = metadata.SetupId.ToString();
        if (metadata.ToolId is { } toolId)
        {
            header.Tags[FeederTags.ToolId] = toolId.ToString();
        }

        if (metadata.MaterialId is { } materialId)
        {
            header.Tags[FeederTags.MaterialId] = materialId.ToString();
        }

        var sections = GcodeSections.Parse(GcodeSections.SplitLines(Encoding.UTF8.GetString(bytes)));
        if (sections.Count > 0)
        {
            header.Tags[FeederTags.Sections] = FeederTags.FormatSections(sections);
        }

        using var call = client.UploadJob(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new UploadChunk { Header = header }, cancellationToken).ConfigureAwait(false);
        for (var offset = 0; offset < bytes.Length; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, bytes.Length - offset);
            await call.RequestStream.WriteAsync(new UploadChunk { Data = ByteString.CopyFrom(bytes, offset, count) }, cancellationToken).ConfigureAwait(false);
            progress?.Report((double)(offset + count) / bytes.Length);
        }

        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        return await call.ResponseAsync.ConfigureAwait(false);
    }
}
