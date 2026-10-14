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

/// <summary>Face of a box stock that faces up on a 3-axis setup; the operator turns the stock by hand in 90° steps.</summary>
public enum StockFace
{
    Top,
    Bottom,

    /// <summary>The −Y face.</summary>
    Front,

    /// <summary>The +Y face.</summary>
    Back,

    /// <summary>The −X face.</summary>
    Left,

    /// <summary>The +X face.</summary>
    Right,
}

/// <summary>Where the turning jig locates a turned stock on the table (its bottom always rests on the table).</summary>
public enum TurnAnchor
{
    /// <summary>Centred on the jig (dowel pins on the stock centre lines): the XY centre stays put.</summary>
    Center,

    /// <summary>Pushed into the front-left corner stops: the front-left corner stays put.</summary>
    FrontLeft,
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

    /// <summary>3-axis: stock face turned up for this setup (box stock only).</summary>
    public StockFace Face { get; set; } = StockFace.Top;

    /// <summary>3-axis: turn about the vertical axis after <see cref="Face"/> (0, 90, 180, 270); null picks it automatically.</summary>
    public int? Spin { get; set; }

    /// <summary>3-axis: how the jig locates the stock after it was turned.</summary>
    public TurnAnchor Anchor { get; set; } = TurnAnchor.Center;

    /// <summary>
    /// 3-axis: deepest cut below the turned stock top (mm). Null picks it: half the stock when another setup machines
    /// the opposite face, else unlimited.
    /// </summary>
    public double? DepthLimit { get; set; }

    /// <summary>3-axis: tabs left at the depth limit plane to hold the part in the stock (0 = none).</summary>
    public int TabCount { get; set; }

    public double TabWidth { get; set; } = 6;

    /// <summary>Tab thickness, centred on the depth limit plane.</summary>
    public double TabThickness { get; set; } = 2;

    /// <summary>With tabs: width of the stock border kept all round as the frame the tabs hold on to.</summary>
    public double FrameWidth { get; set; } = 10;

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
