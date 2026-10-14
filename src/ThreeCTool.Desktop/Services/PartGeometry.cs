using System.Numerics;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Desktop.Services;

/// <summary>Loaded model file: bodies after repair, before the project transform.</summary>
public sealed record LoadedPart(PartModel Model, IReadOnlyList<MeshBody> Bodies, BoundingBox RawBounds)
{
    /// <summary>Import-unit scale and transform history.</summary>
    public Matrix4x4 Matrix => MeshTransforms.Compose(Model.ImportUnit, Model.Transforms);

    /// <summary>Milled bodies merged and transformed into the project (part) frame.</summary>
    public Mesh MillMesh()
    {
        var bodies = Model.Role == BodyRole.Mill ? Bodies.Where(b => b.Role == BodyRole.Mill).Select(b => b.Mesh).ToList() : [];
        return bodies.Count == 0 ? new Mesh([], [], Model.Name) : Mesh.Merge(bodies, Model.Name).Transform(Matrix);
    }

    public Mesh AllBodies() => Mesh.Merge(Bodies.Select(b => b.Mesh), Model.Name).Transform(Matrix);
}

/// <summary>Meshes derived from the project (models, stock, keep-outs).</summary>
public static class PartGeometry
{
    private const double PrimitiveTolerance = 0.01;

    /// <summary>Resolves a project-relative path; paths escaping the project folder are rejected.</summary>
    public static string Resolve(string projectFolder, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectFolder));
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (Path.IsPathRooted(relativePath) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{relativePath}' is outside the project folder.");
        }

        return full;
    }

    /// <summary>Loads and (when the model was repaired on import) repairs every body of a part model.</summary>
    public static LoadedPart Load(string projectFolder, PartModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var imported = MeshImporter.Load(Resolve(projectFolder, model.FilePath));
        if (!string.Equals(imported.Sha256, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Model file '{model.FilePath}' changed since it was imported (SHA-256 mismatch).");
        }

        var bodies = model.RepairReport is null
            ? imported.Bodies
            : [.. imported.Bodies.Select(b => b with { Mesh = MeshRepair.Repair(b.Mesh).Mesh })];
        var bounds = bodies.Aggregate(BoundingBox.Empty, (box, body) => box.Union(body.Mesh.Bounds));
        return new LoadedPart(model, bodies, bounds);
    }

    /// <summary>Repairs every body and sums the reports.</summary>
    public static (IReadOnlyList<MeshBody> Bodies, MeshRepairReport Report) Repair(IReadOnlyList<MeshBody> bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        var total = new MeshRepairReport { IsWatertight = true };
        var repaired = new List<MeshBody>();
        foreach (var body in bodies)
        {
            var (mesh, report) = MeshRepair.Repair(body.Mesh);
            repaired.Add(body with { Mesh = mesh });
            total.RemovedDuplicateTriangles += report.RemovedDuplicateTriangles;
            total.RemovedDegenerateTriangles += report.RemovedDegenerateTriangles;
            total.FlippedTriangles += report.FlippedTriangles;
            total.NonManifoldEdges += report.NonManifoldEdges;
            total.HolesFilled += report.HolesFilled;
            total.IsWatertight &= report.IsWatertight;
        }

        return (repaired, total);
    }

    /// <summary>Stock solid in the part frame (null for mesh stock without a loaded mesh).</summary>
    public static Mesh? StockMesh(Stock stock, Mesh? stockMesh)
    {
        ArgumentNullException.ThrowIfNull(stock);
        return stock.Type switch
        {
            StockType.Box => SolidMeshes.Box(
                new Vector3((float)stock.OriginX, (float)stock.OriginY, (float)stock.OriginZ),
                new Vector3((float)(stock.OriginX + stock.SizeX), (float)(stock.OriginY + stock.SizeY), (float)(stock.OriginZ + stock.SizeZ)),
                "stock"),
            StockType.Cylinder => SolidMeshes.XCylinder(stock.OriginX, stock.OriginX + stock.Length, stock.OriginY, stock.OriginZ, stock.Diameter / 2, PrimitiveTolerance, "stock"),
            _ => stockMesh,
        };
    }

    /// <summary>Loads the mesh of a <see cref="StockType.Mesh"/> stock, checking its hash.</summary>
    public static Mesh? LoadStockMesh(string projectFolder, Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (stock.Type != StockType.Mesh || string.IsNullOrEmpty(stock.MeshPath))
        {
            return null;
        }

        var imported = MeshImporter.Load(Resolve(projectFolder, stock.MeshPath));
        if (stock.MeshSha256 is { Length: > 0 } sha && !string.Equals(sha, imported.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Stock mesh '{stock.MeshPath}' changed since it was imported (SHA-256 mismatch).");
        }

        return Mesh.Merge(imported.Bodies.Select(b => b.Mesh), "stock");
    }

    public static Mesh KeepOutMesh(KeepOutZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return SolidMeshes.FromSolid(zone.Solid, PrimitiveTolerance);
    }

    /// <summary>Box stock around <paramref name="part"/> with <paramref name="margin"/> on every side (top flush + margin).</summary>
    public static void FitBox(Stock stock, BoundingBox part, double margin)
    {
        ArgumentNullException.ThrowIfNull(stock);
        stock.Type = StockType.Box;
        stock.OriginX = part.Min.X - margin;
        stock.OriginY = part.Min.Y - margin;
        stock.OriginZ = part.Min.Z - margin;
        stock.SizeX = part.Size.X + (2 * margin);
        stock.SizeY = part.Size.Y + (2 * margin);
        stock.SizeZ = part.Size.Z + (2 * margin);
    }

    /// <summary>Cylinder stock along X enclosing <paramref name="part"/> plus <paramref name="margin"/>.</summary>
    public static void FitCylinder(Stock stock, Mesh part, double margin)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(part);
        var (y, z, radius) = Placement.EnclosingCircleYz(part);
        var bounds = part.Bounds;
        stock.Type = StockType.Cylinder;
        stock.OriginX = bounds.Min.X - margin;
        stock.OriginY = y;
        stock.OriginZ = z;
        stock.Length = bounds.Size.X + (2 * margin);
        stock.Diameter = 2 * (radius + margin);
    }

    /// <summary>Axis-angle rotation step equivalent to a pure rotation matrix (orientation suggestions).</summary>
    public static TransformStep? RotationStep(Matrix4x4 rotation)
    {
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
        var angle = 2 * Math.Acos(Math.Clamp(q.W, -1, 1));
        var s = Math.Sqrt(Math.Max(0, 1 - (q.W * q.W)));
        if (angle < 1e-6 || s < 1e-9)
        {
            return null;
        }

        return MeshTransforms.Rotate(new Vector3((float)(q.X / s), (float)(q.Y / s), (float)(q.Z / s)), angle * 180 / Math.PI);
    }

    public static string Describe(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeter => "mm",
        LengthUnit.Centimeter => "cm",
        LengthUnit.Meter => "m",
        _ => "in",
    };
}
