using System.Numerics;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Geometry;

public enum StockAnchor
{
    Top,
    Bottom,
    Center,
}

/// <summary>Auto-placement helpers. All results are translations to append to the part's transform history.</summary>
public static class Placement
{
    /// <summary>Centres the part in XY on box stock and aligns its top, bottom or centre with the stock.</summary>
    public static Vector3 OnBoxStock(BoundingBox part, Stock stock, StockAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (stock.Type != StockType.Box)
        {
            throw new ArgumentException("Stock must be a box.", nameof(stock));
        }

        var min = new Vector3((float)stock.OriginX, (float)stock.OriginY, (float)stock.OriginZ);
        var max = min + new Vector3((float)stock.SizeX, (float)stock.SizeY, (float)stock.SizeZ);
        var center = (min + max) * 0.5f;
        var dz = anchor switch
        {
            StockAnchor.Top => max.Z - part.Max.Z,
            StockAnchor.Bottom => min.Z - part.Min.Z,
            _ => center.Z - part.Center.Z,
        };
        return new Vector3(center.X - part.Center.X, center.Y - part.Center.Y, dz);
    }

    /// <summary>
    /// Puts the centre of the part's smallest enclosing circle in YZ on the cylinder stock axis
    /// (minimising the required stock radius) and centres the part along the stock length.
    /// </summary>
    public static Vector3 OnRotaryAxis(Mesh part, Stock stock)
    {
        ArgumentNullException.ThrowIfNull(part);
        RequireCylinder(stock);
        var (y, z, _) = EnclosingCircleYz(part);
        var stockCenterX = stock.OriginX + (stock.Length / 2);
        return new Vector3((float)(stockCenterX - part.Bounds.Center.X), (float)(stock.OriginY - y), (float)(stock.OriginZ - z));
    }

    /// <summary>True if every vertex lies inside the cylinder stock (axis parallel to X).</summary>
    public static bool FitsInCylinder(Mesh part, Stock stock)
    {
        ArgumentNullException.ThrowIfNull(part);
        RequireCylinder(stock);
        var bounds = part.Bounds;
        if (bounds.Min.X < stock.OriginX || bounds.Max.X > stock.OriginX + stock.Length)
        {
            return false;
        }

        return MaxRadius(part, stock.OriginY, stock.OriginZ) <= stock.Diameter / 2;
    }

    /// <summary>Largest distance of any vertex from the X-parallel axis through (centerY, centerZ).</summary>
    public static double MaxRadius(Mesh part, double centerY, double centerZ)
    {
        ArgumentNullException.ThrowIfNull(part);
        var maxSquared = 0.0;
        foreach (var v in part.Vertices)
        {
            var dy = v.Y - centerY;
            var dz = v.Z - centerZ;
            maxSquared = Math.Max(maxSquared, (dy * dy) + (dz * dz));
        }

        return Math.Sqrt(maxSquared);
    }

    /// <summary>Smallest circle enclosing the YZ projection of all vertices (Welzl, deterministic shuffle).</summary>
    public static (double Y, double Z, double Radius) EnclosingCircleYz(Mesh part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var points = part.Vertices.Select(v => (Y: (double)v.Y, Z: (double)v.Z)).ToArray();
        if (points.Length == 0)
        {
            return (0, 0, 0);
        }

        new Random(12345).Shuffle(points);
        var scale = Math.Max(1.0, part.Bounds.Diagonal);
        var epsilon = 1e-9 * scale;

        var c = (points[0].Y, points[0].Z, R: 0.0);
        for (var i = 1; i < points.Length; i++)
        {
            if (Inside(c, points[i], epsilon))
            {
                continue;
            }

            c = (points[i].Y, points[i].Z, 0.0);
            for (var j = 0; j < i; j++)
            {
                if (Inside(c, points[j], epsilon))
                {
                    continue;
                }

                c = Diameter(points[i], points[j]);
                for (var k = 0; k < j; k++)
                {
                    if (!Inside(c, points[k], epsilon))
                    {
                        c = Circumcircle(points[i], points[j], points[k]);
                    }
                }
            }
        }

        return c;
    }

    private static void RequireCylinder(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (stock.Type != StockType.Cylinder)
        {
            throw new ArgumentException("Stock must be a cylinder.", nameof(stock));
        }
    }

    private static bool Inside((double Y, double Z, double R) c, (double Y, double Z) p, double epsilon)
    {
        var dy = p.Y - c.Y;
        var dz = p.Z - c.Z;
        return Math.Sqrt((dy * dy) + (dz * dz)) <= c.R + epsilon;
    }

    private static (double Y, double Z, double R) Diameter((double Y, double Z) a, (double Y, double Z) b)
    {
        var y = (a.Y + b.Y) / 2;
        var z = (a.Z + b.Z) / 2;
        return (y, z, Math.Sqrt(((a.Y - y) * (a.Y - y)) + ((a.Z - z) * (a.Z - z))));
    }

