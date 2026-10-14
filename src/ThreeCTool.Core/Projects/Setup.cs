using ThreeCTool.Core.Machines;

namespace ThreeCTool.Core.Projects;

public enum SetupOrientation
{
    ThreeAxis,
    Indexed,
    SimultaneousRotary,
}

public enum WorkOffset
{
    G54,
    G55,
    G56,
    G57,
    G58,
    G59,
}

public enum WcsReference
{
    StockTopCenter,
    StockTopFrontLeft,
    PartOrigin,
    RotaryAxis,
}

public sealed class Setup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public int Order { get; set; }

    public string Name { get; set; } = string.Empty;

    public SetupOrientation Orientation { get; set; }

    /// <summary>A angle (degrees) for indexed setups.</summary>
    public double IndexAngle { get; set; }

    /// <summary>Name of the calibrated rotary centre-height setting used.</summary>
    public string? RotaryCenterName { get; set; }

    public WorkOffset WorkOffset { get; set; } = WorkOffset.G54;

    public WcsReference WcsReference { get; set; } = WcsReference.StockTopCenter;

    public double WcsOffsetX { get; set; }

    public double WcsOffsetY { get; set; }

    public double WcsOffsetZ { get; set; }

    public ProbeMethod ProbeMethod { get; set; } = ProbeMethod.ToolSetter;

    public List<Operation> Operations { get; set; } = [];

    public List<PrintOperation> PrintOperations { get; set; } = [];
}

public enum PrintMode
{
    Planar,
    NonPlanar,
    PlanarCoreNonPlanarTop,
    Rotary,
    Skin,
}

public enum PrintVolumeSource
{
    WholePart,
    OuterMinusMilledCore,
    SelectedSurfacesWithThickness,
    TaggedBody,
}

public enum PrintSubstrate
{
    Bed,
    InProcessStock,
}

public sealed class PrintOperation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SetupId { get; set; }

    public int Order { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public PrintMode Mode { get; set; }

    public PrintVolumeSource VolumeSource { get; set; }

    /// <summary>Printed body, or the outer shape for <see cref="PrintVolumeSource.OuterMinusMilledCore"/>.</summary>
    public Guid? PartModelId { get; set; }

    public Guid? CorePartModelId { get; set; }

    /// <summary>Triangle indices of the selected surfaces.</summary>
    public List<int> SelectedFaces { get; set; } = [];

    public double SkinThickness { get; set; }

    public Guid PrintProfileId { get; set; }

    public Guid FilamentId { get; set; }

    public Guid ToolheadId { get; set; }

    public PrintSubstrate Substrate { get; set; }
}

public sealed class ToolpathResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary><see cref="Operation"/> or <see cref="PrintOperation"/> id.</summary>
    public Guid OperationId { get; set; }

    /// <summary>Stock piece number (1-based) when the stock is split into pieces; 0 otherwise.</summary>
    public int Piece { get; set; }

    public string InputHash { get; set; } = string.Empty;

    public string CachePath { get; set; } = string.Empty;

    public DateTimeOffset ComputedAt { get; set; }

    public int MoveCount { get; set; }

    public double CutLength { get; set; }

    public double RapidLength { get; set; }

    public double EstimatedSeconds { get; set; }
}

public sealed class GcodeFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SetupId { get; set; }

    /// <summary>Stock piece number (1-based) the file machines when the stock is split into pieces; 0 otherwise.</summary>
    public int Piece { get; set; }

    public ToolheadType Head { get; set; }

    public Guid? ToolId { get; set; }

    public Guid? ToolheadId { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public int LineCount { get; set; }

    public double? Rpm { get; set; }

    public double EstimatedSeconds { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
}
