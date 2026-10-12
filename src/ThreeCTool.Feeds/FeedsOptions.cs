using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Feeds;

/// <summary>Model constants of the feeds and speeds engine. Lengths in mm, forces in N, stresses in N/mm².</summary>
public sealed record FeedsOptions
{
    public static FeedsOptions Default { get; } = new();

    /// <summary>RPM candidates are multiples of this step.</summary>
    public double RpmStep { get; init; } = 100;

    /// <summary>Share of the spindle power curve the engine may use.</summary>
    public double PowerUtilization { get; init; } = 0.8;

    /// <summary>Upper bound for the combined radial and lead-angle chip thinning boost.</summary>
    public double MaxChipThinning { get; init; } = 3;

    /// <summary>Resultant (tangential + radial) force over the tangential cutting force.</summary>
    public double ResultantForceFactor { get; init; } = 1.2;

    /// <summary>Bending diameter of the fluted section relative to the tool diameter.</summary>
    public double FluteCoreRatio { get; init; } = 0.8;

    public double CarbideModulus { get; init; } = 600_000;

    public double HssModulus { get; init; } = 210_000;

    public double CarbideBendingStress { get; init; } = 700;

    public double HssBendingStress { get; init; } = 450;

    /// <summary>Roughing deflection limit as a share of the radial stock to leave (at least the tolerance).</summary>
    public double RoughingDeflectionShare { get; init; } = 0.25;

    /// <summary>Finishing deflection limit as a multiple of the operation tolerance.</summary>
    public double FinishingDeflectionFactor { get; init; } = 1;

    /// <summary>Stickout (as multiple of D) beyond which the chip load is reduced 10 % per D, down to half.</summary>
    public double StickoutRatioFree { get; init; } = 3;

    /// <summary>Warn when the final chip load drops below this share of the target (rubbing).</summary>
    public double MinChipLoadShare { get; init; } = 0.4;

    public double PlungeFeedShare { get; init; } = 0.3;

    /// <summary>Smallest engagement assumed for finishing passes.</summary>
    public double MinEngagement { get; init; } = 0.05;

    /// <summary>Measured engagement area within this factor of the nominal one keeps the nominal feed.</summary>
    public double EngagementMargin { get; init; } = 1.15;

    public double MinFeedFactor { get; init; } = 0.1;

    /// <summary>Per-move feed factors are rounded down to multiples of this step.</summary>
    public double FeedFactorStep { get; init; } = 0.05;

    /// <summary>Learned correction bounds and the largest change per job.</summary>
    public double CorrectionMin { get; init; } = 0.5;

    public double CorrectionMax { get; init; } = 1.5;

    public double MaxCorrectionStep { get; init; } = 0.1;

    /// <summary>Share of an operator override or RPM change adopted per job.</summary>
    public double LearningGain { get; init; } = 0.5;

    public double ModulusOf(ToolMaterial material) => material == ToolMaterial.Hss ? HssModulus : CarbideModulus;

    public double BendingStressOf(ToolMaterial material) => material == ToolMaterial.Hss ? HssBendingStress : CarbideBendingStress;

    public static GoalProfile ProfileFor(OptimizationGoal goal) => goal switch
    {
        OptimizationGoal.Speed => new GoalProfile(ChipLoadFactor: 1, SurfaceSpeedPosition: 0.9, TimeWeight: 1, SurfaceSpeedWeight: 0.05, ChipLoadWeight: 0.5),
        OptimizationGoal.ToolLife => new GoalProfile(ChipLoadFactor: 0.85, SurfaceSpeedPosition: 0.25, TimeWeight: 0.1, SurfaceSpeedWeight: 1, ChipLoadWeight: 0.5),
        _ => new GoalProfile(ChipLoadFactor: 0.8, SurfaceSpeedPosition: 0.6, TimeWeight: 0.2, SurfaceSpeedWeight: 1, ChipLoadWeight: 0.5),
    };
}

/// <summary>
/// How a goal shapes the cut: chip load share of the table value, target position in the surface speed range
/// (0 = min, 1 = max) and the RPM score weights for cutting time, surface speed and chip load deficit.
/// </summary>
public readonly record struct GoalProfile(double ChipLoadFactor, double SurfaceSpeedPosition, double TimeWeight, double SurfaceSpeedWeight, double ChipLoadWeight);
