using System.Numerics;

using ThreeCTool.Geometry;

namespace ThreeCTool.Rendering;

/// <summary>Mesh helpers shared by the GL and software renderers.</summary>
public static class SceneGeometry
{
    private const int ToolSegments = 24;

    /// <summary>Unindexed, flat-shaded vertex data: x, y, z, nx, ny, nz for each corner of each triangle.</summary>
    public static float[] FlatVertices(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var data = new float[mesh.TriangleCount * 18];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            var normal = mesh.FaceNormal(t);
            var offset = t * 18;
            foreach (var p in (ReadOnlySpan<Vector3>)[a, b, c])
            {
                data[offset++] = p.X;
                data[offset++] = p.Y;
                data[offset++] = p.Z;
                data[offset++] = normal.X;
                data[offset++] = normal.Y;
                data[offset++] = normal.Z;
            }
        }

        return data;
    }

    /// <summary>Closed cylinder for a tool pose: from the tip along the axis, with the tool diameter.</summary>
    public static Mesh ToolMesh(ToolMarker pose)
    {
        var axis = pose.Axis.LengthSquared() > 1e-12f ? Vector3.Normalize(pose.Axis) : Vector3.UnitZ;
        var helper = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        var v = Vector3.Cross(axis, u);
        var radius = Math.Max(pose.Diameter / 2, 0.05f);
        var top = pose.Tip + (axis * Math.Max(pose.Length, 0.1f));

        // Ring vertices: bottom 0..n-1, top n..2n-1, then the two cap centres.
        var vertices = new Vector3[(2 * ToolSegments) + 2];
        for (var i = 0; i < ToolSegments; i++)
        {
            var (sin, cos) = MathF.SinCos(2 * MathF.PI * i / ToolSegments);
            var offset = radius * ((cos * u) + (sin * v));
            vertices[i] = pose.Tip + offset;
            vertices[ToolSegments + i] = top + offset;
        }

        var bottomCentre = 2 * ToolSegments;
        var topCentre = bottomCentre + 1;
        vertices[bottomCentre] = pose.Tip;
        vertices[topCentre] = top;
        var indices = new List<int>(ToolSegments * 12);
        for (var i = 0; i < ToolSegments; i++)
        {
            var next = (i + 1) % ToolSegments;
            indices.AddRange([i, next, ToolSegments + next, i, ToolSegments + next, ToolSegments + i]);
            indices.AddRange([bottomCentre, next, i]);
            indices.AddRange([topCentre, ToolSegments + i, ToolSegments + next]);
        }

        return new Mesh(vertices, [.. indices], "tool");
    }
}
