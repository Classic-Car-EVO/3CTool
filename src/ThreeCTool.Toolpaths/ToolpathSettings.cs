using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Toolpaths;

/// <summary>Operation parameters with every default resolved (mm, degrees).</summary>
public sealed record ToolpathSettings
{
    public required double Stepover { get; init; }

    public required double Stepdown { get; init; }

    public required double Tolerance { get; init; }

    public required double RadialStock { get; init; }

    public required double AxialStock { get; init; }

    public double RasterAngle { get; init; }

    public CutDirection Direction { get; init; }

    public BoundaryKind Boundary { get; init; }

    public double BoundaryOffset { get; init; }

    public LinkingMode Linking { get; init; }

    public EntryMode Entry { get; init; }

    public double RampAngle { get; init; } = 3;

    public double LeadRadius { get; init; }

    public double? MinZ { get; init; }

    public double? MaxZ { get; init; }

    public ClearingPattern Pattern { get; init; }

    public RotaryPattern RotaryPattern { get; init; }

    public int PencilPasses { get; init; } = 1;

    public double RetractClearance { get; init; } = 3;

    public static bool IsRoughing(StrategyType strategy) =>
        strategy is StrategyType.AdaptiveRoughing or StrategyType.ZLevelRoughing or StrategyType.RotaryRoughing or StrategyType.RestMachining;

    public static ToolpathSettings Resolve(Operation operation, Tool tool, Tolerances tolerances)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(tolerances);
        var p = operation.Parameters;
        var d = tool.Diameter;
        var roughing = IsRoughing(operation.Strategy);
        var finishingStepover = FinishingStepover(tool, tolerances.MaxScallopHeight);
        var (stepover, stepdown) = operation.Strategy switch
        {
            StrategyType.AdaptiveRoughing => (0.1 * d, d),
            StrategyType.ZLevelRoughing or StrategyType.RestMachining or StrategyType.RotaryRoughing => (0.5 * d, 0.5 * d),
            _ => (finishingStepover, finishingStepover),
        };
        stepover = Positive(p.Stepover) ?? stepover;
        stepdown = Positive(p.Stepdown) ?? stepdown;
        if (roughing)
        {
            var limit = tool.MaxDepthOfCut is > 0 ? tool.MaxDepthOfCut.Value : tool.FluteLength > 0 ? tool.FluteLength : double.MaxValue;
            stepdown = Math.Min(stepdown, limit);
        }

        var radial = p.StockToLeave ?? operation.Strategy switch
        {
            StrategyType.RestMachining => tolerances.SemiFinishStockToLeave,
            _ when roughing => tolerances.RoughingStockToLeave,
            _ => tolerances.FinishingStockToLeave,
        };
        return new ToolpathSettings
        {
            Stepover = stepover,
            Stepdown = stepdown,
            Tolerance = Positive(p.Tolerance) ?? (roughing ? tolerances.RoughingTolerance : tolerances.FinishingTolerance),
            RadialStock = radial,
            AxialStock = p.AxialStockToLeave ?? radial,
            RasterAngle = p.RasterAngle,
            Direction = p.Direction,
            Boundary = p.Boundary,
            BoundaryOffset = p.BoundaryOffset,
            Linking = p.Linking,
            Entry = p.Entry,
            RampAngle = p.RampAngle is > 0 and < 90 ? p.RampAngle : 3,
            LeadRadius = Math.Max(p.LeadRadius, 0),
            MinZ = p.MinZ,
            MaxZ = p.MaxZ,
            Pattern = p.Pattern,
            RotaryPattern = p.RotaryPattern,
            PencilPasses = Math.Max(p.PencilPasses, 1),
            RetractClearance = p.RetractClearance > 0 ? p.RetractClearance : 3,
        };
    }

    /// <summary>Stepover for a scallop height: 2·√(h(2r − h)) for ball/corner radius r, 10 % of D for sharp tools.</summary>
    public static double FinishingStepover(Tool tool, double scallopHeight)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var r = tool.Type switch
        {
            ToolType.BallEnd => tool.Diameter / 2,
            ToolType.BullNose => tool.CornerRadius,
            ToolType.TaperedBall => tool.TipDiameter / 2,
            _ => 0,
        };
        if (r <= 0 || scallopHeight <= 0)
        {
            return 0.1 * tool.Diameter;
        }

        var h = Math.Min(scallopHeight, r);
        return Math.Min(2 * Math.Sqrt(h * ((2 * r) - h)), 0.5 * tool.Diameter);
    }

    private static double? Positive(double? value) => value is > 0 ? value : null;
}
