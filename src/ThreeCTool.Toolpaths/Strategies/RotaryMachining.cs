using System.Numerics;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>
/// Simultaneous 4-axis strategies on the A axis. Passes are planned in unwrapped coordinates (X, arc length at the
/// stock radius, tip radius) so the generic linker applies; the move map turns them into X, Z = radius and A.
/// </summary>
internal static class RotaryMachining
{
    private const int RefineRounds = 4;

    public static ToolpathBuilder NewBuilder(StrategyContext context)
    {
        var reference = Reference(context);
        var clearance = context.Setup.StockRadius + context.Settings.RetractClearance;
        return new ToolpathBuilder(context.Settings, context.Radius, clearance, (p, kind) => new ToolpathMove(kind, p.X, 0, p.Z, p.Y / reference * 180 / Math.PI));
    }

    public static void Finishing(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var reference = Reference(context);
        var (x0, x1) = XRange(context);
        var sample = ParallelFinishing.SampleStep(context);
        var polylines = Pattern(settings.RotaryPattern, x0, x1, settings.Stepover, sample, reference);
        context.Report(0.2);
        var runs = new List<List<P3>>();
        foreach (var polyline in polylines)
        {
            var dropped = Drop(context, polyline);
            Refine(context, polyline, dropped);
            runs.AddRange(Split(polyline, dropped, reference).Select(r => PassTools.Simplify(r, 0.5 * context.Tolerance)));
        }

        context.Report(0.8);
        builder.StayDownLink = (a, b) => SurfaceLink(context, a, b, reference, Math.Max(2 * settings.Stepover, context.Radius));
        Emit(builder, runs, settings.Direction, inMaterial: false, double.NaN);
    }

    public static void Roughing(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var reference = Reference(context);
        var (x0, x1) = XRange(context);
        var sample = Math.Max(ParallelFinishing.SampleStep(context), context.Radius / 4);
        var polylines = Pattern(settings.RotaryPattern, x0, x1, settings.Stepover, sample, reference);
        var drops = new List<List<double>>(polylines.Count);
        foreach (var polyline in polylines)
        {
            // Refined once: the linear interpolation of max(layer, drop) then stays within tolerance on every layer.
            var dropped = Drop(context, polyline);
            Refine(context, polyline, dropped);
            drops.Add(dropped);
        }
        var contact = drops.SelectMany(d => d).Where(z => !double.IsNegativeInfinity(z)).DefaultIfEmpty(0).Min();
        var floor = Math.Max(contact, settings.MinZ ?? 0);
        var outer = context.Setup.StockRadius;
        var layers = new List<double>();
        var count = Math.Max(1, (int)Math.Ceiling((outer - floor) / settings.Stepdown));
        for (var k = 1; k <= count; k++)
        {
            layers.Add(outer - ((outer - floor) * k / count));
        }

        builder.StayDownLink = (a, b) => SurfaceLink(context, a, b, reference, Math.Max(2 * settings.Stepover, context.Radius));
        var gap = 2 * context.Tool.Diameter;
        var previous = outer;
        for (var l = 0; l < layers.Count; l++)
        {
            context.Token.ThrowIfCancellationRequested();
            var rho = layers[l];
            var runs = new List<List<P3>>();
            for (var p = 0; p < polylines.Count; p++)
            {
                var points = polylines[p];
                var current = new List<P3>();
                var air = new List<P3>();
                for (var i = 0; i < points.Count; i++)
                {
                    var target = Math.Max(rho, double.IsNegativeInfinity(drops[p][i]) ? floor : drops[p][i]);
                    var point = new P3(points[i].X, points[i].A * Math.PI / 180 * reference, target);
                    if (target >= previous - 1e-6)
                    {
                        // Above the previous layer: nothing to cut; long stretches become gaps.
                        air.Add(point);
                        if (Length(air) > gap)
                        {
                            Flush();
                        }

                        continue;
                    }

                    current.AddRange(air);
                    air.Clear();
                    current.Add(point);
                }

                Flush();

                void Flush()
                {
                    if (current.Count >= 2)
                    {
                        runs.Add(PassTools.Simplify(current, 0.5 * context.Tolerance));
                    }

                    current = [];
                    if (air.Count > 0)
                    {
                        air.RemoveRange(0, air.Count - 1);
                    }
                }
            }

            // Alternate the pattern direction per layer so layers chain without long returns.
            if (l % 2 == 1)
            {
                runs.Reverse();
                runs.ForEach(r => r.Reverse());
            }

            Emit(builder, runs, settings.Direction == CutDirection.Both ? CutDirection.Both : CutDirection.Climb, inMaterial: true, previous);
            previous = rho;
            context.Report((l + 1.0) / layers.Count);
        }
    }

