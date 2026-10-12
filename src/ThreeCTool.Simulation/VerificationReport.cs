using System.Numerics;

using ThreeCTool.Compute;

namespace ThreeCTool.Simulation;

public enum IssueKind
{
    /// <summary>Material removed below the part surface by more than the gouge threshold.</summary>
    Gouge,

    /// <summary>Shank, holder or head touches material during a feed move.</summary>
    StockCollision,

    /// <summary>Any part of the tool, including the cutter, touches material during a rapid move.</summary>
    RapidCollision,

    /// <summary>Shank, holder or head (or the cutter during a rapid) touches the part.</summary>
    PartCollision,

    KeepOutCollision,

    TravelLimit,

    /// <summary>Dexel segment capacity exceeded; some material was kept.</summary>
    StockModel,

    /// <summary>Input missing; a check was skipped or a default was used.</summary>
    MissingData,
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Finding of the verification. Move indices refer to <see cref="Toolpaths.Toolpath.Moves"/> (or the print moves)
/// of the step; −1 when not move-related. <see cref="Location"/> is in the part frame.
/// </summary>
public sealed record VerificationIssue(
    IssueKind Kind,
    IssueSeverity Severity,
    Guid? StepId,
    int FirstMove,
    int LastMove,
    Vector3? Location,
    string? Body,
    string Message);

public sealed class StepSummary
{
    public required Guid StepId { get; init; }

    public required string Name { get; init; }

    /// <summary>Estimated time of the step (s).</summary>
    public double Duration { get; init; }

    /// <summary>Time (s, from the step start) at which each move finishes.</summary>
    public required double[] MoveEndTimes { get; init; }

    /// <summary>Material removed (negative) or deposited (positive) by the step (mm³).</summary>
    public double VolumeChange { get; init; }

    /// <summary>Printing only: filament used (mm) and its mass (g; 0 when the density is unknown).</summary>
    public double FilamentLength { get; init; }

    public double FilamentMass { get; init; }
}

public sealed class DeviationSummary
{
    /// <summary>Deepest gouge (mm, positive), 0 when none.</summary>
    public double MaxGouge { get; init; }

    /// <summary>Largest leftover material within the deviation range (mm).</summary>
    public double MaxLeftover { get; init; }

    public double MeanLeftover { get; init; }

    /// <summary>Stock surface samples with their signed distance to the part (heatmap source).</summary>
    public required DeviationSample[] Samples { get; init; }
}

public sealed class VerificationReport
{
    public List<VerificationIssue> Issues { get; } = [];

    public List<StepSummary> Steps { get; } = [];

    public DeviationSummary? Deviation { get; set; }

    public float Resolution { get; set; }

    public double CycleTime => Steps.Sum(s => s.Duration);

    /// <summary>Collisions, gouges and travel violations block G-code output.</summary>
    public bool BlocksOutput => Issues.Exists(i => i.Severity == IssueSeverity.Error);

    /// <summary>Output may be written when nothing blocks it or the user explicitly overrides.</summary>
    public bool MayWriteOutput(bool userOverride) => !BlocksOutput || userOverride;

    /// <summary>Step and move running at <paramref name="seconds"/> from the job start; null past the end.</summary>
    public (int Step, int Move)? Locate(double seconds)
    {
        var start = 0.0;
        for (var s = 0; s < Steps.Count; s++)
        {
            var times = Steps[s].MoveEndTimes;
            if (seconds <= start + Steps[s].Duration && times.Length > 0)
            {
                var index = Array.BinarySearch(times, seconds - start);
                return (s, Math.Min(index < 0 ? ~index : index, times.Length - 1));
            }

            start += Steps[s].Duration;
        }

        return null;
    }
}
