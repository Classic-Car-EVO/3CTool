using Clipper2Lib;

using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>Pass construction and ordering helpers.</summary>
internal static class PassTools
{
    /// <summary>Closed loop at height <paramref name="z"/> (first point repeated), starting at the vertex nearest <paramref name="from"/>.</summary>
    public static List<P3> Loop(PathD path, double z, P3? from)
    {
        var start = 0;
        if (from is { } f)
        {
            var best = double.MaxValue;
            for (var i = 0; i < path.Count; i++)
            {
                var d = Sqr(path[i].x - f.X) + Sqr(path[i].y - f.Y);
                if (d < best)
                {
                    (best, start) = (d, i);
                }
            }
        }

        var points = new List<P3>(path.Count + 1);
        for (var k = 0; k <= path.Count; k++)
        {
            var p = path[(start + k) % path.Count];
            points.Add(new P3(p.x, p.y, z));
        }

        return points;
    }

    public static List<P3> Line(PathD path, double z) => path.Select(p => new P3(p.x, p.y, z)).ToList();

    /// <summary>Picks the item whose start (or end, when reversible) is nearest; returns the index and whether to reverse.</summary>
    public static (int Index, bool Reverse) Nearest<T>(IReadOnlyList<T> items, P3? from, Func<T, P3> start, Func<T, P3> end, bool reversible)
    {
        if (from is not { } f || items.Count <= 1)
        {
            return (0, false);
        }

        var best = double.MaxValue;
        var result = (0, false);
        for (var i = 0; i < items.Count; i++)
        {
            var ds = (start(items[i]) - f).LengthXY;
            if (ds < best)
            {
                (best, result) = (ds, (i, false));
            }

            if (reversible)
            {
                var de = (end(items[i]) - f).LengthXY;
                if (de < best)
                {
                    (best, result) = (de, (i, true));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Parallel lines at <paramref name="angle"/> (degrees from +X) <paramref name="spacing"/> apart covering the
    /// region bounds, clipped to it, directed along +u and sorted by row then u.
    /// </summary>
    public static List<(int Row, PathD Path)> Raster(PathsD region, double angle, double spacing, bool inside = true)
    {
        if (region.Count == 0)
        {
            return [];
        }

        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var path in region)
        {
            foreach (var p in path)
            {
                var u = (p.x * cos) + (p.y * sin);
                var v = (-p.x * sin) + (p.y * cos);
                (minU, maxU, minV, maxV) = (Math.Min(minU, u), Math.Max(maxU, u), Math.Min(minV, v), Math.Max(maxV, v));
            }
        }

        var count = Math.Max(1, (int)Math.Ceiling((maxV - minV) / spacing));
        var step = (maxV - minV) / count;
        var result = new List<(int, PathD)>();
        for (var k = 0; k <= count; k++)
        {
            var v = minV + (k * step);
            var u0 = minU - 1;
            var u1 = maxU + 1;
            PathsD line = [[new PointD((u0 * cos) - (v * sin), (u0 * sin) + (v * cos)), new PointD((u1 * cos) - (v * sin), (u1 * sin) + (v * cos))]];

            // Clipper may return pieces in any direction; normalise to +u.
            foreach (var p in Polygons.ClipLines(line, region, inside).Where(p => p.Count >= 2))
            {
                if ((((p[^1].x - p[0].x) * cos) + ((p[^1].y - p[0].y) * sin)) < 0)
                {
                    p.Reverse();
                }

                result.Add((k, p));
            }
        }

        return result.OrderBy(r => r.Item1).ThenBy(r => (r.Item2[0].x * cos) + (r.Item2[0].y * sin)).ToList();
    }

    /// <summary>
    /// Douglas–Peucker simplification of a 3D polyline. A point is dropped only if both its distance to the chord and
    /// its height (Z) above or below the chord at the same XY stay within <paramref name="tolerance"/>: on steep
    /// slopes the perpendicular distance alone understates the tip height error of drop-cutter paths.
    /// </summary>
    public static List<P3> Simplify(List<P3> points, double tolerance)
    {
        if (points.Count <= 2)
        {
            return points;
        }

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var worst = -1;
            var worstDistance = tolerance;
            for (var i = a + 1; i < b; i++)
            {
                var d = Math.Max(SegmentDistance(points[i], points[a], points[b]), HeightDeviation(points[i], points[a], points[b]));
                if (d > worstDistance)
                {
                    (worst, worstDistance) = (i, d);
                }
            }

            if (worst > 0)
            {
                keep[worst] = true;
                stack.Push((a, worst));
                stack.Push((worst, b));
            }
        }

        return points.Where((_, i) => keep[i]).ToList();
    }

    public static double SegmentDistance(P3 p, P3 a, P3 b)
    {
        var ab = b - a;
        var length2 = (ab.X * ab.X) + (ab.Y * ab.Y) + (ab.Z * ab.Z);
        var t = length2 < 1e-18 ? 0 : Math.Clamp((((p.X - a.X) * ab.X) + ((p.Y - a.Y) * ab.Y) + ((p.Z - a.Z) * ab.Z)) / length2, 0, 1);
        return P3.Distance(p, a + (ab * t));
    }

    /// <summary>|Z| difference between <paramref name="p"/> and chord a–b at p's XY projection onto it (0 for vertical chords).</summary>
    public static double HeightDeviation(P3 p, P3 a, P3 b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length2 = (dx * dx) + (dy * dy);
        if (length2 < 1e-18)
        {
            return 0;
        }

        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length2, 0, 1);
        return Math.Abs(p.Z - (a.Z + ((b.Z - a.Z) * t)));
    }

    /// <summary>Points every <paramref name="step"/> (at most) along a polyline, keeping its vertices.</summary>
    public static List<PointD> Resample(PathD path, double step, bool closed)
    {
        var result = new List<PointD>();
        var count = closed ? path.Count : path.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % path.Count];
            var n = Math.Max(1, (int)Math.Ceiling(Polygons.Distance(a, b) / step));
            for (var k = 0; k < n; k++)
            {
                result.Add(new PointD(a.x + ((b.x - a.x) * k / n), a.y + ((b.y - a.y) * k / n)));
            }
        }

        result.Add(closed ? path[0] : path[^1]);
        return result;
    }

    private static double Sqr(double v) => v * v;
}
