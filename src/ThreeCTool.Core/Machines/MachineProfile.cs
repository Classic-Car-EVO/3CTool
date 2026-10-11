namespace ThreeCTool.Core.Machines;

public enum AxisName
{
    X,
    Y,
    Z,
    A,
    B,
}

public enum ProbeMethod
{
    ToolSetter,
    TouchPlate,
}

public enum CoolantMode
{
    None,
    Mist,
    Flood,
}

public enum TransportKind
{
    Serial,
    Tcp,
}

public sealed class MachineProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public ControllerInfo Controller { get; set; } = new();

    /// <summary>Imported from FluidNC <c>config.yaml</c>; read-only in 3C Tool.</summary>
    public List<AxisConfig> Axes { get; set; } = [];

    public SpindleConfig Spindle { get; set; } = new();

    public RotaryAxisConfig RotaryAxis { get; set; } = new();

    /// <summary>Machine-coordinate Z for safe retracts (<c>G53</c>).</summary>
    public double SafeZMachine { get; set; } = -5;

    public ProbeConfig Probe { get; set; } = new();

    public CoolantConfig Coolant { get; set; } = new();

    public bool ArcFitting { get; set; } = true;

    public double ArcFittingTolerance { get; set; } = 0.005;

    public ConnectionSettings Connection { get; set; } = new();

    public List<Toolhead> Toolheads { get; set; } = [];

    public List<Collet> Collets { get; set; } = [];

    public AxisConfig? GetAxis(AxisName axis) => Axes.Find(a => a.Axis == axis);
}

public sealed class ControllerInfo
{
    public string? FluidNcVersion { get; set; }

    public DateTimeOffset? ConfigImportedAt { get; set; }

    public string? ConfigHash { get; set; }
}

/// <summary>Linear axes use mm, rotary axes use degrees.</summary>
public sealed class AxisConfig
{
    public AxisName Axis { get; set; }

    public double StepsPerUnit { get; set; }

    public double MaxRatePerMinute { get; set; }

    public double AccelerationPerSecond2 { get; set; }

    public double MaxTravel { get; set; }

    public bool SoftLimits { get; set; }

    public bool DirectionInverted { get; set; }

    public int MotorCount { get; set; } = 1;

    /// <summary>FluidNC homing cycle number; 0 = not homed.</summary>
    public int HomingCycle { get; set; }

    public bool HomingPositiveDirection { get; set; }

    public double HomingMachinePosition { get; set; }
}

public sealed class SpindleConfig
{
    public double MinRpm { get; set; } = 1500;

    public double MaxRpm { get; set; } = 30000;

    public double RatedPowerW { get; set; } = 2200;

    public double RatedRpm { get; set; } = 24000;

    public bool ManualDial { get; set; } = true;

    /// <summary>Power (W) vs. RPM, linearly interpolated. Empty = constant torque up to <see cref="RatedRpm"/>.</summary>
    public List<PowerCurvePoint> PowerCurve { get; set; } = [];

    public static List<PowerCurvePoint> ConstantTorqueCurve(double minRpm, double maxRpm, double ratedPowerW, double ratedRpm) =>
    [
        new() { Rpm = minRpm, PowerW = ratedPowerW * Math.Min(minRpm, ratedRpm) / ratedRpm },
        new() { Rpm = ratedRpm, PowerW = ratedPowerW },
        new() { Rpm = maxRpm, PowerW = ratedPowerW * Math.Min(maxRpm, ratedRpm) / ratedRpm },
    ];

    public double PowerAt(double rpm)
    {
        if (PowerCurve.Count == 0)
        {
            return RatedPowerW * Math.Min(rpm, RatedRpm) / RatedRpm;
        }

        var points = PowerCurve.OrderBy(p => p.Rpm).ToList();
        if (rpm <= points[0].Rpm)
        {
            return points[0].PowerW;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (rpm <= points[i].Rpm)
            {
                var a = points[i - 1];
                var b = points[i];
                var t = (rpm - a.Rpm) / (b.Rpm - a.Rpm);
                return a.PowerW + (t * (b.PowerW - a.PowerW));
            }
        }

        return points[^1].PowerW;
    }
}

public sealed class PowerCurvePoint
{
    public double Rpm { get; set; }

    public double PowerW { get; set; }
}

/// <summary>A axis rotates around X. Centre position depends on the adjustable centre-height setting.</summary>
public sealed class RotaryAxisConfig
{
    public double MaxDiameter { get; set; } = 400;

    public double MaxLength { get; set; } = 1000;

    public double MinCenterHeight { get; set; } = 160;

    public double MaxCenterHeight { get; set; } = 250;

    /// <summary>Calibrated rotation-axis positions (machine coordinates), one per centre-height setting.</summary>
    public List<RotaryCenter> Centers { get; set; } = [];
}

public sealed class RotaryCenter
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Axis height above the machine table.</summary>
    public double CenterHeight { get; set; }

    public double CenterY { get; set; }

    public double CenterZ { get; set; }
}

public sealed class ProbeConfig
{
    public ProbeMethod DefaultMethod { get; set; } = ProbeMethod.ToolSetter;

    /// <summary>Tool setter position in machine coordinates.</summary>
    public double ToolSetterX { get; set; }

    public double ToolSetterY { get; set; }

    /// <summary>Machine Z from which the setter probe starts.</summary>
    public double ToolSetterStartZ { get; set; } = -5;

    public double TouchPlateThickness { get; set; } = 10;

    public double SeekFeed { get; set; } = 200;

    public double LatchFeed { get; set; } = 25;

    public double MaxDistance { get; set; } = 80;

    public double Retract { get; set; } = 2;
}

public sealed class CoolantConfig
{
    public CoolantMode Mode { get; set; } = CoolantMode.None;

    /// <summary>M-code that switches dust extraction on (e.g. <c>M8</c>, <c>M64 P1</c>); null = none.</summary>
    public string? DustExtractionOnCode { get; set; }

    public string? DustExtractionOffCode { get; set; }
}

public sealed class ConnectionSettings
{
    public TransportKind Transport { get; set; } = TransportKind.Serial;

    public string SerialPort { get; set; } = "/dev/ttyUSB0";

    public int BaudRate { get; set; } = 115200;

    public string Host { get; set; } = "fluidnc.local";

    public int TcpPort { get; set; } = 23;
}
