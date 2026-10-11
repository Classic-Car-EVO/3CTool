namespace ThreeCTool.Core;

public enum SolidShape
{
    Box,
    Cylinder,
}

/// <summary>Box or Z-axis cylinder used for keep-out zones and toolhead collision models.</summary>
public sealed class SimpleSolid
{
    public string Name { get; set; } = string.Empty;

    public SolidShape Shape { get; set; }

    public double CenterX { get; set; }

    public double CenterY { get; set; }

    public double BottomZ { get; set; }

    public double Height { get; set; }

    /// <summary>Box only.</summary>
    public double SizeX { get; set; }

    /// <summary>Box only.</summary>
    public double SizeY { get; set; }

    /// <summary>Cylinder only.</summary>
    public double Radius { get; set; }
}
