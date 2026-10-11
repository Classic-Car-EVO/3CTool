namespace ThreeCTool.Core.Projects;

public enum OptimizationGoal
{
    Quality,
    Speed,
    ToolLife,
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid MachineProfileId { get; set; }

    public Guid? MaterialId { get; set; }

    public Guid? FilamentId { get; set; }

    public Guid? PrintProfileId { get; set; }

    public OptimizationGoal Goal { get; set; } = OptimizationGoal.Quality;

    public Tolerances Tolerances { get; set; } = new();
}

/// <summary>Project default tolerances (mm); overridable per operation.</summary>
public sealed class Tolerances
{
    public double RoughingTolerance { get; set; } = 0.05;

    public double FinishingTolerance { get; set; } = 0.01;

    public double ArcFittingTolerance { get; set; } = 0.005;

    public double RoughingStockToLeave { get; set; } = 0.5;

    public double SemiFinishStockToLeave { get; set; } = 0.2;

    public double FinishingStockToLeave { get; set; }

    public double MaxScallopHeight { get; set; } = 0.02;

    public double GougeThreshold { get; set; } = 0.01;

    /// <summary>Baseline values multiplied by a material's tolerance scale.</summary>
    public static Tolerances ForScale(double scale) => new()
    {
        RoughingTolerance = 0.05 * scale,
        FinishingTolerance = 0.01 * scale,
        ArcFittingTolerance = 0.005 * scale,
        RoughingStockToLeave = 0.5 * scale,
        SemiFinishStockToLeave = 0.2 * scale,
        FinishingStockToLeave = 0,
        MaxScallopHeight = 0.02 * scale,
        GougeThreshold = 0.01 * scale,
    };
}

public sealed class ProjectTool
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ToolId { get; set; }
}
