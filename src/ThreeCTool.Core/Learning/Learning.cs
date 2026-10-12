using ThreeCTool.Core.Projects;

namespace ThreeCTool.Core.Learning;

/// <summary>Learned, bounded correction per (material, tool, strategy).</summary>
public sealed class FeedCorrection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid MaterialId { get; set; }

    public Guid ToolId { get; set; }

    public StrategyType Strategy { get; set; }

    public double ChipLoadFactor { get; set; } = 1;

    public double SurfaceSpeedFactor { get; set; } = 1;

    public int SampleCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum WearEventKind
{
    Usage,
    Wear,
    Breakage,
    Replaced,
}

public sealed class ToolWearLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ToolId { get; set; }

    public DateTimeOffset LoggedAt { get; set; }

    public WearEventKind Kind { get; set; }

    public double RuntimeMinutes { get; set; }

    public double CutLength { get; set; }

    public Guid? JobFeedbackId { get; set; }

    public string? Notes { get; set; }
}

public sealed class JobFeedback
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset RecordedAt { get; set; }

    public string ProjectName { get; set; } = string.Empty;

    public string GcodeSha256 { get; set; } = string.Empty;

    /// <summary>Feeder run this feedback was imported from (null when entered on the desktop); makes imports idempotent.</summary>
    public string? FeederRunId { get; set; }

    public Guid? MaterialId { get; set; }

    public Guid? ToolId { get; set; }

    /// <summary>Ratings 1 (bad) – 5 (good); null = not rated.</summary>
    public int? FinishRating { get; set; }

    public int? ChatterRating { get; set; }

    public int? WearRating { get; set; }

    public string? Notes { get; set; }

    public List<FeedOverrideEvent> FeedOverrides { get; set; } = [];

    public List<RpmChangeEvent> RpmChanges { get; set; } = [];
}

public sealed class FeedOverrideEvent
{
    public DateTimeOffset At { get; set; }

    public int LineNumber { get; set; }

    public int Percent { get; set; }
}

public sealed class RpmChangeEvent
{
    public DateTimeOffset At { get; set; }

    public int LineNumber { get; set; }

    public double Rpm { get; set; }
}
