namespace ThreeCTool.Core.Machines;

/// <summary>
/// Target of one planner block. Linear axes in mm, A in degrees. <see cref="Feed"/> is units/min,
/// or 1/min when <see cref="InverseTime"/> is set (G93: the block takes 1/F minutes). Rapids ignore the feed.
/// </summary>
public readonly record struct MotionBlock(double X, double Y, double Z, double A, double Feed, bool Rapid = false, bool InverseTime = false);

/// <summary>Start position of a block sequence.</summary>
public readonly record struct MotionPosition(double X, double Y, double Z, double A);

/// <summary>
/// Time estimate that mirrors the GRBL/FluidNC planner: Euclidean block length over all axes (degrees count
/// as units), per-axis rate and acceleration limits, junction-deviation cornering and a look-ahead window of
/// <see cref="MachineProfile.PlannerBlocks"/> blocks that must be able to stop at its end.
/// </summary>
public sealed class MotionPlanner
{
    /// <summary>Used when the profile has no configuration for an axis.</summary>
    public const double FallbackRate = 1000;

    public const double FallbackAcceleration = 50;

    private const double MinimumJunctionSpeed = 0;

    private readonly double[] _rates = new double[4];
    private readonly double[] _accelerations = new double[4];
    private readonly double _junctionDeviation;
    private readonly int _window;

    public MotionPlanner(MachineProfile machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        AxisName[] axes = [AxisName.X, AxisName.Y, AxisName.Z, AxisName.A];
        for (var i = 0; i < axes.Length; i++)
        {
            var config = machine.GetAxis(axes[i]);
            _rates[i] = config is { MaxRatePerMinute: > 0 } ? config.MaxRatePerMinute / 60 : FallbackRate / 60;
            _accelerations[i] = config is { AccelerationPerSecond2: > 0 } ? config.AccelerationPerSecond2 : FallbackAcceleration;
        }

        _junctionDeviation = Math.Max(0, machine.JunctionDeviation);
        _window = Math.Max(2, machine.PlannerBlocks);
    }

    /// <summary>Execution time of every block in seconds; zero-length blocks take no time.</summary>
    public double[] Durations(MotionPosition start, IReadOnlyList<MotionBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var count = blocks.Count;
        var length = new double[count];
        var unit = new (double X, double Y, double Z, double A)[count];
        var nominal = new double[count];
        var accel = new double[count];
        var maxEntry = new double[count];

        var p = start;
        var previous = -1;
        for (var i = 0; i < count; i++)
        {
            var b = blocks[i];
            var d = (X: b.X - p.X, Y: b.Y - p.Y, Z: b.Z - p.Z, A: b.A - p.A);
            p = new MotionPosition(b.X, b.Y, b.Z, b.A);
            var l = Math.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z) + (d.A * d.A));
            if (l < 1e-9)
            {
                continue;
            }

            length[i] = l;
            var u = (d.X / l, d.Y / l, d.Z / l, d.A / l);
            unit[i] = u;
            accel[i] = Limit(u, _accelerations);
            var rapid = Limit(u, _rates);
            var programmed = b.Rapid ? rapid : b.InverseTime ? l * b.Feed / 60 : b.Feed / 60;
            nominal[i] = Math.Min(rapid, Math.Max(programmed, 1e-6));

            maxEntry[i] = previous < 0 ? 0 : Math.Min(Math.Min(nominal[i], nominal[previous]), Junction(unit[previous], u));
            previous = i;
        }

        var active = Enumerable.Range(0, count).Where(i => length[i] > 0).ToArray();
        var result = new double[count];
        var entry = 0.0;
        for (var k = 0; k < active.Length; k++)
        {
            var i = active[k];

            // Backward pass over the look-ahead window, which must end at rest.
            var last = Math.Min(active.Length - 1, k + _window - 1);
            var exitLimit = 0.0;
            for (var j = last; j > k; j--)
            {
                var n = active[j];
                exitLimit = Math.Min(maxEntry[n], Math.Sqrt((exitLimit * exitLimit) + (2 * accel[n] * length[n])));
            }

            var exit = Math.Min(exitLimit, Math.Sqrt((entry * entry) + (2 * accel[i] * length[i])));
            result[i] = Trapezoid(length[i], entry, exit, nominal[i], accel[i]);
            entry = exit;
        }

        return result;
    }

    /// <summary>Time for one block with the given entry, exit and cruise speeds (units/s) and acceleration.</summary>
    public static double Trapezoid(double length, double entry, double exit, double nominal, double acceleration)
    {
        entry = Math.Min(entry, nominal);
        exit = Math.Min(exit, nominal);
        var peakSquared = ((2 * acceleration * length) + (entry * entry) + (exit * exit)) / 2;
        if (peakSquared >= nominal * nominal)
        {
            var accelDistance = ((nominal * nominal) - (entry * entry)) / (2 * acceleration);
            var decelDistance = ((nominal * nominal) - (exit * exit)) / (2 * acceleration);
            var cruise = Math.Max(0, length - accelDistance - decelDistance);
            return ((nominal - entry) / acceleration) + ((nominal - exit) / acceleration) + (cruise / nominal);
        }

        var peak = Math.Sqrt(Math.Max(peakSquared, Math.Max(entry * entry, exit * exit)));
        return ((peak - entry) / acceleration) + ((peak - exit) / acceleration);
    }

    private double Junction((double X, double Y, double Z, double A) from, (double X, double Y, double Z, double A) to)
    {
        var cos = -((from.X * to.X) + (from.Y * to.Y) + (from.Z * to.Z) + (from.A * to.A));
        if (cos > 0.999999)
        {
            return MinimumJunctionSpeed;
        }

        if (cos < -0.999999)
        {
            return double.PositiveInfinity;
        }

        var j = (X: to.X - from.X, Y: to.Y - from.Y, Z: to.Z - from.Z, A: to.A - from.A);
        var jl = Math.Sqrt((j.X * j.X) + (j.Y * j.Y) + (j.Z * j.Z) + (j.A * j.A));
        var junctionAccel = Limit((j.X / jl, j.Y / jl, j.Z / jl, j.A / jl), _accelerations);
        var sinHalf = Math.Sqrt(0.5 * (1 - cos));
        var vSquared = junctionAccel * _junctionDeviation * sinHalf / (1 - sinHalf);
        return Math.Max(MinimumJunctionSpeed, Math.Sqrt(vSquared));
    }

    private static double Limit((double X, double Y, double Z, double A) u, double[] limits)
    {
        var result = double.PositiveInfinity;
        Span<double> c = [u.X, u.Y, u.Z, u.A];
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(c[i]) > 1e-12)
            {
                result = Math.Min(result, limits[i] / Math.Abs(c[i]));
            }
        }

        return result;
    }
}
