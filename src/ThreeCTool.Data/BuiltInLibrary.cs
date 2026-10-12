using System.Security.Cryptography;
using System.Text;

using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Printing;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Data;

/// <summary>
/// Built-in library content with typical values. Users can edit or delete these records;
/// they are only inserted when <see cref="Version"/> increases.
/// </summary>
public static class BuiltInLibrary
{
    public const int Version = 1;

    private const double ColletNutLength = 25;

    // Chip load table diameters (mm) shared by all materials below.
    private static readonly double[] ChipLoadDiameters = [1, 3.175, 6, 8, 12];

    /// <summary>Stable id for a built-in record so later library versions can recognise it.</summary>
    public static Guid IdFor(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("3CTool.builtin:" + key));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    public static MachineProfile DefaultMachine()
    {
        var machineId = IdFor("machine:default");
        return new MachineProfile
        {
            Id = machineId,
            Name = "3C mill (default, not yet imported from FluidNC)",
            Axes =
            [
                LinearAxis(AxisName.X, travel: 1300, motors: 2, homingCycle: 2),
                LinearAxis(AxisName.Y, travel: 750, motors: 1, homingCycle: 2),
                LinearAxis(AxisName.Z, travel: 600, motors: 2, homingCycle: 1, homePositive: true),
                new AxisConfig { Axis = AxisName.A, StepsPerUnit = 44.444, MaxRatePerMinute = 7200, AccelerationPerSecond2 = 200 },
                new AxisConfig { Axis = AxisName.B, StepsPerUnit = 400, MaxRatePerMinute = 3000, AccelerationPerSecond2 = 1000 },
            ],
            Spindle = new SpindleConfig
            {
                PowerCurve = SpindleConfig.ConstantTorqueCurve(1500, 30000, 2200, 24000),
            },
            Toolheads =
            [
                new Toolhead
                {
                    Id = IdFor("toolhead:spindle"),
                    MachineProfileId = machineId,
                    Name = "Spindle 2.2 kW",
                    Type = ToolheadType.Spindle,
                    Mount = MountType.Fixed,

                    // Relative to the collet nut face; the nut itself is the collets' holder segment.
                    CollisionModel =
                    [
                        new SimpleSolid { Name = "Spindle body", Shape = SolidShape.Cylinder, Radius = 37.5, BottomZ = ColletNutLength, Height = 50 },
                    ],
                },
                new Toolhead
                {
                    Id = IdFor("toolhead:fdm"),
                    MachineProfileId = machineId,
                    Name = "FDM head (BIQU/BTT USB-C)",
                    Type = ToolheadType.Fdm,
                    Mount = MountType.Kinematic,
                    UsesBAxis = true,
                    RequiredConfirmations = OperatorConfirmations.HeadMounted | OperatorConfirmations.CutterRemoved,
                    Fdm = new FdmHeadProfile(),
                    CollisionModel =
                    [
                        new SimpleSolid { Name = "Nozzle", Shape = SolidShape.Cylinder, Radius = 3, Height = 5 },
                        new SimpleSolid { Name = "Heater block", Shape = SolidShape.Box, BottomZ = 5, SizeX = 20, SizeY = 16, Height = 12 },
                        new SimpleSolid { Name = "Fan duct", Shape = SolidShape.Box, BottomZ = 3, SizeX = 50, SizeY = 50, Height = 40 },
                        new SimpleSolid { Name = "Mount", Shape = SolidShape.Box, BottomZ = 45, SizeX = 70, SizeY = 60, Height = 60 },
                    ],
                },
            ],
            Collets =
            [
                Collet(machineId, "Collet 1/8\"", Units.ToMillimeters(0.125, LengthUnit.Inch)),
                Collet(machineId, "Collet 1/4\"", Units.ToMillimeters(0.25, LengthUnit.Inch)),
                Collet(machineId, "Collet 6 mm", 6),
                Collet(machineId, "Collet 8 mm", 8),
            ],
        };
    }

