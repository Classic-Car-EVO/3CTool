using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>
/// Rest machining: simulates the earlier operations on a dexel stock in this setup's frame and clears, level by
/// level, only where material is left that this tool can reach.
/// </summary>
internal static class RestMachining
{
    /// <summary>Largest angle step of simulated simultaneous rotary moves (degrees).</summary>
    private const double MaxAngleStep = 2;

    public static void Run(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var setup = context.Setup;
        var resolution = context.Request.StockResolution is > 0 ? context.Request.StockResolution.Value : Math.Min(0.25, context.Radius / 3);
        DexelGrid grid;
        using (var stock = SimulatedStock(context, (float)resolution))
        {
            context.Report(0.3);
            if (stock.OverflowCount > 0)
            {
                context.Warnings.Add("Rest machining: the stock simulation ran out of dexel segments in places; some removed material is treated as still present.");
            }

            grid = stock.Read(DexelAxis.Z);
        }

        var stockTop = (double)setup.StockBounds.Max.Z;
        var top = Math.Min(settings.MaxZ ?? stockTop, stockTop);
        var levels = context.Levels(top, context.ClearingFloor, settings.Stepdown, flats: true);
        var entryTop = stockTop;
        var origin = new PointD(grid.OriginU, grid.OriginV);
        var mask = new bool[grid.CountU * grid.CountV];
        for (var i = 0; i < levels.Count; i++)
        {
            context.Token.ThrowIfCancellationRequested();
            var z = levels[i];
            for (var ray = 0; ray < mask.Length; ray++)
            {
                mask[ray] = HasMaterialAbove(grid, ray, (float)(z + (0.5 * context.Tolerance)));
            }

            var material = MarchingSquares.MaskRegion(mask, grid.CountU, grid.CountV, origin, grid.Resolution);
            var part = Polygons.Offset(MeshSlicer.Slice(setup.Part, z), settings.RadialStock + grid.Resolution, context.Tolerance);
            material = Polygons.Open(Polygons.Difference(material, part), 1.5 * grid.Resolution, context.Tolerance);
            if (material.Count > 0)
            {
                var area = Polygons.Intersect(context.ClearingRegion(z), Polygons.Offset(material, context.Radius, context.Tolerance));
                if (area.Count > 0)
                {
                    ZLevelRoughing.Clear(context, builder, area, z, entryTop);
                }
            }

            entryTop = z;
            context.Report(0.3 + (0.7 * (i + 1) / levels.Count));
        }
    }

    private static bool HasMaterialAbove(DexelGrid grid, int ray, float z)
    {
        var count = grid.Counts[ray];
        if (count == 0)
        {
            return false;
        }

        // Segments are sorted; the last one ends highest.
        return grid.Segments[(ray * 2 * grid.Capacity) + (2 * count) - 1] > z;
    }

    /// <summary>Stock in this setup's working frame with every earlier operation's feed moves removed.</summary>
    public static DexelStock SimulatedStock(StrategyContext context, float resolution)
    {
        var setup = context.Setup;
        var request = context.Request;
        var margin = new Vector3(2 * resolution);
        var bounds = new BoundingBox(setup.StockBounds.Min - margin, setup.StockBounds.Max + margin);
        var axisAligned = request.Stock.Type == StockType.Box && (!setup.Frame.IsRotary || Math.Abs(Math.IEEERemainder(setup.Frame.BaseAngle, 90)) < 1e-9);
        DexelStock stock;
        if (axisAligned)
        {
            stock = DexelStock.Box(setup.Compute, setup.StockBounds, resolution);
        }
        else
        {
            using var mesh = new GpuMesh(setup.Compute, setup.Stock);
            stock = DexelStock.FromMesh(mesh, bounds, resolution);
        }

        try
        {
            foreach (var cut in request.PreviousCuts)
            {
                context.Token.ThrowIfCancellationRequested();
                var moves = ToolMoves(cut.Toolpath, setup.Frame);
                if (moves.Count > 0)
                {
                    stock.Remove(CutterProfile.FromTool(cut.Tool), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(moves));
                }
            }
        }
        catch
        {
            stock.Dispose();
            throw;
        }

        return stock;
    }

    /// <summary>Feed moves of a toolpath as straight tip sweeps in the working frame of <paramref name="target"/>.</summary>
    public static List<ToolMove> ToolMoves(Toolpath toolpath, SetupFrame target)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(target);
        var result = new List<ToolMove>();
        var moves = toolpath.Moves;
        var frame = toolpath.Frame;
        for (var i = 1; i < moves.Count; i++)
        {
            var (from, to) = (moves[i - 1], moves[i]);
            if (to.Kind == MoveKind.Rapid)
            {
                continue;
            }

            var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(to.A - from.A) / MaxAngleStep));
            var previous = Working(frame, target, from);
            for (var k = 1; k <= pieces; k++)
            {
                var t = (double)k / pieces;
                var tm = (k - 0.5) / pieces;
                var current = Working(frame, target, Lerp(from, to, t));
                var axis = Working(frame, target, Lerp(from, to, tm)).Axis;
                result.Add(new ToolMove(previous.Tip, current.Tip, axis));
                previous = current;
            }
        }

        return result;
    }

    private static ToolpathMove Lerp(ToolpathMove a, ToolpathMove b, double t) =>
        new(b.Kind, a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t), a.A + ((b.A - a.A) * t));

    private static (Vector3 Tip, Vector3 Axis) Working(SetupFrame source, SetupFrame target, ToolpathMove move)
    {
        var (tip, axis) = source.ToolInPart(move);
        var w = target.PartToWcs(tip, target.BaseAngle);
        var a = target.DirectionToWcs(axis, target.BaseAngle);
        return (new Vector3((float)w.X, (float)w.Y, (float)w.Z), Vector3.Normalize(new Vector3((float)a.X, (float)a.Y, (float)a.Z)));
    }
}
