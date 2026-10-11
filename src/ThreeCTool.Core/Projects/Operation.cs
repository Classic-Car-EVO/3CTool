namespace ThreeCTool.Core.Projects;

public enum StrategyType
{
    AdaptiveRoughing,
    ZLevelRoughing,
    ParallelFinishing,
    Waterline,
    Scallop,
    RestMachining,
    Pencil,
    RotaryRoughing,
    RotaryFinishing,
}

public enum CutDirection
{
    Climb,
    Conventional,
    Both,
}

public enum BoundaryKind
{
    Stock,
    PartSilhouette,
}

public enum LinkingMode
{
    StayDown,
    Retract,
}

public enum EntryMode
{
    Plunge,
    Ramp,
    Helix,
}

public sealed class Operation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SetupId { get; set; }

    public int Order { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public StrategyType Strategy { get; set; }

    public Guid ToolId { get; set; }

    public OperationParameters Parameters { get; set; } = new();

    /// <summary>Computed by the feeds and speeds engine; null until computed.</summary>
    public CuttingConditions? Conditions { get; set; }
}

/// <summary>Null values fall back to the project tolerances / strategy defaults.</summary>
public sealed class OperationParameters
{
    public double? Stepover { get; set; }

    public double? Stepdown { get; set; }

    public double? Tolerance { get; set; }

    public double? StockToLeave { get; set; }

    public double? AxialStockToLeave { get; set; }

    public double RasterAngle { get; set; }

    public CutDirection Direction { get; set; } = CutDirection.Climb;

    public BoundaryKind Boundary { get; set; } = BoundaryKind.Stock;

    public double BoundaryOffset { get; set; }

    public LinkingMode Linking { get; set; } = LinkingMode.StayDown;

    public EntryMode Entry { get; set; } = EntryMode.Helix;

    public double RampAngle { get; set; } = 3;

    public double LeadRadius { get; set; }

    public double? MinZ { get; set; }

    public double? MaxZ { get; set; }
}

public sealed class CuttingConditions
{
    public double Rpm { get; set; }

    /// <summary>mm/min.</summary>
    public double FeedRate { get; set; }

    public double PlungeRate { get; set; }

    /// <summary>mm per tooth.</summary>
    public double ChipLoad { get; set; }

    public double AxialDepth { get; set; }

    public double RadialWidth { get; set; }

    public double PowerW { get; set; }
}
