using Clipper2Lib;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>
/// Pencil finishing: follows concave creases of the tool-tip surface (where the tool touches two faces), found as
/// slope breaks on a drop grid, thinned to centre lines and traced into chains.
/// </summary>
internal static class PencilFinishing
{
    private const int MaxNodes = 4_000_000;
    private const double CreaseAngle = 20 * Math.PI / 180;

    public static void Run(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var grid = SurfaceGrid.Create(context, Math.Max(Math.Min(context.Radius / 4, settings.Stepover / 2), 0.02), MaxNodes);
        context.Report(0.3);
        var mask = Creases(grid);
        Thin(mask, grid.CountX, grid.CountY);
        var chains = Trace(mask, grid.CountX, grid.CountY)
            .Select(c => Smooth(c.Select(k => new PointD(grid.Origin.x + (k % grid.CountX * grid.Spacing), grid.Origin.y + (k / grid.CountX * grid.Spacing))).ToList()))
            .Where(c => Length(c) > 2 * Math.Max(grid.Spacing, context.Radius / 2))
            .ToList();
        context.Report(0.5);

        var sample = ParallelFinishing.SampleStep(context);
        var polylines = new List<List<PointD>>();
        foreach (var chain in chains)
        {
            polylines.Add(PassTools.Resample(new PathD(chain), sample, false));
            for (var k = 1; k < settings.PencilPasses; k++)
            {
                foreach (var side in (ReadOnlySpan<int>)[1, -1])
                {
                    var offset = Offset(chain, side * k * settings.Stepover);
                    polylines.Add(PassTools.Resample(new PathD(offset), sample, false));
                }
            }
        }

        var runs = SurfaceProjection.Project(context, polylines, sample);
        context.Report(0.8);
        builder.StayDownLink = (a, b) => context.SurfaceLink(a, b, Math.Max(2 * settings.Stepover, context.Radius));
        // A crease has a wall on both sides, so either direction is fine.
        var pending = runs.Select(r => r.Points).ToList();
        while (pending.Count > 0)
        {
            var (index, flip) = PassTools.Nearest(pending, builder.Current, p => p[0], p => p[^1], true);
            var points = pending[index];
            pending.RemoveAt(index);
            if (flip)
            {
                points.Reverse();
            }

            builder.AddPass(new CutPass(points));
        }
    }

    /// <summary>Nodes where the slope angle increases by more than the crease angle in any of four directions.</summary>
    private static bool[] Creases(SurfaceGrid grid)
    {
        var (nx, ny, g) = (grid.CountX, grid.CountY, grid.Spacing);
        var mask = new bool[nx * ny];
        (int Di, int Dj)[] directions = [(1, 0), (0, 1), (1, 1), (1, -1)];
        Parallel.For(1, ny - 1, j =>
        {
            for (var i = 1; i < nx - 1; i++)
            {
                var k = (j * nx) + i;
                if (!grid.Inside[k])
                {
                    continue;
                }

                foreach (var (di, dj) in directions)
                {
                    var (a, b) = (k - (dj * nx) - di, k + (dj * nx) + di);
                    if (!grid.Inside[a] || !grid.Inside[b] || grid.Clamped[a] || grid.Clamped[k] || grid.Clamped[b])
                    {
                        // Kinks against the artificial floor are not part creases.
                        continue;
                    }

                    var run = g * Math.Sqrt((di * di) + (dj * dj));
                    var before = Math.Atan((grid.Z[k] - grid.Z[a]) / run);
                    var after = Math.Atan((grid.Z[b] - grid.Z[k]) / run);
                    if (after - before > CreaseAngle)
                    {
                        mask[k] = true;
                        break;
                    }
                }
            }
        });

        return mask;
    }

