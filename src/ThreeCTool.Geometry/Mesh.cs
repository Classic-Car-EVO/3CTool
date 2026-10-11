using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace ThreeCTool.Geometry;

/// <summary>
/// Indexed triangle mesh in millimetres. Triangles are counter-clockwise seen from outside.
/// Vertices and indices are flat arrays so they can be uploaded to the GPU without copying.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Flat arrays are passed directly to GPU kernels and GL buffers.")]
public sealed class Mesh
{
    private BoundingBox? _bounds;

    public Mesh(Vector3[] vertices, int[] indices, string name = "")
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("Index count must be a multiple of 3.", nameof(indices));
        }

        foreach (var index in indices)
        {
            if ((uint)index >= (uint)vertices.Length)
            {
                throw new ArgumentException($"Index {index} is out of range for {vertices.Length} vertices.", nameof(indices));
            }
        }

        Vertices = vertices;
        Indices = indices;
        Name = name;
    }

    public string Name { get; set; }

    public Vector3[] Vertices { get; }

    public int[] Indices { get; }

    public int VertexCount => Vertices.Length;

    public int TriangleCount => Indices.Length / 3;

    public BoundingBox Bounds => _bounds ??= BoundingBox.FromPoints(Vertices);

    public (Vector3 A, Vector3 B, Vector3 C) GetTriangle(int triangle) =>
        (Vertices[Indices[triangle * 3]], Vertices[Indices[(triangle * 3) + 1]], Vertices[Indices[(triangle * 3) + 2]]);

    /// <summary>Unit face normal, or zero for a degenerate triangle.</summary>
    public Vector3 FaceNormal(int triangle)
    {
        var (a, b, c) = GetTriangle(triangle);
        var n = Vector3.Cross(b - a, c - a);
        var length = n.Length();
        return length > 0 ? n / length : Vector3.Zero;
    }

    public double TriangleArea(int triangle)
    {
        var (a, b, c) = GetTriangle(triangle);
        return 0.5 * Vector3.Cross(b - a, c - a).Length();
    }

    /// <summary>Area-weighted vertex normals.</summary>
    public Vector3[] ComputeVertexNormals()
    {
        var normals = new Vector3[Vertices.Length];
        for (var t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = GetTriangle(t);
            var n = Vector3.Cross(b - a, c - a);
            normals[Indices[t * 3]] += n;
            normals[Indices[(t * 3) + 1]] += n;
            normals[Indices[(t * 3) + 2]] += n;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            var length = normals[i].Length();
            normals[i] = length > 0 ? normals[i] / length : Vector3.UnitZ;
        }

        return normals;
    }

    public double SurfaceArea()
    {
        var area = 0.0;
        for (var t = 0; t < TriangleCount; t++)
        {
            area += TriangleArea(t);
        }

        return area;
    }

    /// <summary>Signed volume (divergence theorem); positive for a closed, outward-facing mesh.</summary>
    public double SignedVolume()
    {
        var volume = 0.0;
        for (var t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = GetTriangle(t);
            volume += Vector3D.Dot(a, Vector3D.Cross(b, c));
        }

        return volume / 6.0;
    }

    /// <summary>Returns a transformed copy. Mirroring transforms reverse the winding to keep normals outward.</summary>
    public Mesh Transform(Matrix4x4 matrix)
    {
        var vertices = new Vector3[Vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = Vector3.Transform(Vertices[i], matrix);
        }

        var indices = (int[])Indices.Clone();
        if (matrix.GetDeterminant() < 0)
        {
            for (var t = 0; t < indices.Length; t += 3)
            {
                (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
            }
        }

        return new Mesh(vertices, indices, Name);
    }

    public static Mesh Merge(IEnumerable<Mesh> meshes, string name = "")
    {
        ArgumentNullException.ThrowIfNull(meshes);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var mesh in meshes)
        {
            var offset = vertices.Count;
            vertices.AddRange(mesh.Vertices);
            indices.AddRange(mesh.Indices.Select(i => i + offset));
        }

        return new Mesh([.. vertices], [.. indices], name);
    }

    /// <summary>Double-precision helpers for accumulations over float vertices.</summary>
    private static class Vector3D
    {
        public static (double X, double Y, double Z) Cross(Vector3 a, Vector3 b) =>
            (((double)a.Y * b.Z) - ((double)a.Z * b.Y), ((double)a.Z * b.X) - ((double)a.X * b.Z), ((double)a.X * b.Y) - ((double)a.Y * b.X));

        public static double Dot(Vector3 a, (double X, double Y, double Z) b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
    }
}
