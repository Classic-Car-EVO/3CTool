using Clipper2Lib;

namespace ThreeCTool.Geometry;

/// <summary>A traced contour: closed loops have the inside on their left (counter-clockwise around inside areas).</summary>
public sealed record Contour(PathD Points, bool Closed);

/// <summary>
/// Marching squares on a node grid (nx × ny). Node (i, j) is inside or outside; crossings on grid edges come from a
/// callback so callers can supply exact positions (e.g. waterline fibers) or interpolate a scalar field.
/// </summary>
public static class MarchingSquares
{
    // Per case: segments as (from edge, to edge) with the inside on the left. Edges: 0 bottom, 1 right, 2 top, 3 left.
    private static readonly int[][] Cases =
    [
        [],
        [0, 3],
        [1, 0],
        [1, 3],
        [2, 1],
        [0, 3, 2, 1],
        [2, 0],
        [2, 3],
        [3, 2],
        [0, 2],
        [1, 0, 3, 2],
        [1, 2],
        [3, 1],
        [0, 1],
        [3, 0],
        [],
    ];

    /// <summary>
    /// Traces all contours. <paramref name="crossing"/>(i, j, horizontal) returns the crossing on the edge from node
    /// (i, j) to (i + 1, j) (horizontal) or to (i, j + 1). <paramref name="saddleInside"/>(i, j) decides whether the
    /// two inside corners of an ambiguous cell are connected.
    /// </summary>
    public static List<Contour> Trace(
        int nx,
        int ny,
        Func<int, int, bool> inside,
        Func<int, int, bool, PointD> crossing,
        Func<int, int, bool>? saddleInside = null)
    {
        ArgumentNullException.ThrowIfNull(inside);
        ArgumentNullException.ThrowIfNull(crossing);
        var contours = new List<Contour>();
        if (nx < 2 || ny < 2)
        {
            return contours;
        }

        var states = new bool[nx * ny];
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                states[(j * nx) + i] = inside(i, j);
            }
        }

        var horizontalCount = (long)(nx - 1) * ny;
        var next = new Dictionary<long, long>();
        var hasPredecessor = new HashSet<long>();
        for (var j = 0; j < ny - 1; j++)
        {
            for (var i = 0; i < nx - 1; i++)
            {
                var index = (states[(j * nx) + i] ? 1 : 0)
                    | (states[(j * nx) + i + 1] ? 2 : 0)
                    | (states[((j + 1) * nx) + i + 1] ? 4 : 0)
                    | (states[((j + 1) * nx) + i] ? 8 : 0);
                var segments = Cases[index];
                if (index is 5 or 10 && saddleInside is not null && saddleInside(i, j))
                {
                    segments = index == 5 ? [0, 1, 2, 3] : [3, 0, 1, 2];
                }

                for (var s = 0; s < segments.Length; s += 2)
                {
                    var from = EdgeId(i, j, segments[s], nx, horizontalCount);
                    var to = EdgeId(i, j, segments[s + 1], nx, horizontalCount);
                    next[from] = to;
                    hasPredecessor.Add(to);
                }
            }
        }

        var points = new Dictionary<long, PointD>();
        PointD Point(long edge)
        {
            if (!points.TryGetValue(edge, out var point))
            {
                point = edge < horizontalCount
                    ? crossing((int)(edge % (nx - 1)), (int)(edge / (nx - 1)), true)
                    : crossing((int)((edge - horizontalCount) % nx), (int)((edge - horizontalCount) / nx), false);
                points[edge] = point;
            }

            return point;
        }

        // Open chains start at border edges that nothing leads into.
        foreach (var start in next.Keys.Where(e => !hasPredecessor.Contains(e)).ToList())
        {
            var path = new PathD();
            var edge = start;
            path.Add(Point(edge));
            while (next.Remove(edge, out var to))
            {
                path.Add(Point(to));
                edge = to;
            }

            contours.Add(new Contour(path, false));
        }

        while (next.Count > 0)
        {
            var start = next.Keys.First();
            var path = new PathD();
            var edge = start;
            while (next.Remove(edge, out var to))
            {
                path.Add(Point(edge));
                edge = to;
            }

            contours.Add(new Contour(path, edge == start));
        }

        return contours;
    }

    /// <summary>
    /// Iso-contours of a row-major scalar field (index j·nx + i) sampled at origin + (i, j)·spacing. Inside means
    /// value ≥ <paramref name="level"/>; NaN counts as outside.
    /// </summary>
    public static List<Contour> Isolines(ReadOnlySpan<float> field, int nx, int ny, PointD origin, double spacing, double level)
    {
        if (field.Length != nx * ny)
        {
            throw new ArgumentException("Field size must be nx·ny.", nameof(field));
        }

        var values = field.ToArray();
        bool Inside(int i, int j) => values[(j * nx) + i] >= level;
        double Value(int i, int j)
        {
            var v = values[(j * nx) + i];
            return float.IsNaN(v) ? double.NegativeInfinity : v;
        }

        PointD Crossing(int i, int j, bool horizontal)
        {
            var (i2, j2) = horizontal ? (i + 1, j) : (i, j + 1);
            var a = Value(i, j);
            var b = Value(i2, j2);
            var t = double.IsInfinity(a) || double.IsInfinity(b) || a == b ? 0.5 : Math.Clamp((level - a) / (b - a), 0, 1);
            return horizontal
                ? new PointD(origin.x + ((i + t) * spacing), origin.y + (j * spacing))
                : new PointD(origin.x + (i * spacing), origin.y + ((j + t) * spacing));
        }

        bool Saddle(int i, int j) =>
            (Value(i, j) + Value(i + 1, j) + Value(i + 1, j + 1) + Value(i, j + 1)) / 4 >= level;

        return Trace(nx, ny, Inside, Crossing, Saddle);
    }

    /// <summary>
    /// Region covered by the true cells of a row-major mask whose cell (i, j) is centred at
    /// origin + (i + 0.5, j + 0.5)·resolution. Boundaries run half way between cell centres.
    /// </summary>
    public static PathsD MaskRegion(ReadOnlySpan<bool> mask, int nx, int ny, PointD origin, double resolution)
    {
        if (mask.Length != nx * ny)
        {
            throw new ArgumentException("Mask size must be nx·ny.", nameof(mask));
        }

        // Pad by one outside node on every side so every loop closes.
        var cells = mask.ToArray();
        bool Inside(int i, int j) => i > 0 && j > 0 && i <= nx && j <= ny && cells[((j - 1) * nx) + i - 1];
        PointD Crossing(int i, int j, bool horizontal) => horizontal
            ? new PointD(origin.x + (i * resolution), origin.y + ((j - 0.5) * resolution))
            : new PointD(origin.x + ((i - 0.5) * resolution), origin.y + (j * resolution));

        var loops = Trace(nx + 2, ny + 2, Inside, Crossing, (_, _) => true)
            .Where(c => c.Closed && c.Points.Count >= 3)
            .Select(c => c.Points);
        return Polygons.Union(new PathsD(loops));
    }

    private static long EdgeId(int i, int j, int side, int nx, long horizontalCount) => side switch
    {
        0 => ((long)j * (nx - 1)) + i,
        1 => horizontalCount + ((long)j * nx) + i + 1,
        2 => ((long)(j + 1) * (nx - 1)) + i,
        _ => horizontalCount + ((long)j * nx) + i,
    };
}
