using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths;

/// <summary>
/// Waterline regions from push-cutter fibers: the XY area where the tool tip may not be at a given Z, traced by
/// marching squares over a fiber grid with exact crossings taken from the fiber intervals.
/// </summary>
public static class FiberContours
{
    /// <summary>Fiber spacing for a tolerance: chord error of a radius-R arc between fibers, at most R/4.</summary>
    public static double Spacing(double radius, double tolerance) =>
        Math.Max(Math.Min(Math.Sqrt(8 * radius * tolerance), radius / 4), 1e-3);

    /// <summary>
    /// Forbidden tip region at height <paramref name="z"/> within <paramref name="area"/> (min/max XY), traced as
    /// loops with the forbidden side on the left. Parts of the region crossing the area border are closed along it.
    /// </summary>
    public static PathsD Forbidden(
        ToolAssembly tool, MachiningSetup setup, double z, Vector2 min, Vector2 max, double spacing, bool part = true, bool obstacles = true)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(setup);
        var nx = Math.Max((int)Math.Ceiling((max.X - min.X) / spacing), 1) + 1;
        var ny = Math.Max((int)Math.Ceiling((max.Y - min.Y) / spacing), 1) + 1;
        var step = (float)spacing;
        var length = new Vector2((nx - 1) * step, (ny - 1) * step);
        var fibers = new Fiber[nx + ny];
        for (var j = 0; j < ny; j++)
        {
            fibers[j] = new Fiber(new Vector3(min.X, min.Y + (j * step), (float)z), Vector2.UnitX, length.X);
        }

        for (var i = 0; i < nx; i++)
        {
            fibers[ny + i] = new Fiber(new Vector3(min.X + (i * step), min.Y, (float)z), Vector2.UnitY, length.Y);
        }

        var intervals = tool.Push(setup, fibers, part, obstacles);
        if (intervals.All(f => f.Length == 0))
        {
            return [];
        }

        // Node (i, j) of the padded (nx + 2) × (ny + 2) grid; the outer ring is always free so every loop closes.
        var state = new bool[nx * ny];
        for (var j = 0; j < ny; j++)
        {
            var row = intervals[j];
            var k = 0;
            for (var i = 0; i < nx; i++)
            {
                var t = i * step;
                while (k < row.Length && row[k].Exit < t)
                {
                    k++;
                }

                state[(j * nx) + i] = k < row.Length && row[k].Enter <= t;
            }
        }

        bool Inside(int i, int j) => i > 0 && j > 0 && i <= nx && j <= ny && state[((j - 1) * nx) + i - 1];

        PointD Node(int i, int j) => new(min.X + (Math.Clamp(i - 1, 0, nx - 1) * spacing), min.Y + (Math.Clamp(j - 1, 0, ny - 1) * spacing));

        PointD Crossing(int i, int j, bool horizontal)
        {
            // Edges touching the padding ring cross at the border node itself.
            if (horizontal ? i == 0 || i == nx || j == 0 || j == ny + 1 : j == 0 || j == ny || i == 0 || i == nx + 1)
            {
                return Inside(i, j) ? Node(i, j) : Node(horizontal ? i + 1 : i, horizontal ? j : j + 1);
            }

            var (fiber, from) = horizontal ? (intervals[j - 1], (i - 1) * step) : (intervals[ny + i - 1], (j - 1) * step);
            var t = CrossingOnFiber(fiber, from, from + step);
            return horizontal ? new PointD(min.X + t, min.Y + ((j - 1) * spacing)) : new PointD(min.X + ((i - 1) * spacing), min.Y + t);
        }

        var loops = MarchingSquares.Trace(nx + 2, ny + 2, Inside, Crossing, (_, _) => true)
            .Where(c => c.Closed && c.Points.Count >= 3)
            .Select(c => c.Points);
        return Polygons.Union(new PathsD(loops));
    }

    /// <summary>Forbidden region over the whole part (and keep-outs) plus a margin.</summary>
    public static PathsD Forbidden(ToolAssembly tool, MachiningSetup setup, double z, double spacing, bool part = true, bool obstacles = true)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(tool);
        var bounds = part ? setup.PartBounds : BoundingBox.Empty;
        if (obstacles && setup.Obstacles is not null)
        {
            bounds = bounds.Union(setup.Obstacles.Bounds);
        }

        var margin = (float)(tool.MaxRadius + (2 * spacing));
        return Forbidden(tool, setup, z, new Vector2(bounds.Min.X - margin, bounds.Min.Y - margin), new Vector2(bounds.Max.X + margin, bounds.Max.Y + margin), spacing, part, obstacles);
    }

    /// <summary>Interval end inside (from, to) closest to the middle; the middle when the fibers disagree.</summary>
    private static double CrossingOnFiber(FiberInterval[] intervals, double from, double to)
    {
        var middle = 0.5 * (from + to);
        var best = middle;
        var bestDistance = double.MaxValue;
        foreach (var interval in intervals)
        {
            Consider(interval.Enter);
            Consider(interval.Exit);
        }

        return best;

        void Consider(double t)
        {
            if (t >= from && t <= to && Math.Abs(t - middle) < bestDistance)
            {
                best = t;
                bestDistance = Math.Abs(t - middle);
            }
        }
    }
}
