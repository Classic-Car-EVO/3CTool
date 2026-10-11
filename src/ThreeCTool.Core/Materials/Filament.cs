namespace ThreeCTool.Core.Materials;

public enum FilamentType
{
    Pla,
    Petg,
    Abs,
    Asa,
    Tpu,
    Pa,
    PlaCf,
    PetgCf,
    PaCf,
    Pc,
}

public sealed class Filament
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public FilamentType Type { get; set; }

    public double Diameter { get; set; } = 1.75;

    public double NozzleTemperatureMin { get; set; }

    public double NozzleTemperatureMax { get; set; }

    public double BedTemperatureMin { get; set; }

    public double BedTemperatureMax { get; set; }

    /// <summary>g/cm³.</summary>
    public double Density { get; set; }

    /// <summary>mm³/s.</summary>
    public double MaxVolumetricSpeed { get; set; }

    public double FanMinPercent { get; set; }

    public double FanMaxPercent { get; set; }

    /// <summary>Layers at the start of a print without part cooling.</summary>
    public int FanOffFirstLayers { get; set; } = 1;

    /// <summary>Linear shrinkage (%).</summary>
    public double ShrinkagePercent { get; set; }

    /// <summary>Abrasive filament (e.g. carbon fibre); needs a hardened nozzle.</summary>
    public bool Abrasive { get; set; }

    public bool IsBuiltIn { get; set; }

    public string? Notes { get; set; }
}