    /// <summary>Arc-length reference radius for the unwrapped A coordinate.</summary>
    private static double Reference(StrategyContext context) => Math.Max(context.Setup.StockRadius, 1);

    private static (double X0, double X1) XRange(StrategyContext context)
    {
        var part = context.Setup.PartBounds;
        var stock = context.Setup.StockBounds;
        var offset = context.Settings.BoundaryOffset;
        return (Math.Max(part.Min.X - offset, stock.Min.X), Math.Min(part.Max.X + offset, stock.Max.X));
    }

    /// <summary>(X, A°) polylines of a pattern; A is unwrapped (continuous).</summary>
    private static List<List<(double X, double A)>> Pattern(RotaryPattern pattern, double x0, double x1, double stepover, double sample, double reference)
    {
        var length = Math.Max(x1 - x0, 0);
        var n = Math.Max(1, (int)Math.Ceiling(length / stepover));
        var angleStep = sample / reference * 180 / Math.PI;
        var perTurn = Math.Max(16, (int)Math.Ceiling(360 / angleStep));
        var result = new List<List<(double, double)>>();
        switch (pattern)
        {
            case RotaryPattern.Spiral:
            {
                var spiral = new List<(double, double)>();
                var total = n * perTurn;
                for (var k = 0; k <= total; k++)
                {
                    var t = (double)k / total;
                    spiral.Add((x0 + (length * t), 360.0 * n * t));
                }

                // Finishing turns at both ends so the end faces of the spiral are fully covered.
                result.Add([.. Ring(x0, -360, perTurn), .. spiral, .. Ring(x1, 360.0 * n, perTurn).Skip(1)]);
                break;
            }

            case RotaryPattern.Rings:
                for (var k = 0; k <= n; k++)
                {
                    result.Add(Ring(x0 + (length * k / n), 360.0 * k, perTurn));
                }

                break;

            default:
            {
                var lines = Math.Max(4, (int)Math.Ceiling(2 * Math.PI * reference / stepover));
                var along = Math.Max(1, (int)Math.Ceiling(length / sample));
                for (var k = 0; k < lines; k++)
                {
                    var a = 360.0 * k / lines;
                    var line = new List<(double, double)>(along + 1);
                    for (var i = 0; i <= along; i++)
                    {
                        line.Add((x0 + (length * i / along), a));
                    }

                    result.Add(line);
                }

                break;
            }
        }

        return result;
    }

    private static List<(double X, double A)> Ring(double x, double startAngle, int perTurn)
    {
        var ring = new List<(double, double)>(perTurn + 1);
        for (var k = 0; k <= perTurn; k++)
        {
            ring.Add((x, startAngle + (360.0 * k / perTurn)));
        }

        return ring;
    }

    private static List<double> Drop(StrategyContext context, List<(double X, double A)> points)
    {
        context.Token.ThrowIfCancellationRequested();
        var xa = new Vector2[points.Count];
        for (var i = 0; i < xa.Length; i++)
        {
            xa[i] = new Vector2((float)points[i].X, (float)Math.IEEERemainder(points[i].A, 360));
        }

        return [.. context.Stocked.DropRotary(context.Setup, xa).Select(z => (double)z)];
    }

