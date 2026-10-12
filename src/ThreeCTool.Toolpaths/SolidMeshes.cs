using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Core;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths;

/// <summary>Closed, outward-oriented meshes of stock and keep-out primitives.</summary>
public static class SolidMeshes
{
    public static Mesh Box(Vector3 min, Vector3 max, string name = "box")
    {
        Vector3[] v =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        int[] indices =
        [
            0, 2, 1, 0, 3, 2, // bottom
            4, 5, 6, 4, 6, 7, // top
            0, 1, 5, 0, 5, 4, // front (-Y)
            2, 3, 7, 2, 7, 6, // back (+Y)
            1, 2, 6, 1, 6, 5, // right (+X)
            3, 0, 4, 3, 4, 7, // left (-X)
        ];
        return new Mesh(v, indices, name);
    }

    /// <summary>Cylinder along X from <paramref name="x0"/> to <paramref name="x1"/> around (y, z).</summary>
    public static Mesh XCylinder(double x0, double x1, double y, double z, double radius, double tolerance, string name = "cylinder")
    {
        var mesh = ZCylinder(0, 0, 0, x1 - x0, radius, tolerance, name);

        // Z-cylinder → X-cylinder: (x, y, z) → (z, x, y) keeps the orientation (cyclic permutation).
        var vertices = mesh.Vertices.Select(p => new Vector3((float)x0 + p.Z, (float)y + p.X, (float)z + p.Y)).ToArray();
        return new Mesh(vertices, mesh.Indices, name);
    }

    public static Mesh ZCylinder(double x, double y, double bottom, double height, double radius, double tolerance, string name = "cylinder")
    {
        var circle = Polygons.Circle(new PointD(x, y), radius, tolerance);
        var n = circle.Count;
        var vertices = new Vector3[(2 * n) + 2];
        for (var i = 0; i < n; i++)
        {
            vertices[i] = new Vector3((float)circle[i].x, (float)circle[i].y, (float)bottom);
            vertices[n + i] = new Vector3((float)circle[i].x, (float)circle[i].y, (float)(bottom + height));
        }

        vertices[2 * n] = new Vector3((float)x, (float)y, (float)bottom);
        vertices[(2 * n) + 1] = new Vector3((float)x, (float)y, (float)(bottom + height));
        var indices = new List<int>(12 * n);
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            indices.AddRange([i, j, n + j, i, n + j, n + i]);
            indices.AddRange([2 * n, j, i]);
            indices.AddRange([(2 * n) + 1, n + i, n + j]);
        }

        return new Mesh(vertices, [.. indices], name);
    }

    public static Mesh FromSolid(SimpleSolid solid, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(solid);
        return solid.Shape == SolidShape.Box
            ? Box(
                new Vector3((float)(solid.CenterX - (solid.SizeX / 2)), (float)(solid.CenterY - (solid.SizeY / 2)), (float)solid.BottomZ),
                new Vector3((float)(solid.CenterX + (solid.SizeX / 2)), (float)(solid.CenterY + (solid.SizeY / 2)), (float)(solid.BottomZ + solid.Height)),
                solid.Name)
            : ZCylinder(solid.CenterX, solid.CenterY, solid.BottomZ, solid.Height, solid.Radius, tolerance, solid.Name);
    }
}
