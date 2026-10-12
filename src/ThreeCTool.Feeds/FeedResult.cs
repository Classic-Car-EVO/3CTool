using ThreeCTool.Core.Projects;

namespace ThreeCTool.Feeds;

/// <summary>What capped the feed rate.</summary>
public enum FeedLimit
{
    /// <summary>Target chip load reached; nothing else limits.</summary>
    ChipLoad,
    Power,
    Deflection,
    ToolStrength,
    MachineFeed,
}

public enum FeedWarningLevel
{
    Info,
    Warning,
    Error,
}

public sealed record FeedWarning(FeedWarningLevel Level, string Message);

/// <summary>Cutting state of one operation at one RPM and engagement (mm, mm/min, m/min, W, N, N/mm²).</summary>
public sealed record FeedEvaluation
{
    public required double Rpm { get; init; }

    public required Engagement Engagement { get; init; }

    public required double EffectiveDiameter { get; init; }

    public required double FeedRate { get; init; }

    public required double PlungeRate { get; init; }

    /// <summary>Feed per tooth actually programmed.</summary>
    public required double ChipLoad { get; init; }

    /// <summary>Feed per tooth the engine aimed for (table × goal × correction × stickout × thinning boost).</summary>
    public required double TargetChipLoad { get; init; }

    /// <summary>Largest uncut chip thickness at the programmed feed.</summary>
    public required double MaxChipThickness { get; init; }

    /// <summary>Table chip load at the tool diameter, before goal and correction factors.</summary>
    public required double TableChipLoad { get; init; }

    public required double SurfaceSpeed { get; init; }

    public required double SurfaceSpeedMin { get; init; }

    public required double SurfaceSpeedMax { get; init; }

    public required double SurfaceSpeedTarget { get; init; }

    public required double PowerW { get; init; }

    public required double AvailablePowerW { get; init; }

    public required double Force { get; init; }

    /// <summary>Tip deflection; NaN when the stickout is unknown.</summary>
    public required double Deflection { get; init; }

    public required double AllowedDeflection { get; init; }

    public required double BendingStress { get; init; }

    public required double MaterialRemovalRate { get; init; }

    public required FeedLimit LimitedBy { get; init; }
}

public sealed class FeedResult
{
    internal FeedResult(FeedRequest request, FeedEvaluation evaluation, IReadOnlyList<FeedWarning> warnings, EngagementLimiter limiter)
    {
        Request = request;
        Evaluation = evaluation;
        Warnings = warnings;
        Limiter = limiter;
    }

    public FeedRequest Request { get; }

    public FeedEvaluation Evaluation { get; }

    public IReadOnlyList<FeedWarning> Warnings { get; }

    /// <summary>Per-move slowdown for engagement above the nominal one (measured on the in-process stock).</summary>
    public EngagementLimiter Limiter { get; }

    public CuttingConditions ToConditions() => new()
    {
        Rpm = Evaluation.Rpm,
        FeedRate = Evaluation.FeedRate,
        PlungeRate = Evaluation.PlungeRate,
        ChipLoad = Evaluation.ChipLoad,
        AxialDepth = Evaluation.Engagement.AxialDepth,
        RadialWidth = Evaluation.Engagement.RadialWidth,
        PowerW = Evaluation.PowerW,
    };
}

/// <summary>Feeds for the operations of one G-code file (one tool, one RPM set on the dial).</summary>
public sealed class ToolGroupPlan
{
    internal ToolGroupPlan(double rpm, IReadOnlyList<FeedResult> operations)
    {
        Rpm = rpm;
        Operations = operations;
    }

    public double Rpm { get; }

    public IReadOnlyList<FeedResult> Operations { get; }
}