    /// <summary>Inserts midpoints where the tip radius deviates from the chord by more than the tolerance.</summary>
    private static void Refine(StrategyContext context, List<(double X, double A)> points, List<double> radius)
    {
        for (var round = 0; round < RefineRounds; round++)
        {
            var mids = new List<(int Index, (double X, double A) Point, double Linear)>();
            for (var i = 1; i < points.Count; i++)
            {
                if (double.IsNegativeInfinity(radius[i]) || double.IsNegativeInfinity(radius[i - 1]))
                {
                    continue;
                }

                var mid = (0.5 * (points[i - 1].X + points[i].X), 0.5 * (points[i - 1].A + points[i].A));
                mids.Add((i, mid, 0.5 * (radius[i - 1] + radius[i])));
            }

            if (mids.Count == 0)
            {
                return;
            }

            var dropped = Drop(context, [.. mids.Select(m => m.Point)]);
            var inserted = false;
            for (var k = mids.Count - 1; k >= 0; k--)
            {
                if (!double.IsNegativeInfinity(dropped[k]) && Math.Abs(dropped[k] - mids[k].Linear) > context.Tolerance)
                {
                    points.Insert(mids[k].Index, mids[k].Point);
                    radius.Insert(mids[k].Index, dropped[k]);
                    inserted = true;
                }
            }

            if (!inserted)
            {
                return;
            }
        }
    }

    /// <summary>Unwrapped runs, split where nothing is below the tool.</summary>
    private static List<List<P3>> Split(List<(double X, double A)> points, List<double> radius, double reference)
    {
        var runs = new List<List<P3>>();
        var current = new List<P3>();
        for (var i = 0; i < points.Count; i++)
        {
            if (double.IsNegativeInfinity(radius[i]))
            {
                if (current.Count >= 2)
                {
                    runs.Add(current);
                }

                current = [];
                continue;
            }

            current.Add(new P3(points[i].X, points[i].A * Math.PI / 180 * reference, radius[i]));
        }

        if (current.Count >= 2)
        {
            runs.Add(current);
        }

        return runs;
    }

    private static void Emit(ToolpathBuilder builder, List<List<P3>> runs, CutDirection direction, bool inMaterial, double entryTop)
    {
        var reversible = direction == CutDirection.Both;
        if (direction == CutDirection.Conventional)
        {
            runs.ForEach(r => r.Reverse());
        }

        var pending = runs.ToList();
        while (pending.Count > 0)
        {
            var (index, flip) = PassTools.Nearest(pending, builder.Current, r => r[0], r => r[^1], reversible);
            var points = pending[index];
            pending.RemoveAt(index);
            if (flip)
            {
                points.Reverse();
            }

            builder.AddPass(new CutPass(points) { InMaterial = inMaterial, EntryTop = entryTop });
        }
    }

    /// <summary>Stay-down link over the part in unwrapped coordinates (lifted to the rotary drop radius).</summary>
    private static List<P3>? SurfaceLink(StrategyContext context, P3 from, P3 to, double reference, double maxDistance)
    {
        var d = (to - from).LengthXY;
        if (d > maxDistance)
        {
            return null;
        }

        var count = Math.Max(1, (int)Math.Ceiling(d / context.LinkSampleStep));
        if (count == 1)
        {
            return [];
        }

        var samples = new List<(double X, double A)>(count - 1);
        for (var k = 1; k < count; k++)
        {
            var p = P3.Lerp(from, to, (double)k / count);
            samples.Add((p.X, p.Y / reference * 180 / Math.PI));
        }

        var drop = Drop(context, samples);
        var link = new List<P3>(count - 1);
        for (var k = 1; k < count; k++)
        {
            var p = P3.Lerp(from, to, (double)k / count);
            link.Add(p with { Z = double.IsNegativeInfinity(drop[k - 1]) ? p.Z : Math.Max(p.Z, drop[k - 1]) });
        }

        return link;
    }

    private static double Length(List<P3> points)
    {
        double length = 0;
        for (var i = 1; i < points.Count; i++)
        {
            length += P3.Distance(points[i - 1], points[i]);
        }

        return length;
    }
}
