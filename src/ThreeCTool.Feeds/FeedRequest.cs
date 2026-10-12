using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Feeds;

/// <summary>Everything shared by the operations of one project: machine, material, goal and learned corrections.</summary>
public sealed record FeedContext
{
    public required MachineProfile Machine { get; init; }

    public required Material Material { get; init; }

    public OptimizationGoal Goal { get; init; } = OptimizationGoal.Quality;

    /// <summary>Project tolerances, already scaled for the material.</summary>
    public Tolerances Tolerances { get; init; } = new();

    public IReadOnlyList<FeedCorrection> Corrections { get; init; } = [];

    public FeedsOptions Options { get; init; } = FeedsOptions.Default;
}

/// <summary>One operation with its resolved toolpath settings (mm).</summary>
public sealed record FeedRequest
{
    public Guid OperationId { get; init; }

    public required StrategyType Strategy { get; init; }

    public required Tool Tool { get; init; }

    public required double Stepover { get; init; }

    public required double Stepdown { get; init; }

    public required double Tolerance { get; init; }

    /// <summary>Radial stock this operation leaves.</summary>
    public double RadialStock { get; init; }

    /// <summary>Stock the previous operation left on the surfaces this one finishes; null = project roughing stock.</summary>
    public double? IncomingStock { get; init; }

    /// <summary>Cutting length (mm) used to weight the operation in the RPM choice; 0 = unknown (weight 1).</summary>
    public double CutLength { get; init; }

    public static bool IsRoughing(StrategyType strategy) =>
        strategy is StrategyType.AdaptiveRoughing or StrategyType.ZLevelRoughing or StrategyType.RotaryRoughing or StrategyType.RestMachining;

    /// <summary>Strategies that cut with the tool flank on walls (full diameter) rather than with the tool end.</summary>
    public static bool IsSideCutting(StrategyType strategy) => IsRoughing(strategy) || strategy == StrategyType.Waterline;
}

/// <summary>Axial depth and radial width of cut (mm).</summary>
public readonly record struct Engagement(double AxialDepth, double RadialWidth)
{
    public double Area => AxialDepth * RadialWidth;
}
