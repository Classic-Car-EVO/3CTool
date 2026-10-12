using ThreeCTool.Toolpaths;

namespace ThreeCTool.Post;

/// <summary>Arc replacing points <c>start..End</c> of a polyline: centre in the plane axes, signed sweep (positive = G3).</summary>
internal readonly record struct FittedArc(int End, int Plane, double CenterFirst, double CenterSecond, double Sweep)
{
    public bool Clockwise => Sweep < 0;
}

/// <summary>
/// Replaces runs of short linear moves by circular (or helical) arcs in the G17, G18 or G19 plane when every point
/// and every segment midpoint of the run lies within the tolerance of the arc.
/// </summary>
internal static class ArcFitter
{
    public const int MinSegments = 3;

    public const int MaxPoints = 1000;

    public const double MaxSweep = 1.5 * Math.PI;

    public const double MaxRadius = 2000;

    /// <summary>Smallest radius and chord fitted; shorter arcs gain nothing and suffer from the 4-decimal rounding.</summary>
    public const double MinRadius = 0.05;

    private static readonly int[] Planes = [17, 18, 19];

    /// <summary>Plane axis indices (X = 0, Y = 1, Z = 2): first, second and the helical axis, as in the controller.</summary>
    public static (int First, int Second, int Normal) PlaneAxes(int plane) => plane switch
    {
        18 => (2, 0, 1),
        19 => (1, 2, 0),
        _ => (0, 1, 2),
    };

    public static double Coordinate(P3 p, int axis) => axis switch
    {
        0 => p.X,
        1 => p.Y,
        _ => p.Z,
    };

    /// <summary>Longest arc starting at <paramref name="start"/> spanning at least <see cref="MinSegments"/> segments, or null.</summary>
    public static FittedArc? Fit(IReadOnlyList<P3> points, int start, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        var last = Math.Min(points.Count - 1, start + MaxPoints - 1);
        var first = start + MinSegments;
        if (first > last || Any(first) is not { } best)
        {
            return null;
        }

        // Galloping search for the first failing end, then bisection between the last fit and that failure.
        var (good, bad, step) = (first, last + 1, 1);
        while (good + step <= last)
        {
            if (Any(good + step) is { } fit)
            {
                (best, good, step) = (fit, good + step, step * 2);
            }
            else
            {
                bad = good + step;
                break;
            }
        }

        if (bad == last + 1 && good < last)
        {
            if (Any(last) is { } fit)
            {
                return fit;
            }

            bad = last;
        }

        while (bad - good > 1)
        {
            var middle = (good + bad) / 2;
            if (Any(middle) is { } fit)
            {
                (best, good) = (fit, middle);
            }
            else
            {
                bad = middle;
            }
        }

        return best;

        FittedArc? Any(int end)
        {
            foreach (var plane in Planes)
            {
                if (TryFit(points, start, end, plane, tolerance) is { } fit)
                {
                    return fit;
                }
            }

            return null;
        }
    }

    private static FittedArc? TryFit(IReadOnlyList<P3> points, int start, int end, int plane, double tolerance)
    {
        var (a, b, n) = PlaneAxes(plane);
        var (ua, va) = (Coordinate(points[start], a), Coordinate(points[start], b));
        var (ue, ve) = (Coordinate(points[end], a), Coordinate(points[end], b));
        var mid = points[(start + end) / 2];
        var (um, vm) = (Coordinate(mid, a), Coordinate(mid, b));

        // Circle through start, middle and end.
        var d = 2 * ((ua * (vm - ve)) + (um * (ve - va)) + (ue * (va - vm)));
        if (Math.Abs(d) < 1e-12)
        {
            return null;
        }

        var sa = (ua * ua) + (va * va);
        var sm = (um * um) + (vm * vm);
        var se = (ue * ue) + (ve * ve);
        var cu = ((sa * (vm - ve)) + (sm * (ve - va)) + (se * (va - vm))) / d;
        var cv = ((sa * (ue - um)) + (sm * (ua - ue)) + (se * (um - ua))) / d;

        // Project the centre onto the perpendicular bisector of the chord so both ends have the same radius (the
        // controller rejects arcs whose start and end radii differ).
        var (chordU, chordV) = (ue - ua, ve - va);
        var chord = Math.Sqrt((chordU * chordU) + (chordV * chordV));
        if (chord < MinRadius)
        {
            return null;
        }

        var (nu, nv) = (-chordV / chord, chordU / chord);
        var (mu, mv) = (0.5 * (ua + ue), 0.5 * (va + ve));
        var along = ((cu - mu) * nu) + ((cv - mv) * nv);
        (cu, cv) = (mu + (along * nu), mv + (along * nv));
        var radius = Math.Sqrt(((ua - cu) * (ua - cu)) + ((va - cv) * (va - cv)));
        if (radius is < MinRadius or > MaxRadius)
        {
            return null;
        }

        // Angles must advance monotonically; the sweep must stay below the limit.
        var count = end - start;
        var cumulative = new double[count + 1];
        var previous = Math.Atan2(va - cv, ua - cu);
        var sign = 0;
        for (var i = 1; i <= count; i++)
        {
            var p = points[start + i];
            var angle = Math.Atan2(Coordinate(p, b) - cv, Coordinate(p, a) - cu);
            var step = Math.IEEERemainder(angle - previous, 2 * Math.PI);
            if (Math.Abs(step) < 1e-12 || Math.Abs(step) >= Math.PI / 2)
            {
                return null;
            }

            var s = Math.Sign(step);
            if (sign != 0 && s != sign)
            {
                return null;
            }

            sign = s;
            cumulative[i] = cumulative[i - 1] + step;
            previous = angle;
        }

        var sweep = cumulative[count];
        if (Math.Abs(sweep) > MaxSweep)
        {
            return null;
        }

        // Every point and segment midpoint within tolerance: radially in the plane, linearly along the helix axis.
        var (wa, we) = (Coordinate(points[start], n), Coordinate(points[end], n));
        for (var i = 1; i <= count; i++)
        {
            var p = points[start + i];
            var q = points[start + i - 1];
            if (Math.Abs(Radius(p) - radius) > tolerance ||
                Math.Abs(Coordinate(p, n) - (wa + ((we - wa) * cumulative[i] / sweep))) > tolerance)
            {
                return null;
            }

            var middle = P3.Lerp(q, p, 0.5);
            var expected = wa + ((we - wa) * 0.5 * (cumulative[i - 1] + cumulative[i]) / sweep);
            if (Math.Abs(Radius(middle) - radius) > tolerance || Math.Abs(Coordinate(middle, n) - expected) > tolerance)
            {
                return null;
            }
        }

        return new FittedArc(end, plane, cu, cv, sweep);

        double Radius(P3 p)
        {
            var (du, dv) = (Coordinate(p, a) - cu, Coordinate(p, b) - cv);
            return Math.Sqrt((du * du) + (dv * dv));
        }
    }
}
