using Clipper2Lib;

using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>Z-level roughing: each level's clearing region is cleared with offset rings or a raster plus a contour.</summary>
internal static class ZLevelRoughing
{
    public static void Run(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var stockTop = (double)context.Setup.StockBounds.Max.Z;
        var top = Math.Min(settings.MaxZ ?? stockTop, stockTop);
        var levels = context.Levels(top, context.ClearingFloor, settings.Stepdown, flats: true);
        var entryTop = stockTop;
        for (var i = 0; i < levels.Count; i++)
        {
            var z = levels[i];
            Clear(context, builder, context.ClearingRegion(z), z, entryTop);
            entryTop = z;
            context.Report((i + 1.0) / levels.Count);
        }
    }

    /// <summary>Clears <paramref name="region"/> at height <paramref name="z"/>; material reaches up to <paramref name="entryTop"/>.</summary>
    public static void Clear(StrategyContext context, ToolpathBuilder builder, PathsD region, double z, double entryTop)
    {
        builder.StayDownLink = (a, b) => Math.Abs(a.Z - b.Z) < 1e-9 && Polygons.ContainsSegment(region, new PointD(a.X, a.Y), new PointD(b.X, b.Y)) ? [] : null;
        var components = Polygons.Components(region).Where(c => Math.Abs(Polygons.Area(c)) > context.Tolerance * context.Tolerance).ToList();
        while (components.Count > 0)
        {
            var (index, _) = PassTools.Nearest(components, builder.Current, c => new P3(c[0][0].x, c[0][0].y, z), _ => default, false);
            var component = components[index];
            components.RemoveAt(index);
            context.Token.ThrowIfCancellationRequested();
            if (context.Settings.Pattern == ClearingPattern.Raster)
            {
                Raster(context, builder, component, z, entryTop);
            }
            else
            {
                Offset(context, builder, component, z, entryTop);
            }
        }
    }

    private static void Offset(StrategyContext context, ToolpathBuilder builder, PathsD component, double z, double entryTop)
    {
        var step = context.Settings.Stepover;
        var rings = new List<PathsD>();
        var distance = 0.0;
        while (true)
        {
            var ring = Polygons.Offset(component, -distance, context.Tolerance);
            if (ring.Count == 0)
            {
                break;
            }

            rings.Add(ring);
            distance += step;
        }

        // Rings closer than the tool radius so the last pocket core is fully cleared.
        var fine = 0.9 * context.Radius;
        if (fine < step && rings.Count > 0)
        {
            var extra = distance - step + fine;
            while (true)
            {
                var ring = Polygons.Offset(component, -extra, context.Tolerance);
                if (ring.Count == 0)
                {
                    break;
                }

                rings.Add(ring);
                extra += fine;
            }

            rings.Sort((a, b) => Math.Abs(Polygons.Area(b)).CompareTo(Math.Abs(Polygons.Area(a))));
        }

        for (var r = rings.Count - 1; r >= 0; r--)
        {
            EmitLoops(context, builder, component, rings[r], z, entryTop);
        }
    }

    private static void Raster(StrategyContext context, ToolpathBuilder builder, PathsD component, double z, double entryTop)
    {
        var settings = context.Settings;
        var lines = PassTools.Raster(component, settings.RasterAngle, settings.Stepover).Select(l => l.Path).ToList();
        var reversible = settings.Direction == CutDirection.Both;
        var reverse = settings.Direction == CutDirection.Conventional;
        while (lines.Count > 0)
        {
            var (index, flip) = PassTools.Nearest(lines, builder.Current, l => Start(l, reverse, z), l => Start(l, !reverse, z), reversible);
            var line = lines[index];
            lines.RemoveAt(index);
            var points = PassTools.Line(line, z);
            if (reverse ^ flip)
            {
                points.Reverse();
            }

            builder.AddPass(new CutPass(points) { EntryTop = entryTop, InMaterial = true, Region = component });
        }

        EmitLoops(context, builder, component, component, z, entryTop);
    }

    private static P3 Start(PathD line, bool fromEnd, double z) => fromEnd ? new P3(line[^1].x, line[^1].y, z) : new P3(line[0].x, line[0].y, z);

    private static void EmitLoops(StrategyContext context, ToolpathBuilder builder, PathsD component, PathsD loops, double z, double entryTop)
    {
        var pending = loops.Where(l => l.Count >= 3 && Polygons.Length(l, true) > 4 * context.Tolerance).ToList();
        var conventional = context.Settings.Direction == CutDirection.Conventional;
        while (pending.Count > 0)
        {
            var (index, _) = PassTools.Nearest(pending, builder.Current, l => Nearest(l, builder.Current, z), _ => default, false);
            var loop = pending[index];
            pending.RemoveAt(index);
            var points = PassTools.Loop(loop, z, builder.Current);
            if (conventional)
            {
                points.Reverse();
            }

            builder.AddPass(new CutPass(points) { EntryTop = entryTop, InMaterial = true, Region = component });
        }
    }

    private static P3 Nearest(PathD loop, P3? from, double z)
    {
        if (from is not { } f)
        {
            return new P3(loop[0].x, loop[0].y, z);
        }

        var best = loop[0];
        var bestDistance = double.MaxValue;
        foreach (var p in loop)
        {
            var d = ((p.x - f.X) * (p.x - f.X)) + ((p.y - f.Y) * (p.y - f.Y));
            if (d < bestDistance)
            {
                (best, bestDistance) = (p, d);
            }
        }

        return new P3(best.x, best.y, z);
    }
}
