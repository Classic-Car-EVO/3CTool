using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Simulation;

/// <summary>Initial in-process stock in the part frame, with room for deposited material.</summary>
internal static class StockBuilder
{
    public static BoundingBox StockBounds(SimulationJob job)
    {
        var s = job.Stock;
        return s.Type switch
        {
            StockType.Box => new BoundingBox(
                new Vector3((float)s.OriginX, (float)s.OriginY, (float)s.OriginZ),
                new Vector3((float)(s.OriginX + s.SizeX), (float)(s.OriginY + s.SizeY), (float)(s.OriginZ + s.SizeZ))),
            StockType.Cylinder => new BoundingBox(
                new Vector3((float)s.OriginX, (float)(s.OriginY - (s.Diameter / 2)), (float)(s.OriginZ - (s.Diameter / 2))),
                new Vector3((float)(s.OriginX + s.Length), (float)(s.OriginY + (s.Diameter / 2)), (float)(s.OriginZ + (s.Diameter / 2)))),
            _ => (job.StockMesh ?? throw new ArgumentException("Mesh stock requires SimulationJob.StockMesh.", nameof(job))).Bounds,
        };
    }

    /// <summary>Stock bounds grown to everything deposition steps can add.</summary>
    public static BoundingBox GridBounds(SimulationJob job, IReadOnlyList<PreparedStep> steps)
    {
        var bounds = StockBounds(job);
        foreach (var step in steps.Where(s => !s.IsMilling))
        {
            foreach (var s in step.Segments.Where(s => s.Active))
            {
                var grow = new Vector3((s.Width / 2) + s.Height);
                bounds = bounds.Union(new BoundingBox(Vector3.Min(s.Move.From, s.Move.To) - grow, Vector3.Max(s.Move.From, s.Move.To) + grow));
            }
        }

        return bounds;
    }

    public static float Resolution(SimulationJob job, BoundingBox bounds)
    {
        var o = job.Options;
        if (o.Resolution is { } fixedResolution)
        {
            return fixedResolution;
        }

        var size = bounds.Size;
        var area = ((double)size.X * size.Y) + ((double)size.Y * size.Z) + ((double)size.Z * size.X);
        return Math.Clamp((float)Math.Sqrt(area / Math.Max(o.TargetRays, 1)), o.MinResolution, o.MaxResolution);
    }

    public static DexelStock Create(SimulationJob job, IReadOnlyList<PreparedStep> steps, out float resolution)
    {
        var stockBounds = StockBounds(job);
        var grid = GridBounds(job, steps);
        resolution = Resolution(job, grid);
        var s = job.Stock;
        var exact = grid == stockBounds;
        if (exact && s.Type == StockType.Box)
        {
            return DexelStock.Box(job.Compute, stockBounds, resolution);
        }

        if (exact && s.Type == StockType.Cylinder && s.OriginY == 0 && s.OriginZ == 0)
        {
            return DexelStock.Cylinder(job.Compute, (float)s.OriginX, (float)(s.OriginX + s.Length), (float)(s.Diameter / 2), resolution);
        }

        var margin = new Vector3(2 * resolution);
        var mesh = s.Type switch
        {
            StockType.Box => SolidMeshes.Box(stockBounds.Min, stockBounds.Max, "stock"),
            StockType.Cylinder => SolidMeshes.XCylinder(s.OriginX, s.OriginX + s.Length, s.OriginY, s.OriginZ, s.Diameter / 2, Math.Min(0.01, resolution / 4.0), "stock"),
            _ => job.StockMesh!,
        };
        using var gpu = new GpuMesh(job.Compute, mesh);
        return DexelStock.FromMesh(gpu, new BoundingBox(grid.Min - margin, grid.Max + margin), resolution);
    }
}
