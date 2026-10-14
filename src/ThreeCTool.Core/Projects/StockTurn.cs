using System.Globalization;

using V3 = (double X, double Y, double Z);

namespace ThreeCTool.Core.Projects;

/// <summary>
/// Multi-sided 3-axis machining: the operator turns a box stock by hand in 90° steps about X or Y (which
/// <see cref="StockFace"/> faces up), optionally spins it about the vertical axis, and puts it back into a fixed jig
/// so the work coordinates stay where they were. The turn is the rotation R (part → turned), applied before the
/// translation that sets the turned stock on the table at the jig.
/// </summary>
public static class StockTurn
{
    private const double Tolerance = 1e-9;

    public static IReadOnlyList<StockFace> Faces { get; } = Enum.GetValues<StockFace>();

    public static IReadOnlyList<int> Spins { get; } = [0, 90, 180, 270];

    /// <summary>The setup is 3-axis and the stock is not in its original position (another face up or spun).</summary>
    public static bool IsTurned(Setup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return setup.Orientation == SetupOrientation.ThreeAxis && (setup.Face != StockFace.Top || NormalizeSpin(setup.Spin ?? 0) != 0);
    }

    /// <summary>R·v: the face turned up (about X for front/back/bottom, about Y for left/right), then the spin about Z.</summary>
    public static V3 Rotate(StockFace face, int spin, V3 v)
    {
        var (x, y, z) = face switch
        {
            StockFace.Bottom => (v.X, -v.Y, -v.Z),
            StockFace.Front => (v.X, v.Z, -v.Y),
            StockFace.Back => (v.X, -v.Z, v.Y),
            StockFace.Left => (v.Z, v.Y, -v.X),
            StockFace.Right => (-v.Z, v.Y, v.X),
            _ => v,
        };
        return NormalizeSpin(spin) switch
        {
            90 => (-y, x, z),
            180 => (-x, -y, z),
            270 => (y, -x, z),
            _ => (x, y, z),
        };
    }

    /// <summary>Rᵀ·v, the inverse of <see cref="Rotate"/>.</summary>
    public static V3 Unrotate(StockFace face, int spin, V3 v)
    {
        var ex = Rotate(face, spin, (1, 0, 0));
        var ey = Rotate(face, spin, (0, 1, 0));
        var ez = Rotate(face, spin, (0, 0, 1));
        return (Dot(ex, v), Dot(ey, v), Dot(ez, v));
    }

    /// <summary>Nearest of 0, 90, 180 and 270.</summary>
    public static int NormalizeSpin(int spin) => ((((int)Math.Round(spin / 90.0, MidpointRounding.AwayFromZero)) % 4) + 4) % 4 * 90;

    /// <summary>
    /// The spin used: the chosen one, else 0 for the top and bottom faces (the stock lies as placed) and for the side
    /// faces whichever puts the longest edge along X.
    /// </summary>
    public static int ResolveSpin(StockFace face, int? spin, double sizeX, double sizeY, double sizeZ)
    {
        if (spin is { } chosen)
        {
            return NormalizeSpin(chosen);
        }

        if (face is StockFace.Top or StockFace.Bottom)
        {
            return 0;
        }

        var turned = Rotate(face, 0, (sizeX, sizeY, sizeZ));
        return Math.Abs(turned.Y) > Math.Abs(turned.X) + Tolerance ? 90 : 0;
    }

