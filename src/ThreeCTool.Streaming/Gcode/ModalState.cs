namespace ThreeCTool.Streaming.Gcode;

public enum MotionMode
{
    Rapid,
    Linear,
    ArcClockwise,
    ArcCounterClockwise,
    Probe,
    Cancel,
}

/// <summary>
/// Modal state reconstructed by scanning a file (section 11.3 step 6). Positions are in mm in the active work
/// coordinate system including G92, except axes flagged in <see cref="MachineAxes"/> (last moved with G53).
/// <see cref="double.NaN"/> means unknown (after probing, G28/G30, homing or a WCS change).
/// </summary>
public sealed class ModalState
{
    public const int AxisCount = 5;
    public const string AxisLetters = "XYZAB";

    public MotionMode Motion { get; set; } = MotionMode.Rapid;

    public int Plane { get; set; } = 17;

    public bool Inches { get; set; }

    public bool Incremental { get; set; }

    /// <summary>G90.1: arc centers absolute. Default G91.1.</summary>
    public bool ArcAbsolute { get; set; }

    public bool InverseTime { get; set; }

    public int Wcs { get; set; } = 54;

    /// <summary>Last F word in file units (per minute; with G93 the per-block value).</summary>
    public double Feed { get; set; }

    public double SpindleSpeed { get; set; }

    /// <summary>3 = CW, 4 = CCW, 5 = off.</summary>
    public int Spindle { get; set; } = 5;

    public bool Mist { get; set; }

    public bool Flood { get; set; }

    public int Tool { get; set; }

    public SortedDictionary<int, bool> DigitalOutputs { get; } = [];

    public SortedDictionary<int, double> AnalogOutputs { get; } = [];

    /// <summary>Heater targets set by <c>(MC:HEAT ...)</c> directives, °C.</summary>
    public SortedDictionary<string, double> Heaters { get; } = new(StringComparer.Ordinal);

    public double[] Position { get; } = [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN];

    public bool[] MachineAxes { get; } = new bool[AxisCount];

    /// <summary>Accumulated G92 offset per axis (mm): un-offset coordinate = position + offset.</summary>
    public double[] G92Offset { get; } = new double[AxisCount];

    public bool[] G92Active { get; } = new bool[AxisCount];

    /// <summary>Last G43.1 value (mm); 0 after G49; null if the file never set it or set it from an expression.</summary>
    public double? ToolLengthOffset { get; set; }

    /// <summary>Last plain comment, used as the M0 prompt.</summary>
    public string? LastComment { get; set; }

    public ModalState Clone()
    {
        var clone = new ModalState
        {
            Motion = Motion,
            Plane = Plane,
            Inches = Inches,
            Incremental = Incremental,
            ArcAbsolute = ArcAbsolute,
            InverseTime = InverseTime,
            Wcs = Wcs,
            Feed = Feed,
            SpindleSpeed = SpindleSpeed,
            Spindle = Spindle,
            Mist = Mist,
            Flood = Flood,
            Tool = Tool,
            ToolLengthOffset = ToolLengthOffset,
            LastComment = LastComment,
        };
        foreach (var (k, v) in DigitalOutputs)
        {
            clone.DigitalOutputs[k] = v;
        }

        foreach (var (k, v) in AnalogOutputs)
        {
            clone.AnalogOutputs[k] = v;
        }

        foreach (var (k, v) in Heaters)
        {
            clone.Heaters[k] = v;
        }

        Position.CopyTo(clone.Position, 0);
        MachineAxes.CopyTo(clone.MachineAxes, 0);
        G92Offset.CopyTo(clone.G92Offset, 0);
        G92Active.CopyTo(clone.G92Active, 0);
        return clone;
    }
}
