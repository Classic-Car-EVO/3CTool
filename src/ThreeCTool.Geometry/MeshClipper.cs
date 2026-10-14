using System.Numerics;

using Clipper2Lib;

namespace ThreeCTool.Geometry;

/// <summary>
/// Cuts closed meshes with axis-aligned planes and closes the cut with a flat cap, so the result is a closed solid
/// again (up to the 0.1 µm rounding of <see cref="Polygons.Precision"/> on the cap).
/// </summary>
public static class MeshClipper
{
    /// <summary>
    /// The part of <paramref name="mesh"/> on one side of the plane <paramref name="axis"/> = <paramref name="position"/>
    /// (axis 0 = X, 1 = Y, 2 = Z): below it when <paramref name="keepBelow"/>, above it otherwise.
    /// </summary>
    public static Mesh Clip(Mesh mesh, int axis, double position, bool keepBelow)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegative(axis);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(axis, 2);
        var toZ = axis switch
        {
            0 => Rotate,
            1 => Rotate * Rotate,
            _ => Matrix4x4.Identity,
        };
        if (!keepBelow)
        {
            toZ *= Matrix4x4.CreateScale(1, 1, -1);
            position = -position;
        }

        var local = toZ.IsIdentity ? mesh : mesh.Transform(toZ);
        var clipped = ClipBelow(local, position);
        if (toZ.IsIdentity)
        {
            return clipped;
        }

        Matrix4x4.Invert(toZ, out var back);
        var result = clipped.Transform(back);
        result.Name = mesh.Name;
        return result;
    }

    /// <summary>The part of <paramref name="mesh"/> inside the given slab bounds; null bounds are not cut.</summary>
    public static Mesh ClipToBox(Mesh mesh, double? minX, double? maxX, double? minY, double? maxY, double? minZ, double? maxZ)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var result = mesh;
        (int Axis, double? Value, bool Below)[] cuts =
        [
            (0, minX, false), (0, maxX, true), (1, minY, false), (1, maxY, true), (2, minZ, false), (2, maxZ, true),
        ];
        foreach (var (axis, value, below) in cuts)
        {
            if (value is { } v && result.TriangleCount > 0)
            {
                result = Clip(result, axis, v, below);
            }
        }

        return result;
    }

    /// <summary>
    /// The part of <paramref name="mesh"/> below Z = <paramref name="z"/>, capped at the plane. Vertices on the plane count
    /// as above, matching <see cref="MeshSlicer.Slice(Mesh, double)"/>, which supplies the cap outline.
    /// </summary>
    public static Mesh ClipBelow(Mesh mesh, double z)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TriangleCount == 0 || mesh.Bounds.Max.Z < z)
        {
            return mesh;
        }

        var source = mesh.Vertices;
        var vertices = new List<Vector3>();
        var map = new int[source.Length];
        Array.Fill(map, -1);
        var crossings = new Dictionary<(int, int), int>();
        var indices = new List<int>();

        int Keep(int i)
        {
            if (map[i] < 0)
            {
                map[i] = vertices.Count;
                vertices.Add(source[i]);
            }

            return map[i];
        }

        int Cross(int i, int j)
        {
            var key = i < j ? (i, j) : (j, i);
            if (!crossings.TryGetValue(key, out var index))
            {
                var p = Crossing(source[i], source[j], z);
                index = vertices.Count;
                vertices.Add(new Vector3((float)p.x, (float)p.y, (float)z));
                crossings[key] = index;
            }

            return index;
        }

        void Add(int a, int b, int c)
        {
            if (a != b && b != c && c != a && Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]) != Vector3.Zero)
            {
                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }
        }

        var all = mesh.Indices;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var i0 = all[3 * t];
            var i1 = all[(3 * t) + 1];
            var i2 = all[(3 * t) + 2];
            var a0 = source[i0].Z >= z;
            var a1 = source[i1].Z >= z;
            var a2 = source[i2].Z >= z;
            if (a0 && a1 && a2)
            {
                continue;
            }

            if (!a0 && !a1 && !a2)
            {
                Add(Keep(i0), Keep(i1), Keep(i2));
                continue;
            }

            // Isolated corner k with the others c1, c2 in winding order.
            var (k, c1, c2, above) = a1 == a2 ? (i0, i1, i2, a0) : a0 == a2 ? (i1, i2, i0, a1) : (i2, i0, i1, a2);
            var p = Cross(k, c1);
            var q = Cross(c2, k);
            if (above)
            {
                var b1 = Keep(c1);
                var b2 = Keep(c2);
                Add(p, b1, b2);
                Add(p, b2, q);
            }
            else
            {
                Add(Keep(k), p, q);
            }
        }

        AddCap(mesh, z, vertices, indices);
        return new Mesh([.. vertices], [.. indices], mesh.Name);
    }

    private static readonly Matrix4x4 Rotate = new(
        0, 0, 1, 0,
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    private static void AddCap(Mesh mesh, double z, List<Vector3> vertices, List<int> indices)
    {
        var region = MeshSlicer.Slice(mesh, z);
        if (region.Count == 0
            || Clipper.Triangulate(region, Polygons.Precision, out var triangles, true) != TriangulateResult.success)
        {
            return;
        }

        foreach (var triangle in triangles)
        {
            if (triangle.Count != 3)
            {
                continue;
            }

            var area = ((triangle[1].x - triangle[0].x) * (triangle[2].y - triangle[0].y))
                - ((triangle[1].y - triangle[0].y) * (triangle[2].x - triangle[0].x));
            if (area == 0)
            {
                continue;
            }

            // The cap is the top face of the kept part: counter-clockwise seen from +Z.
            var order = area > 0 ? new[] { 0, 1, 2 } : [0, 2, 1];
            foreach (var corner in order)
            {
                indices.Add(vertices.Count);
                vertices.Add(new Vector3((float)triangle[corner].x, (float)triangle[corner].y, (float)z));
            }
        }
    }

    /// <summary>Plane crossing of an edge from a canonical end order, identical to <see cref="MeshSlicer"/>.</summary>
    private static PointD Crossing(Vector3 a, Vector3 b, double z)
    {
        if (Compare(a, b) > 0)
        {
            (a, b) = (b, a);
        }

        var t = (z - a.Z) / ((double)b.Z - a.Z);
        return new PointD(a.X + (t * ((double)b.X - a.X)), a.Y + (t * ((double)b.Y - a.Y)));
    }

    private static int Compare(Vector3 a, Vector3 b)
    {
        var c = a.X.CompareTo(b.X);
        if (c != 0)
        {
            return c;
        }

        c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.Z.CompareTo(b.Z);
    }
}