    public static IReadOnlyList<Material> Materials() =>
    [
        Material("softwood", "Softwood (pine, spruce)", MaterialCategory.Wood, "Janka ~1.7–2.5 kN", kc: 30, scale: 2.0, doc: 1.0, woc: 1.0, vc: (300, 1000), [0.02, 0.08, 0.18, 0.22, 0.28]),
        Material("hardwood", "Hardwood (oak, beech, walnut)", MaterialCategory.Wood, "Janka ~4.5–6 kN", kc: 60, scale: 1.5, doc: 1.0, woc: 1.0, vc: (250, 800), [0.015, 0.06, 0.14, 0.18, 0.23]),
        Material("mdf", "MDF", MaterialCategory.WoodComposite, null, kc: 50, scale: 1.5, doc: 1.0, woc: 1.0, vc: (250, 900), [0.02, 0.08, 0.17, 0.22, 0.28]),
        Material("plywood", "Plywood", MaterialCategory.WoodComposite, null, kc: 50, scale: 2.0, doc: 1.0, woc: 1.0, vc: (250, 900), [0.018, 0.07, 0.15, 0.2, 0.25]),
        Material("hdpe", "HDPE", MaterialCategory.Plastic, "Shore D 60–70", kc: 100, scale: 1.5, doc: 1.0, woc: 1.0, vc: (150, 600), [0.025, 0.1, 0.2, 0.25, 0.3], "Single-flute (O-flute) tools give the best chip evacuation."),
        Material("pmma", "Acrylic (PMMA)", MaterialCategory.Plastic, "Rockwell M 90–100", kc: 250, scale: 0.75, doc: 0.75, woc: 1.0, vc: (150, 500), [0.015, 0.05, 0.12, 0.15, 0.2], "Use O-flute tools; too low chip load melts the edge."),
        Material("pom", "POM (Delrin)", MaterialCategory.Plastic, "Rockwell M 90", kc: 200, scale: 1.0, doc: 1.0, woc: 1.0, vc: (150, 500), [0.02, 0.08, 0.15, 0.2, 0.25]),
        Material("pa", "Nylon (PA)", MaterialCategory.Plastic, "Rockwell R 110–120", kc: 180, scale: 1.5, doc: 1.0, woc: 1.0, vc: (150, 500), [0.02, 0.08, 0.15, 0.2, 0.25]),
        Material("pvc", "PVC (rigid / foam)", MaterialCategory.Plastic, null, kc: 180, scale: 1.5, doc: 1.0, woc: 1.0, vc: (150, 600), [0.02, 0.08, 0.17, 0.2, 0.25]),
        Material("pu-board", "Modeling foam / tooling board (PU)", MaterialCategory.Foam, "Density 0.1–0.7 g/cm³", kc: 40, scale: 1.0, doc: 1.5, woc: 1.0, vc: (200, 800), [0.03, 0.1, 0.2, 0.25, 0.3]),
        Material("eps-xps", "EPS / XPS foam", MaterialCategory.Foam, "Density 15–40 kg/m³", kc: 5, scale: 4.0, doc: 2.0, woc: 1.0, vc: (200, 1000), [0.05, 0.15, 0.3, 0.35, 0.45]),
        Material("al-6061", "Aluminium 6061 / 6082", MaterialCategory.Aluminium, "HB 95", kc: 700, scale: 0.75, doc: 0.5, woc: 0.5, vc: (150, 400), [0.005, 0.02, 0.04, 0.05, 0.07], "Use 1–3 flute tools for aluminium and air blast or mist."),
        Material("al-7075", "Aluminium 7075", MaterialCategory.Aluminium, "HB 150", kc: 800, scale: 0.75, doc: 0.5, woc: 0.5, vc: (150, 450), [0.005, 0.02, 0.045, 0.055, 0.075], "Use 1–3 flute tools for aluminium and air blast or mist."),
        Material("cf-g10", "Carbon fibre / G10 (composites)", MaterialCategory.Composite, null, kc: 500, scale: 1.0, doc: 0.5, woc: 0.5, vc: (100, 250), [0.005, 0.02, 0.04, 0.05, 0.06], "Use diamond-cut or compression carbide; dust extraction required (hazardous dust).", hss: false),
    ];

    public static IReadOnlyList<Filament> Filaments() =>
    [
        Filament("pla", "PLA", FilamentType.Pla, nozzle: (190, 220), bed: (0, 60), density: 1.24, flow: 12, fan: (100, 100), shrink: 0.3),
        Filament("petg", "PETG", FilamentType.Petg, nozzle: (230, 250), bed: (70, 85), density: 1.27, flow: 10, fan: (30, 50), shrink: 0.4),
        Filament("abs", "ABS", FilamentType.Abs, nozzle: (240, 260), bed: (95, 110), density: 1.04, flow: 12, fan: (0, 15), shrink: 0.7, notes: "Needs an enclosure and heated bed; emits fumes."),
        Filament("asa", "ASA", FilamentType.Asa, nozzle: (240, 260), bed: (90, 110), density: 1.07, flow: 12, fan: (0, 20), shrink: 0.5, notes: "Needs an enclosure and heated bed; emits fumes."),
        Filament("tpu", "TPU 95A", FilamentType.Tpu, nozzle: (210, 230), bed: (30, 60), density: 1.21, flow: 3.5, fan: (50, 100), shrink: 0.3, notes: "Low retraction, slow speeds."),
        Filament("pa", "PA (Nylon)", FilamentType.Pa, nozzle: (250, 280), bed: (70, 90), density: 1.14, flow: 8, fan: (0, 30), shrink: 1.5, notes: "Hygroscopic: dry before use."),
        Filament("pla-cf", "PLA-CF", FilamentType.PlaCf, nozzle: (210, 230), bed: (50, 60), density: 1.29, flow: 10, fan: (100, 100), shrink: 0.2, abrasive: true),
        Filament("petg-cf", "PETG-CF", FilamentType.PetgCf, nozzle: (240, 260), bed: (70, 85), density: 1.30, flow: 9, fan: (30, 50), shrink: 0.3, abrasive: true),
        Filament("pa-cf", "PA-CF", FilamentType.PaCf, nozzle: (260, 290), bed: (80, 100), density: 1.20, flow: 8, fan: (0, 20), shrink: 0.4, abrasive: true, notes: "Hygroscopic: dry before use."),
        Filament("pc", "PC", FilamentType.Pc, nozzle: (260, 300), bed: (100, 120), density: 1.20, flow: 8, fan: (0, 20), shrink: 0.7, notes: "Needs an enclosure and heated bed."),
    ];

