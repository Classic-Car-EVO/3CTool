using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Core.Materials;

public enum MaterialCategory
{
    Wood,
    WoodComposite,
    Plastic,
    Foam,
    Aluminium,
    Composite,
}

public sealed class Material
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public MaterialCategory Category { get; set; }

    public string? Hardness { get; set; }

    /// <summary>Specific cutting force kc1.1 (N/mm²) at 1 mm chip thickness.</summary>
    public double SpecificCuttingForce { get; set; }

    /// <summary>Kienzle exponent mc.</summary>
    public double KienzleExponent { get; set; } = 0.25;

    /// <summary>Multiplier applied to the baseline project tolerances (&gt;1 coarser, &lt;1 finer).</summary>
    public double ToleranceScale { get; set; } = 1;

    /// <summary>Maximum axial depth of cut as a multiple of tool diameter.</summary>
    public double MaxDocFactor { get; set; } = 1;

    /// <summary>Maximum radial width of cut as a multiple of tool diameter.</summary>
    public double MaxWocFactor { get; set; } = 1;

    public List<CuttingData> CuttingData { get; set; } = [];

    public bool IsBuiltIn { get; set; }

    public string? Notes { get; set; }
}

public sealed class CuttingData
{
    public ToolMaterial ToolMaterial { get; set; }

    /// <summary>Surface speed range (m/min).</summary>
    public double SurfaceSpeedMin { get; set; }

    public double SurfaceSpeedMax { get; set; }

    /// <summary>Chip load per tooth (mm) vs. tool diameter (mm).</summary>
    public List<ChipLoadPoint> ChipLoad { get; set; } = [];
}

public sealed class ChipLoadPoint
{
    public double Diameter { get; set; }

    public double ChipLoad { get; set; }
}
