using ThreeCTool.Core.Projects;

namespace ThreeCTool.Toolpaths;

/// <summary>Feed class of a move; actual feed rates are resolved later by the feeds engine / post.</summary>
public enum MoveKind : byte
{
    Rapid,
    Cut,
    Plunge,
    Ramp,
    LeadIn,
    LeadOut,
    Link,
}

/// <summary>Move to a target in WCS (mm, degrees). The start is the previous move's target.</summary>
public readonly record struct ToolpathMove(MoveKind Kind, double X, double Y, double Z, double A = 0)
{
    public P3 Position => new(X, Y, Z);
}

/// <summary>Double-precision 3D point / vector used for toolpath maths.</summary>
public readonly record struct P3(double X, double Y, double Z)
{
    public static P3 operator +(P3 a, P3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static P3 operator -(P3 a, P3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static P3 operator *(P3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    public double LengthXY => Math.Sqrt((X * X) + (Y * Y));

    public static double Distance(P3 a, P3 b) => (a - b).Length;

    public static P3 Lerp(P3 a, P3 b, double t) => a + ((b - a) * t);

    public static P3 Add(P3 a, P3 b) => a + b;

    public static P3 Subtract(P3 a, P3 b) => a - b;

    public static P3 Multiply(P3 a, double s) => a * s;
}

/// <summary>Toolpath bounds in WCS (A in degrees).</summary>
public readonly record struct ToolpathBounds(P3 Min, P3 Max, double MinA, double MaxA)
{
    public P3 Size => Max - Min;
}

/// <summary>Generated toolpath of one operation, in the WCS of its setup (tool along +Z at the move's A).</summary>
public sealed class Toolpath
{
    public Guid OperationId { get; init; }

    public StrategyType Strategy { get; init; }

    public Guid ToolId { get; init; }

    public SetupFrame Frame { get; init; } = SetupFrame.Identity;

    /// <summary>Z of the clearance plane, or the safe radius for simultaneous rotary toolpaths.</summary>
    public double Clearance { get; init; }

    public List<ToolpathMove> Moves { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public bool UsesRotary => Frame.Orientation == SetupOrientation.SimultaneousRotary;

    public int MoveCount => Moves.Count;

    /// <summary>Length of all feed moves (mm); rotary moves use the arc length at the mean radius.</summary>
    public double CutLength => Length(m => m != MoveKind.Rapid);

    public double RapidLength => Length(m => m == MoveKind.Rapid);

    public ToolpathBounds Bounds
    {
        get
        {
            if (Moves.Count == 0)
            {
                return default;
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, minA = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue, maxA = double.MinValue;
            foreach (var m in Moves)
            {
                (minX, maxX) = (Math.Min(minX, m.X), Math.Max(maxX, m.X));
                (minY, maxY) = (Math.Min(minY, m.Y), Math.Max(maxY, m.Y));
                (minZ, maxZ) = (Math.Min(minZ, m.Z), Math.Max(maxZ, m.Z));
                (minA, maxA) = (Math.Min(minA, m.A), Math.Max(maxA, m.A));
            }

            return new(new P3(minX, minY, minZ), new P3(maxX, maxY, maxZ), minA, maxA);
        }
    }

    /// <summary>Length of the move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double MoveLength(ToolpathMove from, ToolpathMove to)
    {
        var linear = P3.Distance(from.Position, to.Position);
        var da = (to.A - from.A) * Math.PI / 180;
        if (da == 0)
        {
            return linear;
        }

        var radius = Math.Abs(0.5 * (Math.Sqrt((from.Y * from.Y) + (from.Z * from.Z)) + Math.Sqrt((to.Y * to.Y) + (to.Z * to.Z))));
        return Math.Sqrt((linear * linear) + (da * radius * da * radius));
    }

    private double Length(Func<MoveKind, bool> include)
    {
        double total = 0;
        for (var i = 1; i < Moves.Count; i++)
        {
            if (include(Moves[i].Kind))
            {
                total += MoveLength(Moves[i - 1], Moves[i]);
            }
        }

        return total;
    }
}
