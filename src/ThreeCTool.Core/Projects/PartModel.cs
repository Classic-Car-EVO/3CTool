namespace ThreeCTool.Core.Projects;

public enum BodyRole
{
    Mill,
    Print,
}

public enum TransformKind
{
    Rotate,
    Scale,
    Translate,
}

public sealed class PartModel
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    /// <summary>Path relative to the project folder.</summary>
    public string FilePath { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public LengthUnit ImportUnit { get; set; } = LengthUnit.Millimeter;

    public BodyRole Role { get; set; } = BodyRole.Mill;

    /// <summary>Applied in order after unit conversion; kept as editable history.</summary>
    public List<TransformStep> Transforms { get; set; } = [];

    public MeshRepairReport? RepairReport { get; set; }
}

/// <summary>Rotate: axis (X, Y, Z) + AngleDegrees about the origin. Scale: uniform Factor. Translate: (X, Y, Z).</summary>
public sealed class TransformStep
{
    public TransformKind Kind { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double AngleDegrees { get; set; }

    public double Factor { get; set; } = 1;
}

public sealed class MeshRepairReport
{
    public int RemovedDuplicateTriangles { get; set; }

    public int RemovedDegenerateTriangles { get; set; }

    public int FlippedTriangles { get; set; }

    public int NonManifoldEdges { get; set; }

    public int HolesFilled { get; set; }

    public bool IsWatertight { get; set; }
}

public enum StockType
{
    Box,
    Cylinder,
    Mesh,
}

public sealed class Stock
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public StockType Type { get; set; }

    /// <summary>Box: minimum corner. Cylinder: centre of the -X end face (axis parallel to X).</summary>
    public double OriginX { get; set; }

    public double OriginY { get; set; }

    public double OriginZ { get; set; }

    public double SizeX { get; set; }

    public double SizeY { get; set; }

    public double SizeZ { get; set; }

    public double Diameter { get; set; }

    public double Length { get; set; }

    public string? MeshPath { get; set; }

    public string? MeshSha256 { get; set; }
}

public enum KeepOutAttachment
{
    Machine,

    /// <summary>Moves with the part: setup frame, turns with A (clamps, chuck jaws).</summary>
    Setup,

    /// <summary>Belongs to a setup but stays put when A turns (headstock, tailstock): WCS coordinates of that setup.</summary>
    SetupFixed,
}

public sealed class KeepOutZone
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public KeepOutAttachment Attachment { get; set; }

    /// <summary>Set when <see cref="Attachment"/> is <see cref="KeepOutAttachment.Setup"/> or <see cref="KeepOutAttachment.SetupFixed"/>.</summary>
    public Guid? SetupId { get; set; }

    public SimpleSolid Solid { get; set; } = new();
}
