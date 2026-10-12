using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>
/// Scallop (constant stepover) finishing: passes are iso-distance curves of the geodesic distance from the boundary,
/// measured over the tool-tip surface so the 3D spacing stays constant on slopes.
/// </summary>
internal static class ScallopFinishing
{
    private const int MaxNodes = 4_000_000;

    private static readonly (int Di, int Dj)[] Neighbours =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
        (2, 1), (2, -1), (-2, 1), (-2, -1), (1, 2), (1, -2), (-1, 2), (-1, -2),
    ];

    public static void Run(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var step = settings.Stepover;
        var grid = SurfaceGrid.Create(context, Math.Max(step / 4, 0.02), MaxNodes);
        context.Report(0.3);
        var distance = Geodesic(grid);
        context.Report(0.5);
        var max = distance.Where(d => !float.IsNaN(d)).DefaultIfEmpty(0).Max();
        var sample = ParallelFinishing.SampleStep(context);
        var rings = new List<List<List<PointD>>>();
        for (var level = 0.5 * step; level < max; level += step)
        {
            rings.Add(MarchingSquares.Isolines(distance, grid.CountX, grid.CountY, grid.Origin, grid.Spacing, level)
                .Where(c => Polygons.Length(c.Points, c.Closed) > 4 * context.Tolerance)
                .Select(c => PassTools.Resample(c.Points, sample, c.Closed))
                .ToList());
        }

        var flat = rings.SelectMany((r, k) => r.Select(p => (Ring: k, Points: p))).ToList();
        var runs = SurfaceProjection.Project(context, flat.Select(f => f.Points).ToList(), sample);
        context.Report(0.8);

        builder.StayDownLink = (a, b) => context.SurfaceLink(a, b, Math.Max(2 * step, context.Radius));
        var reversible = settings.Direction == CutDirection.Both;
        var conventional = settings.Direction == CutDirection.Conventional;
        foreach (var ring in runs.GroupBy(r => flat[r.Source].Ring).OrderBy(g => g.Key))
        {
            var pending = ring.Select(r => r.Points).ToList();
            if (conventional)
            {
                pending.ForEach(p => p.Reverse());
            }

            while (pending.Count > 0)
            {
                var (index, flip) = PassTools.Nearest(pending, builder.Current, p => p[0], p => p[^1], reversible);
                var points = pending[index];
                pending.RemoveAt(index);
                if (flip)
                {
                    points.Reverse();
                }

                builder.AddPass(new CutPass(points));
            }
        }
    }

    /// <summary>Distance over the tip surface from the region edge (NaN outside), 16-neighbour Dijkstra.</summary>
    private static float[] Geodesic(SurfaceGrid grid)
    {
        var (nx, ny, g) = (grid.CountX, grid.CountY, grid.Spacing);
        var distance = new float[nx * ny];
        Array.Fill(distance, float.NaN);
        var best = new double[nx * ny];
        Array.Fill(best, double.MaxValue);
        var queue = new PriorityQueue<int, double>();
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var k = (j * nx) + i;
                if (!grid.Inside[k])
                {
                    continue;
                }

                var edge = i == 0 || j == 0 || i == nx - 1 || j == ny - 1 ||
                    !grid.Inside[k - 1] || !grid.Inside[k + 1] || !grid.Inside[k - nx] || !grid.Inside[k + nx];
                if (edge)
                {
                    best[k] = 0;
                    queue.Enqueue(k, 0);
                }
            }
        }

        while (queue.TryDequeue(out var k, out var d))
        {
            if (d > best[k])
            {
                continue;
            }

            distance[k] = (float)d;
            var (i, j) = (k % nx, k / nx);
            foreach (var (di, dj) in Neighbours)
            {
                var (a, b) = (i + di, j + dj);
                if (a < 0 || b < 0 || a >= nx || b >= ny)
                {
                    continue;
                }

                var n = (b * nx) + a;
                if (!grid.Inside[n])
                {
                    continue;
                }

                var dz = grid.Z[n] - grid.Z[k];
                var nd = d + Math.Sqrt((((di * di) + (dj * dj)) * g * g) + (dz * dz));
                if (nd < best[n])
                {
                    best[n] = nd;
                    queue.Enqueue(n, nd);
                }
            }
        }

        return distance;
    }
}

/// <summary>Tip heights of the stocked tool on a regular grid over the boundary (nodes at origin + (i, j)·spacing).</summary>
internal sealed class SurfaceGrid
{
    private SurfaceGrid(PointD origin, double spacing, int countX, int countY, double[] z, bool[] inside, bool[] clamped)
    {
        Origin = origin;
        Spacing = spacing;
        CountX = countX;
        CountY = countY;
        Z = z;
        Inside = inside;
        Clamped = clamped;
    }

    public PointD Origin { get; }

    public double Spacing { get; }

    public int CountX { get; }

    public int CountY { get; }

    /// <summary>Tip Z (floor where nothing is below).</summary>
    public double[] Z { get; }

    /// <summary>Node inside the boundary and below the max Z.</summary>
    public bool[] Inside { get; }

    /// <summary>Node raised to the floor (nothing below, or the part continues below the floor).</summary>
    public bool[] Clamped { get; }

    public static SurfaceGrid Create(StrategyContext context, double spacing, int maxNodes)
    {
        var bounds = Clipper.GetBounds(context.Boundary);
        var (w, h) = (bounds.right - bounds.left, bounds.bottom - bounds.top);
        spacing = Math.Max(spacing, Math.Sqrt(w * h / maxNodes));
        var nx = (int)Math.Ceiling(w / spacing) + 1;
        var ny = (int)Math.Ceiling(h / spacing) + 1;
        var origin = new PointD(bounds.left, bounds.top);
        var inside = RegionRaster.Rasterize(context.Boundary, origin, spacing, nx, ny);
        context.Token.ThrowIfCancellationRequested();
        var drop = context.Stocked.DropGrid(context.Setup, new Vector2((float)origin.x, (float)origin.y), (float)spacing, nx, ny);
        var floor = context.FinishingFloor;
        var maxZ = context.Settings.MaxZ ?? double.MaxValue;
        var z = new double[drop.Length];
        var clamped = new bool[drop.Length];
        for (var k = 0; k < z.Length; k++)
        {
            clamped[k] = drop[k] < floor - 1e-6;
            z[k] = clamped[k] ? floor : drop[k];
            inside[k] &= z[k] <= maxZ;
        }

        return new SurfaceGrid(origin, spacing, nx, ny, z, inside, clamped);
    }
}
