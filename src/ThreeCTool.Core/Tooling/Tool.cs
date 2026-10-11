namespace ThreeCTool.Core.Tooling;

public enum ToolType
{
    FlatEnd,
    BallEnd,
    BullNose,
    VBit,
    TaperedBall,
    Chamfer,
}

public enum ToolMaterial
{
    Carbide,
    Hss,
}

public enum ToolStatus
{
    InStock,
    Worn,
    Broken,
    Retired,
}

/// <summary>Cutting tool. All dimensions in mm, angles in degrees.</summary>
public sealed class Tool
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public ToolType Type { get; set; }

    public double Diameter { get; set; }

    /// <summary>Bull nose corner radius; ball end = Diameter / 2.</summary>
    public double CornerRadius { get; set; }

    /// <summary>Included tip angle for V-bit and chamfer mill.</summary>
    public double TipAngle { get; set; }

    /// <summary>Taper half-angle per side for tapered ball mills.</summary>
    public double TaperAngle { get; set; }

    /// <summary>V-bit flat tip or tapered-ball tip diameter.</summary>
    public double TipDiameter { get; set; }

    public int FluteCount { get; set; } = 2;

    public double FluteLength { get; set; }

    public double ShankDiameter { get; set; }

    public double OverallLength { get; set; }

    /// <summary>Length protruding from the collet nut.</summary>
    public double Stickout { get; set; }

    public ToolMaterial Material { get; set; } = ToolMaterial.Carbide;

    public string? Coating { get; set; }

    public double HelixAngle { get; set; } = 30;

    public double? MaxDepthOfCut { get; set; }

    public ToolStatus Status { get; set; } = ToolStatus.InStock;

    public string? Manufacturer { get; set; }

    public string? ProductCode { get; set; }

    public string? Notes { get; set; }
}