    /// <summary>Zhang–Suen thinning to one-node-wide centre lines.</summary>
    private static void Thin(bool[] mask, int nx, int ny)
    {
        var remove = new List<int>();
        bool changed;
        do
        {
            changed = false;
            for (var pass = 0; pass < 2; pass++)
            {
                remove.Clear();
                for (var j = 1; j < ny - 1; j++)
                {
                    for (var i = 1; i < nx - 1; i++)
                    {
                        var k = (j * nx) + i;
                        if (!mask[k])
                        {
                            continue;
                        }

                        // P2..P9 clockwise from north (north = +j).
                        Span<bool> p =
                        [
                            mask[k + nx], mask[k + nx + 1], mask[k + 1], mask[k - nx + 1],
                            mask[k - nx], mask[k - nx - 1], mask[k - 1], mask[k + nx - 1],
                        ];
                        var count = 0;
                        var transitions = 0;
                        for (var n = 0; n < 8; n++)
                        {
                            count += p[n] ? 1 : 0;
                            transitions += !p[n] && p[(n + 1) % 8] ? 1 : 0;
                        }

                        var ok = count is >= 2 and <= 6 && transitions == 1 && (pass == 0
                            ? !(p[0] && p[2] && p[4]) && !(p[2] && p[4] && p[6])
                            : !(p[0] && p[2] && p[6]) && !(p[0] && p[4] && p[6]));
                        if (ok)
                        {
                            remove.Add(k);
                        }
                    }
                }

                foreach (var k in remove)
                {
                    mask[k] = false;
                }

                changed |= remove.Count > 0;
            }
        }
        while (changed);
    }

    /// <summary>Chains of 8-connected skeleton nodes: walks from line ends first, preferring orthogonal steps.</summary>
    private static List<List<int>> Trace(bool[] mask, int nx, int ny)
    {
        var visited = new bool[mask.Length];
        var chains = new List<List<int>>();

        List<int> Neighbours(int k)
        {
            var (i, j) = (k % nx, k / nx);
            var result = new List<int>(8);
            foreach (var (di, dj) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)])
            {
                var (a, b) = (i + di, j + dj);
                if (a >= 0 && b >= 0 && a < nx && b < ny && mask[(b * nx) + a])
                {
                    result.Add((b * nx) + a);
                }
            }

            return result;
        }

        var nodes = Enumerable.Range(0, mask.Length).Where(k => mask[k]).ToList();
        foreach (var start in nodes.Where(k => Neighbours(k).Count == 1).Concat(nodes))
        {
            if (visited[start])
            {
                continue;
            }

            var chain = new List<int> { start };
            visited[start] = true;
            var current = start;
            while (true)
            {
                var neighbours = Neighbours(current);
                var next = neighbours.FirstOrDefault(n => !visited[n], -1);
                if (next < 0)
                {
                    // Join a branch to the junction it ends at.
                    var join = neighbours.FirstOrDefault(n => chain.Count < 3 || !chain[^3..].Contains(n), -1);
                    if (join >= 0 && chain.Count > 1)
                    {
                        chain.Add(join);
                    }

                    break;
                }

                visited[next] = true;
                chain.Add(next);
                current = next;
            }

            if (chain.Count >= 2)
            {
                chains.Add(chain);
            }
        }

        return chains;
    }

    private static List<PointD> Smooth(List<PointD> chain)
    {
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var next = new List<PointD>(chain.Count) { chain[0] };
            for (var i = 1; i < chain.Count - 1; i++)
            {
                next.Add(new PointD((0.25 * chain[i - 1].x) + (0.5 * chain[i].x) + (0.25 * chain[i + 1].x), (0.25 * chain[i - 1].y) + (0.5 * chain[i].y) + (0.25 * chain[i + 1].y)));
            }

            if (chain.Count > 1)
            {
                next.Add(chain[^1]);
            }

            chain = next;
        }

        return chain;
    }

    /// <summary>Copy shifted sideways by <paramref name="distance"/> (left positive) along vertex normals.</summary>
    private static List<PointD> Offset(List<PointD> chain, double distance)
    {
        var result = new List<PointD>(chain.Count);
        for (var i = 0; i < chain.Count; i++)
        {
            var a = chain[Math.Max(i - 1, 0)];
            var b = chain[Math.Min(i + 1, chain.Count - 1)];
            var (dx, dy) = (b.x - a.x, b.y - a.y);
            var length = Math.Max(Math.Sqrt((dx * dx) + (dy * dy)), 1e-12);
            result.Add(new PointD(chain[i].x - (dy / length * distance), chain[i].y + (dx / length * distance)));
        }

        return result;
    }

    private static double Length(List<PointD> chain)
    {
        double length = 0;
        for (var i = 1; i < chain.Count; i++)
        {
            length += Math.Sqrt(((chain[i].x - chain[i - 1].x) * (chain[i].x - chain[i - 1].x)) + ((chain[i].y - chain[i - 1].y) * (chain[i].y - chain[i - 1].y)));
        }

        return length;
    }
}
