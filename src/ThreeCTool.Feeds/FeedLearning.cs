using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Projects;

namespace ThreeCTool.Feeds;

/// <summary>Lines of a G-code file cut by one strategy (inclusive, 1-based as the streamer reports them).</summary>
public readonly record struct FileSection(int FirstLine, int LastLine, StrategyType Strategy);

/// <summary>Operator feedback for one streamed G-code file, as input to <see cref="FeedLearning.Update"/>.</summary>
public sealed record LearningInput
{
    public required JobFeedback Feedback { get; init; }

    public required Guid MaterialId { get; init; }

    public required Guid ToolId { get; init; }

    public required IReadOnlyList<FileSection> Sections { get; init; }

    /// <summary>RPM the file asked for (the dial setting at the tool change).</summary>
    public required double PlannedRpm { get; init; }

    /// <summary>Wear, breakage and replacement events of the tool logged for this job.</summary>
    public IReadOnlyList<ToolWearLog> WearEvents { get; init; } = [];
}

/// <summary>
/// Bounded learning of chip load and surface speed corrections per (material, tool, strategy) from feed overrides,
/// RPM changes, ratings and wear/breakage events. Each job moves a factor by at most
/// <see cref="FeedsOptions.MaxCorrectionStep"/>, within <see cref="FeedsOptions.CorrectionMin"/>–<see cref="FeedsOptions.CorrectionMax"/>.
/// </summary>
public static class FeedLearning
{
    private const double RatingStep = 0.05;
    private const double BreakageStep = 0.1;
    private const double WearStep = 0.05;

    /// <summary>Returns the updated (or new) correction for every strategy in the file; unchanged entries are copies.</summary>
    public static IReadOnlyList<FeedCorrection> Update(IReadOnlyList<FeedCorrection> existing, LearningInput input, DateTimeOffset now, FeedsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(input);
        options ??= FeedsOptions.Default;
        var feedback = input.Feedback;
        var lastLine = input.Sections.Count == 0 ? 0 : input.Sections.Max(s => s.LastLine);

        // The operator's preferred state is (vc · r, fz · p / r) for RPM ratio r and feed override p.
        var rpmRatio = input.PlannedRpm > 0 && feedback.RpmChanges.Count > 0
            ? LineWeighted(feedback.RpmChanges.Select(c => (c.LineNumber, c.Rpm)), 1, lastLine, input.PlannedRpm) / input.PlannedRpm
            : 1;
        double commonChip = 0, commonSpeed = options.LearningGain * Math.Log(Math.Max(rpmRatio, 1e-3));
        commonChip -= commonSpeed;
        commonChip -= RatingPenalty(feedback.FinishRating) + RatingPenalty(feedback.ChatterRating);
        commonSpeed -= RatingPenalty(feedback.WearRating);
        foreach (var e in input.WearEvents)
        {
            if (e.Kind == WearEventKind.Breakage)
            {
                commonChip -= BreakageStep;
            }
            else if (e.Kind == WearEventKind.Wear)
            {
                commonSpeed -= WearStep;
            }
        }

        var overrides = feedback.FeedOverrides.Select(o => (o.LineNumber, (double)o.Percent)).ToList();
        var result = new List<FeedCorrection>();
        foreach (var strategy in input.Sections.Select(s => s.Strategy).Distinct())
        {
            var sections = input.Sections.Where(s => s.Strategy == strategy).ToList();
            double weighted = 0, lines = 0;
            foreach (var s in sections)
            {
                var count = Math.Max(s.LastLine - s.FirstLine + 1, 1);
                weighted += count * LineWeighted(overrides, s.FirstLine, s.LastLine, 100);
                lines += count;
            }

            var chip = commonChip + (options.LearningGain * Math.Log(Math.Max(weighted / lines / 100, 1e-3)));
            var current = existing.FirstOrDefault(c => c.MaterialId == input.MaterialId && c.ToolId == input.ToolId && c.Strategy == strategy);
            var updated = current is null
                ? new FeedCorrection { MaterialId = input.MaterialId, ToolId = input.ToolId, Strategy = strategy }
                : new FeedCorrection
                {
                    Id = current.Id,
                    MaterialId = current.MaterialId,
                    ToolId = current.ToolId,
                    Strategy = current.Strategy,
                    ChipLoadFactor = current.ChipLoadFactor,
                    SurfaceSpeedFactor = current.SurfaceSpeedFactor,
                    SampleCount = current.SampleCount,
                    UpdatedAt = current.UpdatedAt,
                };
            updated.ChipLoadFactor = Step(updated.ChipLoadFactor, chip, options);
            updated.SurfaceSpeedFactor = Step(updated.SurfaceSpeedFactor, commonSpeed, options);
            updated.SampleCount++;
            updated.UpdatedAt = now;
            result.Add(updated);
        }

        return result;
    }

    /// <summary>Applies a log-scale signal, limited per job and to the overall bounds.</summary>
    private static double Step(double factor, double signal, FeedsOptions options)
    {
        var change = Math.Clamp(Math.Exp(signal), 1 - options.MaxCorrectionStep, 1 + options.MaxCorrectionStep);
        return Math.Clamp(factor * change, options.CorrectionMin, options.CorrectionMax);
    }

    /// <summary>Ratings 1–2 reduce the factor (5 % per point below 3); 3–5 or no rating leave it.</summary>
    private static double RatingPenalty(int? rating) => rating is { } r && r < 3 ? RatingStep * (3 - r) : 0;

    /// <summary>
    /// Mean value over lines <paramref name="first"/>–<paramref name="last"/> of a setting that changes at given
    /// lines (events sorted by line; the value before the first event is <paramref name="initial"/>).
    /// </summary>
    private static double LineWeighted(IEnumerable<(int Line, double Value)> events, int first, int last, double initial)
    {
        var sorted = events.OrderBy(e => e.Line).ToList();
        var value = initial;
        var index = 0;
        while (index < sorted.Count && sorted[index].Line <= first)
        {
            value = sorted[index++].Value;
        }

        double sum = 0;
        var position = first;
        while (index < sorted.Count && sorted[index].Line <= last)
        {
            sum += (sorted[index].Line - position) * value;
            position = sorted[index].Line;
            value = sorted[index++].Value;
        }

        sum += (last - position + 1) * value;
        return sum / Math.Max(last - first + 1, 1);
    }
}
