using Clipper2Lib;

namespace ThreeCTool.Geometry;

/// <summary>
/// 2D region and polyline operations (Clipper2, mm, fixed <see cref="Precision"/>). Regions are Clipper-normalized
/// path sets: outer loops counter-clockwise, holes clockwise, the region always on the left of each loop.
/// </summary>
public static class Polygons
{
    /// <summary>Decimal places kept by Clipper (0.1 µm).</summary>
    public const int Precision = 4;

    public static PathsD Union(PathsD subject, FillRule fillRule = FillRule.NonZero) =>
        Clipper.BooleanOp(ClipType.Union, subject, [], fillRule, Precision);

    public static PathsD Union(PathsD subject, PathsD clip) =>
        Clipper.Union(subject, clip, FillRule.NonZero, Precision);

    public static PathsD Difference(PathsD subject, PathsD clip) =>
        Clipper.Difference(subject, clip, FillRule.NonZero, Precision);

    public static PathsD Intersect(PathsD subject, PathsD clip) =>
        Clipper.Intersect(subject, clip, FillRule.NonZero, Precision);

    /// <summary>Grows (positive <paramref name="delta"/>) or shrinks a region with round joins.</summary>
    public static PathsD Offset(PathsD region, double delta, double arcTolerance)
    {
        if (region.Count == 0 || delta == 0)
        {
            return region;
        }

        return Clipper.InflatePaths(region, delta, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance(arcTolerance));
    }

    /// <summary>Morphological opening: removes parts of the region narrower than 2·<paramref name="radius"/>.</summary>
    public static PathsD Open(PathsD region, double radius, double arcTolerance) =>
        Offset(Offset(region, -radius, arcTolerance), radius, arcTolerance);

    /// <summary>Parts of open polylines inside (or outside) a region.</summary>
    public static PathsD ClipLines(PathsD lines, PathsD region, bool inside = true)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        if (region.Count == 0)
        {
            return inside ? [] : lines;
        }

        var clipper = new ClipperD(Precision);
        clipper.AddOpenSubject(lines);
        clipper.AddClip(region);
        var closed = new PathsD();
        var open = new PathsD();
        clipper.Execute(inside ? ClipType.Intersection : ClipType.Difference, FillRule.NonZero, closed, open);
        return open;
    }

    public static double Area(PathsD region) => Clipper.Area(region);

    public static PathsD Rectangle(double minX, double minY, double maxX, double maxY) =>
        [new PathD([new PointD(minX, minY), new PointD(maxX, minY), new PointD(maxX, maxY), new PointD(minX, maxY)])];

    /// <summary>Counter-clockwise circle whose chords deviate at most <paramref name="tolerance"/> from the arc.</summary>
    public static PathD Circle(PointD center, double radius, double tolerance)
    {
        var segments = CircleSegments(radius, tolerance);
        var path = new PathD(segments);
        for (var i = 0; i < segments; i++)
        {
            var (sin, cos) = Math.SinCos(2 * Math.PI * i / segments);
            path.Add(new PointD(center.x + (radius * cos), center.y + (radius * sin)));
        }

        return path;
    }

    /// <summary>Chord count for a full circle within <paramref name="tolerance"/> (at least 8).</summary>
    public static int CircleSegments(double radius, double tolerance)
    {
        if (radius <= tolerance)
        {
            return 8;
        }

        var step = 2 * Math.Acos(1 - (tolerance / radius));
        return Math.Clamp((int)Math.Ceiling(2 * Math.PI / step), 8, 4096);
    }

    /// <summary>Non-zero winding test against all loops of a normalized region (on the boundary counts as inside).</summary>
    public static bool Contains(PathsD region, PointD point)
    {
        var winding = 0;
        foreach (var path in region)
        {
            var result = Clipper.PointInPolygon(point, path, Precision);
            if (result == PointInPolygonResult.IsOn)
            {
                return true;
            }

            if (result == PointInPolygonResult.IsInside)
            {
                winding += Clipper.IsPositive(path) ? 1 : -1;
            }
        }

        return winding > 0;
    }

    /// <summary>Distance from <paramref name="point"/> to the nearest boundary edge of the region.</summary>
    public static double DistanceToBoundary(PathsD region, PointD point)
    {
        var best = double.PositiveInfinity;
        foreach (var path in region)
        {
            for (var i = 0; i < path.Count; i++)
            {
                best = Math.Min(best, SegmentDistanceSquared(point, path[i], path[(i + 1) % path.Count]));
            }
        }

        return Math.Sqrt(best);
    }

    /// <summary>True when the straight segment a→b stays inside the region (touching the boundary is allowed).</summary>
    public static bool ContainsSegment(PathsD region, PointD a, PointD b)
    {
        if (!Contains(region, a) || !Contains(region, b))
        {
            return false;
        }

        foreach (var path in region)
        {
            for (var i = 0; i < path.Count; i++)
            {
                if (ProperlyIntersect(a, b, path[i], path[(i + 1) % path.Count]))
                {
                    return false;
                }
            }
        }

        return Contains(region, new PointD((a.x + b.x) / 2, (a.y + b.y) / 2));
    }

    public static double Length(PathD path, bool closed)
    {
        double length = 0;
        var count = closed ? path.Count : path.Count - 1;
        for (var i = 0; i < count; i++)
        {
            length += Distance(path[i], path[(i + 1) % path.Count]);
        }

        return length;
    }

    public static double Distance(PointD a, PointD b) => Math.Sqrt(Sqr(a.x - b.x) + Sqr(a.y - b.y));

    public static double SegmentDistanceSquared(PointD p, PointD a, PointD b)
    {
        var dx = b.x - a.x;
        var dy = b.y - a.y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared > 0 ? Math.Clamp((((p.x - a.x) * dx) + ((p.y - a.y) * dy)) / lengthSquared, 0, 1) : 0;
        return Sqr(p.x - (a.x + (t * dx))) + Sqr(p.y - (a.y + (t * dy)));
    }

    /// <summary>Clipper2 scales the arc tolerance with the precision; never pass 0 (that means "default").</summary>
    private static double ArcTolerance(double tolerance) => Math.Max(tolerance, Math.Pow(10, -Precision));

    private static bool ProperlyIntersect(PointD a, PointD b, PointD c, PointD d)
    {
        var d1 = Cross(c, d, a);
        var d2 = Cross(c, d, b);
        var d3 = Cross(a, b, c);
        var d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(PointD o, PointD a, PointD b) => ((a.x - o.x) * (b.y - o.y)) - ((a.y - o.y) * (b.x - o.x));

    private static double Sqr(double value) => value * value;
}
