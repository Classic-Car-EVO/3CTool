namespace ThreeCTool.Streaming.Gcode;

/// <summary>A programmed move (mm/degrees), see <see cref="ModalState.Position"/> for the coordinate frame.</summary>
public sealed class MotionSegment
{
    private const int DistanceSamples = 32;

    internal MotionSegment(int line, MotionMode mode, double[] start, double[] end, bool[] machineAxes, int plane, double[]? center, double sweep, double rotaryRadius,
        double[]? programmedEnd = null)
    {
        Line = line;
        Mode = mode;
        Start = start;
        End = end;
        ProgrammedEnd = programmedEnd ?? end;
        MachineAxes = machineAxes;
        Plane = plane;
        Center = center;
        Sweep = sweep;
        Length = ComputeLength(rotaryRadius);
    }

    public int Line { get; }

    public MotionMode Mode { get; }

    public IReadOnlyList<double> Start { get; }

    public IReadOnlyList<double> End { get; }

    /// <summary>The programmed target: <see cref="End"/>, except for probe moves, which stop at contact (End is unknown).</summary>
    public IReadOnlyList<double> ProgrammedEnd { get; }

    public IReadOnlyList<bool> MachineAxes { get; }

    public int Plane { get; }

    /// <summary>Arc center in plane coordinates (first, second plane axis), null for straight moves.</summary>
    public IReadOnlyList<double>? Center { get; }

    /// <summary>Signed arc sweep in radians (negative = clockwise).</summary>
    public double Sweep { get; }

    /// <summary>Path length in mm; rotary moves are weighted by a nominal radius.</summary>
    public double Length { get; }

    public bool IsArc => Center is not null;

    /// <summary>Plane axis indices: first, second and the helical (normal) axis.</summary>
    public static (int First, int Second, int Normal) PlaneAxes(int plane) => plane switch
    {
        18 => (2, 0, 1),
        19 => (1, 2, 0),
        _ => (0, 1, 2),
    };

    /// <summary>Point at parameter <paramref name="t"/> ∈ [0, 1] along the move.</summary>
    public double[] PointAt(double t)
    {
        var point = new double[ModalState.AxisCount];
        for (var i = 0; i < point.Length; i++)
        {
            point[i] = Start[i] + ((End[i] - Start[i]) * t);
        }

        if (Center is not null)
        {
            var (a, b, _) = PlaneAxes(Plane);
            var radius = Math.Sqrt(Square(Start[a] - Center[0]) + Square(Start[b] - Center[1]));
            var angle = Math.Atan2(Start[b] - Center[1], Start[a] - Center[0]) + (Sweep * t);
            point[a] = Center[0] + (radius * Math.Cos(angle));
            point[b] = Center[1] + (radius * Math.Sin(angle));
        }

        return point;
    }

    /// <summary>
    /// Shortest XYZ distance from a position to this move. <paramref name="work"/> is used for WCS axes and
    /// <paramref name="machine"/> for axes moved in machine coordinates. Unknown coordinates give infinity.
    /// </summary>
    public double DistanceTo(IReadOnlyList<double> work, IReadOnlyList<double> machine)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(machine);
        var target = new double[3];
        for (var i = 0; i < 3; i++)
        {
            target[i] = MachineAxes[i] ? machine[i] : work[i];
            if (double.IsNaN(target[i]) || double.IsNaN(Start[i]) || double.IsNaN(End[i]))
            {
                return double.PositiveInfinity;
            }
        }

        if (!IsArc)
        {
            return DistanceToLine(Start, End, target);
        }

        var best = double.PositiveInfinity;
        var previous = PointAt(0);
        for (var s = 1; s <= DistanceSamples; s++)
        {
            var next = PointAt(s / (double)DistanceSamples);
            best = Math.Min(best, DistanceToLine(previous, next, target));
            previous = next;
        }

        return best;
    }

    private static double DistanceToLine(IReadOnlyList<double> a, IReadOnlyList<double> b, double[] p)
    {
        double lengthSquared = 0, dot = 0;
        for (var i = 0; i < 3; i++)
        {
            lengthSquared += Square(b[i] - a[i]);
            dot += (p[i] - a[i]) * (b[i] - a[i]);
        }

        var t = lengthSquared > 0 ? Math.Clamp(dot / lengthSquared, 0, 1) : 0;
        double distance = 0;
        for (var i = 0; i < 3; i++)
        {
            distance += Square(p[i] - (a[i] + ((b[i] - a[i]) * t)));
        }

        return Math.Sqrt(distance);
    }

    private static double Square(double v) => v * v;

    private static double Delta(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? 0 : b - a;

    private double ComputeLength(double rotaryRadius)
    {
        double linear;
        if (Center is not null)
        {
            var (a, b, n) = PlaneAxes(Plane);
            var radius = Math.Sqrt(Square(Start[a] - Center[0]) + Square(Start[b] - Center[1]));
            linear = Math.Sqrt(Square(Sweep * radius) + Square(Delta(Start[n], End[n])));
        }
        else
        {
            linear = Math.Sqrt(Square(Delta(Start[0], End[0])) + Square(Delta(Start[1], End[1])) + Square(Delta(Start[2], End[2])));
        }

        var rotary = Math.Abs(Delta(Start[3], End[3])) * Math.PI / 180 * rotaryRadius;
        var extruder = Math.Abs(Delta(Start[4], End[4]));
        return Math.Max(linear, Math.Max(rotary, extruder));
    }
}