    private static (double Y, double Z, double R) Circumcircle((double Y, double Z) a, (double Y, double Z) b, (double Y, double Z) c)
    {
        var bY = b.Y - a.Y;
        var bZ = b.Z - a.Z;
        var cY = c.Y - a.Y;
        var cZ = c.Z - a.Z;
        var d = 2 * ((bY * cZ) - (bZ * cY));
        if (Math.Abs(d) < 1e-18)
        {
            // Collinear: the circle on the two farthest points.
            var ab = Diameter(a, b);
            var ac = Diameter(a, c);
            var bc = Diameter(b, c);
            return ab.R >= ac.R && ab.R >= bc.R ? ab : ac.R >= bc.R ? ac : bc;
        }

        var b2 = (bY * bY) + (bZ * bZ);
        var c2 = (cY * cY) + (cZ * cZ);
        var y = ((cZ * b2) - (bZ * c2)) / d;
        var z = ((bY * c2) - (cY * b2)) / d;
        return (a.Y + y, a.Z + z, Math.Sqrt((y * y) + (z * z)));
    }
}

public enum MachiningMode
{
    /// <summary>Tool approaches from +Z only.</summary>
    ThreeAxis,

    /// <summary>A-axis indexing: tool approaches from directions perpendicular to the rotary (X) axis.</summary>
    ThreePlusOne,
}

/// <param name="Rotation">Rotation to apply to the part (part frame → machine frame).</param>
/// <param name="UnreachableArea">Surface area (mm²) the tool cannot reach in this orientation (undercuts).</param>
/// <param name="Height">Part extent along machine Z after rotation (3-axis) or the minimum stock radius about the A axis (3+1).</param>
public sealed record OrientationCandidate(string Description, Vector3 PartAxis, Matrix4x4 Rotation, double UnreachableArea, double TotalArea, double Height)
{
    public double ReachableFraction => TotalArea > 0 ? 1 - (UnreachableArea / TotalArea) : 1;
}

/// <summary>Ranks principal-axis orientations by unreachable (undercut) surface area using BVH visibility rays.</summary>
public static class OrientationAdvisor
{
    private static readonly (Vector3 Axis, string Name)[] PrincipalAxes =
    [
        (Vector3.UnitZ, "+Z"), (-Vector3.UnitZ, "-Z"),
        (Vector3.UnitX, "+X"), (-Vector3.UnitX, "-X"),
        (Vector3.UnitY, "+Y"), (-Vector3.UnitY, "-Y"),
    ];

    public static IReadOnlyList<OrientationCandidate> Suggest(Mesh mesh, MachiningMode mode, int indexPositions = 8)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfLessThan(indexPositions, 1);

        var bvh = new Bvh(mesh);
        var areas = Enumerable.Range(0, mesh.TriangleCount).Select(mesh.TriangleArea).ToArray();
        var totalArea = areas.Sum();
        var candidates = new List<OrientationCandidate>();

        if (mode == MachiningMode.ThreeAxis)
        {
            foreach (var (axis, name) in PrincipalAxes)
            {
                var rotation = RotationBetween(axis, Vector3.UnitZ);
                var unreachable = UnreachableArea(mesh, bvh, areas, [axis]);
                var height = mesh.Bounds.Transform(rotation).Size.Z;
                candidates.Add(new OrientationCandidate($"Part {name} up", axis, rotation, unreachable, totalArea, height));
            }
        }
        else
        {
            // Direction sign along the rotary axis does not change reachability, so three axes suffice.
            foreach (var (axis, name) in PrincipalAxes.Where((_, i) => i % 2 == 0))
            {
                var rotation = RotationBetween(axis, Vector3.UnitX);
                var (u, v) = Perpendiculars(axis);
                var directions = Enumerable.Range(0, indexPositions)
                    .Select(k => (2 * MathF.PI * k) / indexPositions)
                    .Select(angle => (u * MathF.Cos(angle)) + (v * MathF.Sin(angle)))
                    .ToArray();
                var unreachable = UnreachableArea(mesh, bvh, areas, directions);
                var height = Placement.EnclosingCircleYz(mesh.Transform(rotation)).Radius;
                candidates.Add(new OrientationCandidate($"Part {name[1..]} along A axis", axis, rotation, unreachable, totalArea, height));
            }
        }

        var tolerance = 1e-3 * totalArea;
        return [.. candidates.OrderBy(c => Math.Round(c.UnreachableArea / Math.Max(tolerance, 1e-12))).ThenBy(c => c.Height)];
    }

    /// <summary>Area of triangles not visible from any of <paramref name="directions"/> (part frame, pointing towards the tool).</summary>
    private static double UnreachableArea(Mesh mesh, Bvh bvh, double[] areas, Vector3[] directions)
    {
        var offset = 1e-4f * Math.Max(1f, mesh.Bounds.Diagonal);
        var far = mesh.Bounds.Diagonal * 2 + 1;
        var unreachable = new double[mesh.TriangleCount];
        Parallel.For(0, mesh.TriangleCount, t =>
        {
            var (a, b, c) = mesh.GetTriangle(t);
            var centroid = (a + b + c) / 3f;
            var normal = mesh.FaceNormal(t);
            foreach (var d in directions)
            {
                if (Vector3.Dot(normal, d) < -1e-3f)
                {
                    continue;
                }

                var origin = centroid + (normal * offset) + (d * offset);
                if (!bvh.Occluded(origin, d, far))
                {
                    return;
                }
            }

            unreachable[t] = areas[t];
        });
        return unreachable.Sum();
    }

    private static Matrix4x4 RotationBetween(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.9999f)
        {
            return Matrix4x4.Identity;
        }

        if (dot < -0.9999f)
        {
            var (u, _) = Perpendiculars(from);
            return Matrix4x4.CreateFromAxisAngle(u, MathF.PI);
        }

        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(from, to)), MathF.Acos(dot));
    }

    private static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis)
    {
        var helper = MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        return (u, Vector3.Cross(axis, u));
    }
}
