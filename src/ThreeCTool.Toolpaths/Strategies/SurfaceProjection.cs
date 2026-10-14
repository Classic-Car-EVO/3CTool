using System.Numerics;

using Clipper2Lib;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>Drops XY polylines onto the part with the stocked tool, refining until the chord error is within tolerance.</summary>
internal static class SurfaceProjection
{
    private const int RefineRounds = 4;

    /// <summary>
    /// Projected runs (source polyline index, tip points). No contact clamps to the floor; points above
    /// <see cref="ToolpathSettings.MaxZ"/> split the run.
    /// </summary>
    public static List<(int Source, List<P3> Points)> Project(StrategyContext context, IReadOnlyList<List<PointD>> polylines, double step)
    {
        var floor = context.FinishingFloor;
        var tolerance = context.Tolerance;
        var runs = polylines.Select(p => p.Select(q => new P3(q.x, q.y, double.NaN)).ToList()).ToList();
        DropMissing(context, runs, floor);

        for (var round = 0; round < RefineRounds; round++)
        {
            var inserted = false;
            var candidates = new List<(int Run, int Index, P3 Mid)>();
            for (var r = 0; r < runs.Count; r++)
            {
                var run = runs[r];
                for (var i = 1; i < run.Count; i++)
                {
                    if ((run[i] - run[i - 1]).LengthXY > 0.02)
                    {
                        candidates.Add((r, i, P3.Lerp(run[i - 1], run[i], 0.5)));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                break;
            }

            var z = Drop(context, candidates.Select(c => c.Mid).ToList(), floor);
            foreach (var group in candidates.Select((c, k) => (c, z: z[k])).Where(x => Math.Abs(x.z - x.c.Mid.Z) > tolerance).GroupBy(x => x.c.Run))
            {
                var run = runs[group.Key];
                foreach (var (c, zm) in group.OrderByDescending(x => x.c.Index))
                {
                    run.Insert(c.Index, c.Mid with { Z = zm });
                    inserted = true;
                }
            }

            if (!inserted)
            {
                break;
            }
        }

        StepUnresolved(context, runs, floor, tolerance);

        var result = new List<(int, List<P3>)>();
        var maxZ = context.Settings.MaxZ ?? double.MaxValue;
        for (var r = 0; r < runs.Count; r++)
        {
            var current = new List<P3>();
            foreach (var p in runs[r])
            {
                if (p.Z > maxZ + 1e-9)
                {
                    Flush();
                    continue;
                }

                current.Add(p);
            }

            Flush();

            void Flush()
            {
                if (current.Count >= 2)
                {
                    result.Add((r, PassTools.Simplify(current, 0.5 * tolerance)));
                }

                current = [];
            }
        }

        return result;
    }

    /// <summary>
    /// Segments still outside tolerance after refinement (the drop height jumps at a vertical wall, or rises with
    /// infinite slope where a ball meets an edge) would cut diagonally through the edge. They become a vertical move
    /// at the lower end followed by a move at the higher Z, which is above the surface along the whole segment.
    /// </summary>
    private static void StepUnresolved(StrategyContext context, List<List<P3>> runs, double floor, double tolerance)
    {
        var candidates = new List<(int Run, int Index, P3 Mid)>();
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            for (var i = 1; i < run.Count; i++)
            {
                if (Math.Abs(run[i].Z - run[i - 1].Z) > tolerance)
                {
                    candidates.Add((r, i, P3.Lerp(run[i - 1], run[i], 0.5)));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var z = Drop(context, candidates.Select(c => c.Mid).ToList(), floor);
        foreach (var group in candidates.Select((c, k) => (c, z: z[k])).Where(x => Math.Abs(x.z - x.c.Mid.Z) > tolerance).GroupBy(x => x.c.Run))
        {
            var run = runs[group.Key];
            foreach (var (c, _) in group.OrderByDescending(x => x.c.Index))
            {
                var (a, b) = (run[c.Index - 1], run[c.Index]);
                run.Insert(c.Index, a.Z < b.Z ? a with { Z = b.Z } : b with { Z = a.Z });
            }
        }
    }

    private static void DropMissing(StrategyContext context, List<List<P3>> runs, double floor)
    {
        var all = runs.SelectMany(r => r).ToList();
        var z = Drop(context, all, floor);
        var k = 0;
        foreach (var run in runs)
        {
            for (var i = 0; i < run.Count; i++)
            {
                run[i] = run[i] with { Z = z[k++] };
            }
        }
    }

    private static double[] Drop(StrategyContext context, List<P3> points, double floor)
    {
        var xy = new Vector2[points.Count];
        for (var i = 0; i < xy.Length; i++)
        {
            xy[i] = new Vector2((float)points[i].X, (float)points[i].Y);
        }

        var drop = context.Drop(xy);
        var z = new double[drop.Length];
        for (var i = 0; i < z.Length; i++)
        {
            z[i] = float.IsNegativeInfinity(drop[i]) ? floor : Math.Max(drop[i], floor);
        }

        return z;
    }
}
