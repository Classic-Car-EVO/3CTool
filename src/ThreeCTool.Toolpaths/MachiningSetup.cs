using System.Collections.Concurrent;
using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths;

/// <summary>
/// A setup prepared for toolpath generation: part, stock and setup keep-outs transformed into the working frame
/// (the WCS at the setup's base A angle, tool along +Z) and uploaded to the compute device.
/// </summary>
public sealed class MachiningSetup : IDisposable
{
    private const double PrimitiveTolerance = 0.01;

    private readonly ConcurrentDictionary<double, PathsD> _sections = new();
    private readonly Lazy<PathsD> _stockFootprint;
    private readonly Lazy<PathsD> _partSilhouette;

    private MachiningSetup(ComputeContext compute, SetupFrame frame, Mesh part, Mesh stock, Mesh? obstacles, double stockRadius)
    {
        Compute = compute;
        Frame = frame;
        Part = part;
        Stock = stock;
        Obstacles = obstacles;
        PartGpu = new GpuMesh(compute, part);
        ObstaclesGpu = obstacles is null ? null : new GpuMesh(compute, obstacles);
        StockRadius = stockRadius;
        _stockFootprint = new(() => MeshSlicer.Silhouette(Stock));
        _partSilhouette = new(() => MeshSlicer.Silhouette(Part));
    }

    public ComputeContext Compute { get; }

    public SetupFrame Frame { get; }

    public Mesh Part { get; }

    public GpuMesh PartGpu { get; }

    public Mesh Stock { get; }

    /// <summary>Setup keep-out zones (clamps, vise jaws); null when there are none.</summary>
    public Mesh? Obstacles { get; }

    public GpuMesh? ObstaclesGpu { get; }

    public BoundingBox PartBounds => Part.Bounds;

    public BoundingBox StockBounds => Stock.Bounds;

    /// <summary>Largest distance of the stock from the X axis (the rotary axis in rotary frames).</summary>
    public double StockRadius { get; }

    /// <summary>Highest point of stock, part and keep-outs in the working frame.</summary>
    public double TopZ => Math.Max(Math.Max(StockBounds.Max.Z, PartBounds.Max.Z), Obstacles?.Bounds.Max.Z ?? double.MinValue);

    public PathsD StockFootprint => _stockFootprint.Value;

    public PathsD PartSilhouette => _partSilhouette.Value;

    public static MachiningSetup Create(ToolpathRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stockPart = StockMesh(request);
        var frame = CreateFrame(request.Setup, request.Stock, stockPart.Bounds);
        var matrix = frame.PartToWcsMatrix(frame.BaseAngle);
        var keepOuts = request.KeepOuts
            .Where(k => k.Attachment == KeepOutAttachment.Setup && k.SetupId == request.Setup.Id)
            .Select(k => SolidMeshes.FromSolid(k.Solid, PrimitiveTolerance))
            .ToList();
        var obstacles = keepOuts.Count == 0 ? null : Mesh.Merge(keepOuts, "keep-outs").Transform(matrix);
        var stock = stockPart.Transform(matrix);
        var radius = request.Stock.Type == StockType.Cylinder
            ? request.Stock.Diameter / 2
            : stock.Vertices.Max(v => Math.Sqrt(((double)v.Y * v.Y) + ((double)v.Z * v.Z)));
        return new MachiningSetup(request.Compute, frame, request.Part.Transform(matrix), stock, obstacles, radius);
    }

    public static SetupFrame CreateFrame(Setup setup, Stock stock, BoundingBox stockBounds)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(stock);
        var (axisY, axisZ) = stock.Type == StockType.Cylinder
            ? (stock.OriginY, stock.OriginZ)
            : (0.5 * ((double)stockBounds.Min.Y + stockBounds.Max.Y), 0.5 * ((double)stockBounds.Min.Z + stockBounds.Max.Z));
        var centerX = 0.5 * ((double)stockBounds.Min.X + stockBounds.Max.X);
        var (x, y, z) = setup.WcsReference switch
        {
            WcsReference.StockTopCenter => (centerX, 0.5 * ((double)stockBounds.Min.Y + stockBounds.Max.Y), (double)stockBounds.Max.Z),
            WcsReference.StockTopFrontLeft => (stockBounds.Min.X, stockBounds.Min.Y, stockBounds.Max.Z),
            WcsReference.PartOrigin => (0.0, 0.0, 0.0),
            _ => (stockBounds.Min.X, axisY, axisZ),
        };
        x += setup.WcsOffsetX;

        // Rotary setups keep the WCS Y/Z origin on the rotation axis; only X is free.
        return setup.Orientation == SetupOrientation.ThreeAxis
            ? new SetupFrame(setup.Orientation, x, y + setup.WcsOffsetY, z + setup.WcsOffsetZ, 0)
            : new SetupFrame(setup.Orientation, x, axisY, axisZ, setup.Orientation == SetupOrientation.Indexed ? setup.IndexAngle : 0);
    }

    /// <summary>Stock material just below height <paramref name="z"/> (cached).</summary>
    public PathsD StockSection(double z) => _sections.GetOrAdd(z, h => MeshSlicer.Slice(Stock, h));

    /// <summary>Clearance plane for retracts: everything plus <paramref name="clearance"/>.</summary>
    public double ClearancePlane(double clearance) => TopZ + clearance;

    public void Dispose()
    {
        PartGpu.Dispose();
        ObstaclesGpu?.Dispose();
    }

    private static Mesh StockMesh(ToolpathRequest request)
    {
        var s = request.Stock;
        return s.Type switch
        {
            StockType.Box => SolidMeshes.Box(
                new Vector3((float)s.OriginX, (float)s.OriginY, (float)s.OriginZ),
                new Vector3((float)(s.OriginX + s.SizeX), (float)(s.OriginY + s.SizeY), (float)(s.OriginZ + s.SizeZ)),
                "stock"),
            StockType.Cylinder => SolidMeshes.XCylinder(s.OriginX, s.OriginX + s.Length, s.OriginY, s.OriginZ, s.Diameter / 2, PrimitiveTolerance, "stock"),
            _ => request.StockMesh ?? throw new ArgumentException("Mesh stock requires ToolpathRequest.StockMesh.", nameof(request)),
        };
    }
}