    public static IReadOnlyList<PrintProfile> PrintProfiles() =>
    [
        new PrintProfile { Id = IdFor("print:0.20-standard"), Name = "0.20 mm Standard (0.4 nozzle)", IsBuiltIn = true },
        new PrintProfile
        {
            Id = IdFor("print:0.12-fine"),
            Name = "0.12 mm Fine (0.4 nozzle)",
            LayerHeight = 0.12,
            FirstLayerHeight = 0.2,
            LineWidth = 0.42,
            TopLayers = 7,
            BottomLayers = 5,
            PerimeterSpeed = 35,
            ExternalPerimeterSpeed = 25,
            InfillSpeed = 50,
            IsBuiltIn = true,
        },
        new PrintProfile
        {
            Id = IdFor("print:0.28-draft"),
            Name = "0.28 mm Draft (0.4 nozzle)",
            LayerHeight = 0.28,
            FirstLayerHeight = 0.3,
            LineWidth = 0.5,
            Perimeters = 2,
            TopLayers = 4,
            BottomLayers = 3,
            InfillDensity = 15,
            Infill = InfillPattern.Grid,
            PerimeterSpeed = 55,
            InfillSpeed = 70,
            IsBuiltIn = true,
        },
    ];

    private static AxisConfig LinearAxis(AxisName axis, double travel, int motors, int homingCycle, bool homePositive = false) => new()
    {
        Axis = axis,
        StepsPerUnit = 100,
        MaxRatePerMinute = 6000,
        AccelerationPerSecond2 = 300,
        MaxTravel = travel,
        SoftLimits = true,
        MotorCount = motors,
        HomingCycle = homingCycle,
        HomingPositiveDirection = homePositive,
    };

    private static Collet Collet(Guid machineId, string name, double shank) => new()
    {
        Id = IdFor("collet:" + name),
        MachineProfileId = machineId,
        Name = name,
        ShankDiameter = shank,
        Holder = [new HolderSegment { Diameter = 20, Length = ColletNutLength }],
    };

    private static Material Material(
        string key,
        string name,
        MaterialCategory category,
        string? hardness,
        double kc,
        double scale,
        double doc,
        double woc,
        (double Min, double Max) vc,
        double[] chipLoads,
        string? notes = null,
        bool hss = true)
    {
        var data = new List<CuttingData> { Cutting(ToolMaterial.Carbide, vc.Min, vc.Max, chipLoads) };
        if (hss)
        {
            // Typical HSS: ~35 % of the carbide surface speed and ~80 % of the chip load.
            data.Add(Cutting(ToolMaterial.Hss, vc.Min * 0.35, vc.Max * 0.35, [.. chipLoads.Select(c => c * 0.8)]));
        }

        return new Material
        {
            Id = IdFor("material:" + key),
            Name = name,
            Category = category,
            Hardness = hardness,
            SpecificCuttingForce = kc,
            KienzleExponent = category == MaterialCategory.Aluminium ? 0.23 : 0.25,
            ToleranceScale = scale,
            MaxDocFactor = doc,
            MaxWocFactor = woc,
            CuttingData = data,
            IsBuiltIn = true,
            Notes = notes,
        };
    }

    private static CuttingData Cutting(ToolMaterial toolMaterial, double vcMin, double vcMax, double[] chipLoads) => new()
    {
        ToolMaterial = toolMaterial,
        SurfaceSpeedMin = vcMin,
        SurfaceSpeedMax = vcMax,
        ChipLoad = [.. ChipLoadDiameters.Zip(chipLoads, (d, c) => new ChipLoadPoint { Diameter = d, ChipLoad = c })],
    };

    private static Filament Filament(
        string key,
        string name,
        FilamentType type,
        (double Min, double Max) nozzle,
        (double Min, double Max) bed,
        double density,
        double flow,
        (double Min, double Max) fan,
        double shrink,
        bool abrasive = false,
        string? notes = null) => new()
    {
        Id = IdFor("filament:" + key),
        Name = name,
        Type = type,
        NozzleTemperatureMin = nozzle.Min,
        NozzleTemperatureMax = nozzle.Max,
        BedTemperatureMin = bed.Min,
        BedTemperatureMax = bed.Max,
        Density = density,
        MaxVolumetricSpeed = flow,
        FanMinPercent = fan.Min,
        FanMaxPercent = fan.Max,
        ShrinkagePercent = shrink,
        Abrasive = abrasive,
        Notes = notes,
        IsBuiltIn = true,
    };
}