    /// <summary>Spin of a setup on a stock of the given size.</summary>
    public static int ResolveSpin(Setup setup, double sizeX, double sizeY, double sizeZ)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return ResolveSpin(setup.Face, setup.Spin, sizeX, sizeY, sizeZ);
    }

    /// <summary>Size of the turned stock (X, Y, Z on the table).</summary>
    public static V3 TurnedSize(StockFace face, int spin, double sizeX, double sizeY, double sizeZ)
    {
        var turned = Rotate(face, spin, (sizeX, sizeY, sizeZ));
        return (Math.Abs(turned.X), Math.Abs(turned.Y), Math.Abs(turned.Z));
    }

    /// <summary>
    /// Translation T placing the turned stock box (part-frame bounds <paramref name="min"/>..<paramref name="max"/>)
    /// at the jig: R·p + T rests the turned box on the original bottom, centred on the original centre
    /// (<see cref="TurnAnchor.Center"/>) or with its front-left corner on the original one (<see cref="TurnAnchor.FrontLeft"/>).
    /// </summary>
    public static V3 Placement(StockFace face, int spin, TurnAnchor anchor, V3 min, V3 max)
    {
        var a = Rotate(face, spin, min);
        var b = Rotate(face, spin, max);
        V3 low = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        V3 high = (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        var z = min.Z - low.Z;
        return anchor == TurnAnchor.FrontLeft
            ? (min.X - low.X, min.Y - low.Y, z)
            : ((0.5 * (min.X + max.X)) - (0.5 * (low.X + high.X)), (0.5 * (min.Y + max.Y)) - (0.5 * (low.Y + high.Y)), z);
    }

    public static StockFace Opposite(StockFace face) => face switch
    {
        StockFace.Top => StockFace.Bottom,
        StockFace.Bottom => StockFace.Top,
        StockFace.Front => StockFace.Back,
        StockFace.Back => StockFace.Front,
        StockFace.Left => StockFace.Right,
        _ => StockFace.Left,
    };

    /// <summary>Stock size along the normal of <paramref name="face"/>.</summary>
    public static double Thickness(StockFace face, double sizeX, double sizeY, double sizeZ) => face switch
    {
        StockFace.Top or StockFace.Bottom => sizeZ,
        StockFace.Front or StockFace.Back => sizeY,
        _ => sizeX,
    };

    /// <summary>
    /// Deepest cut of a 3-axis setup below the turned stock top: its own limit, else half the stock thickness when
    /// another 3-axis setup with enabled operations machines the opposite face, else null (unlimited).
    /// </summary>
    public static double? DepthLimit(Setup setup, IEnumerable<Setup> setups, Stock stock)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(setups);
        ArgumentNullException.ThrowIfNull(stock);
        if (setup.Orientation != SetupOrientation.ThreeAxis)
        {
            return null;
        }

        if (setup.DepthLimit is { } limit && limit > 0)
        {
            return limit;
        }

        if (stock.Type != StockType.Box)
        {
            return null;
        }

        var opposite = Opposite(setup.Face);
        return setups.Any(s => s.Id != setup.Id && s.Orientation == SetupOrientation.ThreeAxis && s.Face == opposite && s.Operations.Any(o => o.Enabled))
            ? Thickness(setup.Face, stock.SizeX, stock.SizeY, stock.SizeZ) / 2
            : null;
    }

    /// <summary>Why the 3-axis setups cannot be machined as set up (turned non-box stock, tabs without a depth limit), or nothing.</summary>
    public static IEnumerable<string> Problems(Stock stock, IReadOnlyList<Setup> setups)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(setups);
        foreach (var setup in setups.Where(s => s.Orientation == SetupOrientation.ThreeAxis && s.Operations.Any(o => o.Enabled)))
        {
            if (IsTurned(setup) && stock.Type != StockType.Box)
            {
                yield return $"Setup '{setup.Name}' turns the stock; only a box stock can be turned.";
            }

            if (setup.DepthLimit is < 0)
            {
                yield return $"Setup '{setup.Name}' has a negative depth limit.";
            }

            if (setup.TabCount < 0)
            {
                yield return $"Setup '{setup.Name}' has a negative number of tabs.";
            }
            else if (setup.TabCount > 0)
            {
                if (setup.TabWidth <= 0 || setup.TabThickness <= 0 || setup.FrameWidth <= 0)
                {
                    yield return $"Setup '{setup.Name}' needs a tab width, tab thickness and frame width above zero.";
                }

                if (stock.Type != StockType.Box)
                {
                    yield return $"Setup '{setup.Name}' has tabs; tabs and their frame need a box stock.";
                }
                else if (DepthLimit(setup, setups, stock) is null)
                {
                    yield return $"Setup '{setup.Name}' has tabs but no depth limit; set one or machine the opposite face too.";
                }
            }
        }
    }

    /// <summary>Both faces lie on the same axis (equal or opposite): they share the depth plane, tabs and frame.</summary>
    public static bool SameAxis(StockFace a, StockFace b) => a == b || a == Opposite(b);

    public static string FaceName(StockFace face) => face switch
    {
        StockFace.Front => "front (-Y)",
        StockFace.Back => "back (+Y)",
        StockFace.Left => "left (-X)",
        StockFace.Right => "right (+X)",
        _ => face.ToString().ToLowerInvariant(),
    };

    /// <summary>Operator instruction for the turned position, e.g. "front face up, original top face towards +Y back, original right face towards +X right".</summary>
    public static string Instruction(StockFace face, int spin)
    {
        var name = face switch
        {
            StockFace.Front => "front",
            StockFace.Back => "back",
            StockFace.Left => "left",
            StockFace.Right => "right",
            _ => face.ToString().ToLowerInvariant(),
        };

        // Two of the three original faces end up facing sideways: they fix the position.
        V3[] axes = [(0, 0, 1), (0, -1, 0), (1, 0, 0)];
        string[] names = ["top", "front", "right"];
        var sides = Enumerable.Range(0, 3)
            .Select(i => (Name: names[i], Turned: Rotate(face, spin, axes[i])))
            .Where(s => Math.Abs(s.Turned.Z) < 0.5)
            .Select(s => $"original {s.Name} face {Direction(s.Turned)}");
        return $"{name} face up, {string.Join(", ", sides)}";
    }

    private static string Direction(V3 v) =>
        v.Z > 0.5 ? "up"
        : v.Z < -0.5 ? "down on the table"
        : v.X > 0.5 ? "towards +X right"
        : v.X < -0.5 ? "towards -X left"
        : v.Y > 0.5 ? "towards +Y back"
        : "towards -Y front";

    private static double Dot(V3 a, V3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
