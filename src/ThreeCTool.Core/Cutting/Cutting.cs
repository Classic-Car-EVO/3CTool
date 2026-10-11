namespace ThreeCTool.Core.Cutting;

// Knife provisions: data model only, no flattening/nesting algorithms in the first release.

public enum PlySource
{
    DxfSvg,
    FlattenedSurface,
}

public sealed class PlyBook
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public List<Ply> Plies { get; set; } = [];
}

public sealed class Ply
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PlyBookId { get; set; }

    public int Sequence { get; set; }

    public string Name { get; set; } = string.Empty;

    public string MaterialName { get; set; } = string.Empty;

    public double FiberOrientationDegrees { get; set; }

    public PlySource Source { get; set; }

    public string? SourcePath { get; set; }

    public Guid? FlatPatternId { get; set; }
}

public sealed class FlatPattern
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public List<Contour2D> Contours { get; set; } = [];
}

public sealed class Contour2D
{
    public bool Closed { get; set; } = true;

    public List<Point2D> Points { get; set; } = [];
}

public sealed class Point2D
{
    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class NestLayout
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public double SheetWidth { get; set; }

    public double SheetLength { get; set; }

    public List<NestPlacement> Placements { get; set; } = [];
}

public sealed class NestPlacement
{
    public Guid FlatPatternId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double RotationDegrees { get; set; }
}
