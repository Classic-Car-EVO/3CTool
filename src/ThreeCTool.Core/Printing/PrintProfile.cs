namespace ThreeCTool.Core.Printing;

public enum SeamPosition
{
    Aligned,
    Nearest,
    Rear,
    Random,
}

public enum InfillPattern
{
    Rectilinear,
    Grid,
    Gyroid,
    Honeycomb,
}

public enum SupportMode
{
    None,
    Normal,
    Tree,
}

public enum AdhesionType
{
    None,
    Skirt,
    Brim,
    Raft,
}

/// <summary>3C Tool-native print settings. Lengths in mm, speeds in mm/s.</summary>
public sealed class PrintProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public double NozzleDiameter { get; set; } = 0.4;

    public double LayerHeight { get; set; } = 0.2;

    public double FirstLayerHeight { get; set; } = 0.25;

    public bool AdaptiveLayerHeight { get; set; }

    public double MinLayerHeight { get; set; } = 0.08;

    public double MaxLayerHeight { get; set; } = 0.3;

    public double LineWidth { get; set; } = 0.45;

    public double FirstLayerLineWidth { get; set; } = 0.5;

    public int Perimeters { get; set; } = 3;

    public SeamPosition Seam { get; set; } = SeamPosition.Aligned;

    public int TopLayers { get; set; } = 5;

    public int BottomLayers { get; set; } = 4;

    public InfillPattern Infill { get; set; } = InfillPattern.Gyroid;

    /// <summary>0–100 %.</summary>
    public double InfillDensity { get; set; } = 20;

    public double ExtrusionMultiplier { get; set; } = 1;

    public double PerimeterSpeed { get; set; } = 45;

    public double ExternalPerimeterSpeed { get; set; } = 30;

    public double InfillSpeed { get; set; } = 60;

    public double TopSolidSpeed { get; set; } = 35;

    public double SupportSpeed { get; set; } = 50;

    public double BridgeSpeed { get; set; } = 25;

    public double FirstLayerSpeed { get; set; } = 20;

    public double TravelSpeed { get; set; } = 120;

    public double RetractLength { get; set; } = 0.8;

    public double RetractSpeed { get; set; } = 35;

    public double DeretractSpeed { get; set; } = 25;

    /// <summary>Travel moves shorter than this do not retract.</summary>
    public double RetractMinTravel { get; set; } = 1.5;

    public double WipeDistance { get; set; } = 1;

    public double ZHop { get; set; } = 0.2;

    public SupportMode Support { get; set; } = SupportMode.None;

    /// <summary>Overhangs steeper than this angle from vertical get support.</summary>
    public double SupportOverhangAngle { get; set; } = 50;

    public int SupportInterfaceLayers { get; set; } = 2;

    public double SupportZDistance { get; set; } = 0.2;

    public double SupportXyDistance { get; set; } = 0.6;

    public bool DetectBridges { get; set; } = true;

    public double BridgeFlowRatio { get; set; } = 0.9;

    public AdhesionType Adhesion { get; set; } = AdhesionType.Skirt;

    public int SkirtLoops { get; set; } = 2;

    public double SkirtDistance { get; set; } = 5;

    public double BrimWidth { get; set; } = 5;

    public int RaftLayers { get; set; } = 3;

    public bool Ironing { get; set; }

    public double IroningSpacing { get; set; } = 0.1;

    /// <summary>0–100 % of normal flow.</summary>
    public double IroningFlow { get; set; } = 15;

    public double IroningSpeed { get; set; } = 15;

    /// <summary>Seconds; prints slow down to reach it.</summary>
    public double MinLayerTime { get; set; } = 8;

    public bool IsBuiltIn { get; set; }
}
