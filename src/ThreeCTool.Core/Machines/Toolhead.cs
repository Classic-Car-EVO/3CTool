namespace ThreeCTool.Core.Machines;

public enum ToolheadType
{
    Spindle,
    Fdm,
    Knife,
}

public enum MountType
{
    Fixed,
    Kinematic,
}

[Flags]
public enum OperatorConfirmations
{
    None = 0,
    HeadMounted = 1,
    CutterRemoved = 2,
}

public enum KnifeType
{
    Drag,
    Tangential,
    OscillatingTangential,
}

public sealed class Toolhead
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid MachineProfileId { get; set; }

    public string Name { get; set; } = string.Empty;

    public ToolheadType Type { get; set; }

    public MountType Mount { get; set; }

    /// <summary>Head reference point relative to the spindle tool tip (mm).</summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double OffsetZ { get; set; }

    public DateTimeOffset? OffsetCalibratedAt { get; set; }

    /// <summary>Uses the shared B axis (FDM extruder, knife blade rotation).</summary>
    public bool UsesBAxis { get; set; }

    /// <summary>Collision solids relative to the head reference point (nozzle tip / knife tip).</summary>
    public List<SimpleSolid> CollisionModel { get; set; } = [];

    public OperatorConfirmations RequiredConfirmations { get; set; }

    public FdmHeadProfile? Fdm { get; set; }

    public KnifeHeadProfile? Knife { get; set; }
}

public sealed class FdmHeadProfile
{
    public double NozzleDiameter { get; set; } = 0.4;

    public double MaxTemperature { get; set; } = 300;

    /// <summary>mm³/s.</summary>
    public double MaxVolumetricFlow { get; set; } = 15;

    public double FilamentDiameter { get; set; } = 1.75;

    /// <summary>B-axis units per mm of filament (FluidNC B steps/unit is fixed).</summary>
    public double BUnitsPerMmFilament { get; set; } = 1;

    /// <summary>Seconds of extra extrusion per mm/s of velocity change.</summary>
    public double PressureAdvance { get; set; } = 0.04;

    public string KlipperHeater { get; set; } = "extruder";

    public string KlipperHotendFan { get; set; } = "heater_fan hotend_fan";

    /// <summary>Optional Klipper bed heater object; null = unheated steel table.</summary>
    public string? KlipperBedHeater { get; set; }

    /// <summary>FluidNC <c>M67 E</c> output index for the part-cooling fan.</summary>
    public int PartFanOutput { get; set; }

    public double NozzleProbeSeekFeed { get; set; } = 100;

    public double NozzleProbeLatchFeed { get; set; } = 20;
}

/// <summary>Provision for the future knife head; no algorithms use it yet.</summary>
public sealed class KnifeHeadProfile
{
    public KnifeType? Type { get; set; }

    /// <summary>Drag knife: distance from blade tip to rotation axis.</summary>
    public double BladeOffset { get; set; }

    public double BUnitsPerDegree { get; set; } = 1;

    public string? OscillationOnCode { get; set; }

    public string? OscillationOffCode { get; set; }

    /// <summary>Direction changes above this angle lift, rotate and plunge the blade.</summary>
    public double CornerLiftAngle { get; set; } = 30;
}
