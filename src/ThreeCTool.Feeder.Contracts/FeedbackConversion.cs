using System.Globalization;

using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Projects;

namespace ThreeCTool.Feeder.Contracts;

/// <summary>Keys of <see cref="UploadHeader.Tags"/> the desktop uses to map feedback back to the library.</summary>
public static class FeederTags
{
    public const string MaterialId = "material_id";
    public const string ToolId = "tool_id";
    public const string SetupId = "setup_id";

    /// <summary>Line ranges per strategy, see <see cref="FormatSections"/>.</summary>
    public const string Sections = "sections";

    /// <summary><c>first-last:Strategy</c> entries separated by <c>;</c>.</summary>
    public static string FormatSections(IEnumerable<(int FirstLine, int LastLine, StrategyType Strategy)> sections) =>
        string.Join(';', sections.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.FirstLine}-{s.LastLine}:{s.Strategy}")));

    public static IReadOnlyList<(int FirstLine, int LastLine, StrategyType Strategy)> ParseSections(string? text)
    {
        var result = new List<(int, int, StrategyType)>();
        foreach (var entry in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            var dash = entry.IndexOf('-', StringComparison.Ordinal);
            if (colon < 0 || dash < 0 || dash > colon
                || !int.TryParse(entry.AsSpan(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
                || !int.TryParse(entry.AsSpan(dash + 1, colon - dash - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var last)
                || !Enum.TryParse<StrategyType>(entry.AsSpan(colon + 1), out var strategy))
            {
                throw new FormatException($"Invalid section '{entry}'.");
            }

            result.Add((first, last, strategy));
        }

        return result;
    }

    public static Guid? GetGuid(UploadHeader header, string key)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.Tags.TryGetValue(key, out var text) && Guid.TryParse(text, out var id) ? id : null;
    }
}

/// <summary>Converts feedback downloaded from a feeder into library entities for <c>FeedLearning</c>.</summary>
public static class FeedbackConversion
{
    public static JobFeedback ToJobFeedback(FeedbackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var job = record.Job ?? new UploadHeader();
        var feedback = record.Feedback ?? new FeedbackSubmission { Skipped = true };
        var rated = !feedback.Skipped;
        return new JobFeedback
        {
            RecordedAt = record.RecordedAt?.ToDateTimeOffset() ?? record.EndedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
            ProjectName = job.ProjectName,
            GcodeSha256 = job.Sha256,
            MaterialId = FeederTags.GetGuid(job, FeederTags.MaterialId),
            ToolId = FeederTags.GetGuid(job, FeederTags.ToolId),
            FinishRating = rated ? Rating(feedback.FinishRating) : null,
            ChatterRating = rated ? Rating(feedback.ChatterRating) : null,
            WearRating = rated ? Rating(feedback.WearRating) : null,
            Notes = string.IsNullOrWhiteSpace(feedback.Notes) ? null : feedback.Notes,
            FeedOverrides = [.. record.FeedOverrides.Select(o => new FeedOverrideEvent { At = o.At.ToDateTimeOffset(), LineNumber = o.Line, Percent = o.Percent })],
            RpmChanges = [.. record.RpmChanges.Select(c => new RpmChangeEvent { At = c.At.ToDateTimeOffset(), LineNumber = c.Line, Rpm = c.Rpm })],
        };
    }

    /// <summary>Tool log entries of the run: usage (runtime), plus the wear/breakage/replacement the operator reported.</summary>
    public static IReadOnlyList<ToolWearLog> ToWearLogs(FeedbackRecord record, Guid jobFeedbackId)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (FeederTags.GetGuid(record.Job ?? new UploadHeader(), FeederTags.ToolId) is not { } toolId)
        {
            return [];
        }

        var at = record.EndedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var result = new List<ToolWearLog>
        {
            new()
            {
                ToolId = toolId,
                LoggedAt = at,
                Kind = WearEventKind.Usage,
                RuntimeMinutes = record.RuntimeSeconds / 60.0,
                JobFeedbackId = jobFeedbackId,
            },
        };
        var kind = record.Feedback?.WearEvent switch
        {
            WearEvent.Wear => WearEventKind.Wear,
            WearEvent.Breakage => WearEventKind.Breakage,
            WearEvent.Replaced => WearEventKind.Replaced,
            _ => (WearEventKind?)null,
        };
        if (kind is { } k)
        {
            result.Add(new ToolWearLog
            {
                ToolId = toolId,
                LoggedAt = at,
                Kind = k,
                JobFeedbackId = jobFeedbackId,
                Notes = string.IsNullOrWhiteSpace(record.Feedback!.Notes) ? null : record.Feedback.Notes,
            });
        }

        return result;
    }

    private static int? Rating(int value) => value is >= 1 and <= 5 ? value : null;
}
