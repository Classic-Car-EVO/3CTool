using Clipper2Lib;

using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>Waterline finishing: contours of the part at constant Z, with extra levels where the surface flattens.</summary>
internal static class Waterline
{
    private const int MaxRefineDepth = 4;

    public static void Run(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var partTop = (double)context.Setup.PartBounds.Max.Z;
        var top = Math.Min(settings.MaxZ ?? partTop, partTop);
        var bottom = Math.Max(context.FinishingFloor, context.Setup.PartBounds.Min.Z);
        var levels = Refine(context, context.Levels(top, bottom, settings.Stepdown, flats: false));
        builder.StayDownLink = (a, b) => context.SurfaceLink(a, b, Math.Max(2 * settings.Stepover, context.Radius));
        for (var i = 0; i < levels.Count; i++)
        {
            context.Token.ThrowIfCancellationRequested();
            var z = levels[i];
            var free = Polygons.Difference(context.Boundary, context.Forbidden(z));
            var pending = Contours(context, z).Select(p => PassTools.Line(p, z)).ToList();
            var reversible = settings.Direction == CutDirection.Both;
            while (pending.Count > 0)
            {
                var (index, flip) = PassTools.Nearest(pending, builder.Current, p => p[0], p => p[^1], reversible);
                var points = pending[index];
                pending.RemoveAt(index);
                if (flip)
                {
                    points.Reverse();
                }

                if (P3.Distance(points[0], points[^1]) < 1e-6 && builder.Current is { } from)
                {
                    points = Rotate(points, from);
                }

                builder.AddPass(new CutPass(points) { Region = free, Contour = true });
            }

            context.Report((i + 1.0) / levels.Count);
        }
    }

    /// <summary>Part contours at <paramref name="z"/> outside keep-outs and inside the boundary, oriented for the cut direction.</summary>
    public static List<PathD> Contours(StrategyContext context, double z)
    {
        var part = context.Forbidden(z, part: true, obstacles: false);
        if (part.Count == 0)
        {
            return [];
        }

        var lines = new PathsD(part.Select(loop => new PathD(loop) { loop[0] }));
        if (context.Setup.Obstacles is not null)
        {
            lines = Polygons.ClipLines(lines, context.Forbidden(z, part: false, obstacles: true), inside: false);
        }

        lines = Polygons.ClipLines(lines, context.Boundary);

        // Climb keeps the part on the right of the cutter.
        var partOnLeft = context.Settings.Direction == CutDirection.Conventional;
        var result = new List<PathD>();
        foreach (var line in lines.Where(l => l.Count >= 2 && Polygons.Length(l, false) > 2 * context.Tolerance))
        {
            if (PartOnLeft(part, line) != partOnLeft)
            {
                line.Reverse();
            }

            result.Add(line);
        }

        return result;
    }

    private static bool PartOnLeft(PathsD part, PathD line)
    {
        var votes = 0;
        for (var k = 1; k < line.Count && Math.Abs(votes) < 3; k++)
        {
            var (a, b) = (line[k - 1], line[k]);
            var length = Polygons.Distance(a, b);
            if (length < 1e-3)
            {
                continue;
            }

            const double probe = 2e-3;
            var mid = new PointD(0.5 * (a.x + b.x), 0.5 * (a.y + b.y));
            var left = new PointD(mid.x - ((b.y - a.y) / length * probe), mid.y + ((b.x - a.x) / length * probe));
            var right = new PointD(mid.x + ((b.y - a.y) / length * probe), mid.y - ((b.x - a.x) / length * probe));
            var (inLeft, inRight) = (Polygons.Contains(part, left), Polygons.Contains(part, right));
            if (inLeft != inRight)
            {
                votes += inLeft ? 1 : -1;
            }
        }

        return votes > 0;
    }

    private static List<P3> Rotate(List<P3> loop, P3 from)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < loop.Count - 1; i++)
        {
            var d = (loop[i] - from).LengthXY;
            if (d < bestDistance)
            {
                (best, bestDistance) = (i, d);
            }
        }

        var rotated = new List<P3>(loop.Count);
        for (var k = 0; k < loop.Count - 1; k++)
        {
            rotated.Add(loop[(best + k) % (loop.Count - 1)]);
        }

        rotated.Add(rotated[0]);
        return rotated;
    }

    /// <summary>Inserts mid levels where the contour moves more than a stepover between levels (shallow areas).</summary>
    private static List<double> Refine(StrategyContext context, List<double> levels)
    {
        var step = context.Settings.Stepover;
        var result = new List<double>();
        for (var i = 0; i < levels.Count; i++)
        {
            if (i > 0)
            {
                Split(levels[i - 1], levels[i], 0);
            }

            result.Add(levels[i]);
        }

        return result;

        void Split(double upper, double lower, int depth)
        {
            if (depth >= MaxRefineDepth)
            {
                return;
            }

            var gap = Polygons.Difference(context.Forbidden(lower, obstacles: false), Polygons.Offset(context.Forbidden(upper, obstacles: false), step, context.Tolerance));
            if (Math.Abs(Polygons.Area(gap)) <= step * step)
            {
                return;
            }

            var mid = 0.5 * (upper + lower);
            Split(upper, mid, depth + 1);
            result.Add(mid);
            Split(mid, lower, depth + 1);
        }
    }
}
