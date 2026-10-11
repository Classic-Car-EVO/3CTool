using System.Runtime.CompilerServices;

using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

internal struct DropCutterArgs
{
    public MeshView Mesh;
    public CutterProfile Cutter;
    public ArrayView<Float3> Origins;
    public ArrayView<Float3> Axes;
    public ArrayView<float> Result;
}

/// <summary>
/// Drop-cutter along an arbitrary axis: for an origin O and unit axis W (tool "up"), the largest t such that the
/// cutter tip at O + t·W touches the mesh without gouging it. Evaluated per triangle as the maximum over
/// facet, edge and vertex contacts; vertices are covered by the edge endpoints.
/// </summary>
internal static class DropCutterKernel
{
    public static void Run(Index1D index, DropCutterArgs args) =>
        args.Result[index] = Drop(args.Mesh, args.Cutter, args.Origins[index], args.Axes[index]);

    public static float Drop(MeshView mesh, CutterProfile cutter, Float3 origin, Float3 w)
    {
        Float3.Basis(w, out var u, out var v);
        var radius = cutter.Radius;
        var inflate = new Float3(radius, radius, radius);
        var absW = Float3.Abs(w);
        var best = DeviceMath.NoContact;
        var count = mesh.NodeCount;
        var i = 0;
        while (i < count)
        {
            var node = mesh.Nodes[i];
            var center = (node.Min + node.Max) * 0.5f;
            var extent = (node.Max - node.Min) * 0.5f;
            var upper = Float3.Dot(center - origin, w) + Float3.Dot(extent, absW);
            var next = node.Skip;
            if (upper > best && DeviceMath.LineHitsBox(origin, w, node.Min - inflate, node.Max + inflate))
            {
                if (node.Count > 0)
                {
                    for (var k = 0; k < node.Count; k++)
                    {
                        var triangle = mesh.Triangles[node.Start + k];
                        var a = Local(triangle.A - origin, u, v, w);
                        var b = Local(triangle.B - origin, u, v, w);
                        var c = Local(triangle.C - origin, u, v, w);
                        best = MathF.Max(best, Triangle(a, b, c, cutter, best));
                    }
                }
                else
                {
                    next = i + 1;
                }
            }

            i = next;
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Float3 Local(Float3 p, Float3 u, Float3 v, Float3 w) =>
        new(Float3.Dot(p, u), Float3.Dot(p, v), Float3.Dot(p, w));

    /// <summary>Contact height for one triangle in the local frame (tool axis = local Z through the origin).</summary>
    public static float Triangle(Float3 a, Float3 b, Float3 c, CutterProfile cutter, float best)
    {
        var result = DeviceMath.NoContact;
        var top = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
        if (top <= best)
        {
            return result;
        }

        result = MathF.Max(result, Facet(a, b, c, cutter));
        result = MathF.Max(result, Edge(a, b, cutter, MathF.Max(best, result)));
        result = MathF.Max(result, Edge(b, c, cutter, MathF.Max(best, result)));
        result = MathF.Max(result, Edge(c, a, cutter, MathF.Max(best, result)));
        return result;
    }

    private static float Facet(Float3 a, Float3 b, Float3 c, CutterProfile cutter)
    {
        var n = Float3.Cross(b - a, c - a);
        var length = Float3.Length(n);
        if (length < 1e-12f)
        {
            return DeviceMath.NoContact;
        }

        n *= 1f / length;
        if (n.Z < 0)
        {
            n = -n;
        }

        if (n.Z < 1e-6f)
        {
            return DeviceMath.NoContact;
        }

        var nxy = MathF.Sqrt((n.X * n.X) + (n.Y * n.Y));
        float contactRadius = 0;
        float cu = 0;
        float cv = 0;
        if (nxy > 1e-9f)
        {
            contactRadius = cutter.ContactRadius(nxy / n.Z);
            cu = -contactRadius * n.X / nxy;
            cv = -contactRadius * n.Y / nxy;
        }

        if (!DeviceMath.PointInTriangle2D(cu, cv, a.X, a.Y, b.X, b.Y, c.X, c.Y))
        {
            return DeviceMath.NoContact;
        }

        var planeW = a.Z - (((n.X * (cu - a.X)) + (n.Y * (cv - a.Y))) / n.Z);
        return planeW - cutter.HeightAt(contactRadius);
    }

    private static float Edge(Float3 p0, Float3 p1, CutterProfile cutter, float best)
    {
        if (MathF.Max(p0.Z, p1.Z) <= best)
        {
            return DeviceMath.NoContact;
        }

        var e = p1 - p0;
        if (!DeviceMath.DiscRange(p0.X, p0.Y, e.X, e.Y, cutter.Radius, out var s0, out var s1))
        {
            return DeviceMath.NoContact;
        }

        var function = new EdgeDrop { P0 = p0, E = e, Cutter = cutter };
        return DeviceMath.GoldenMax(function, s0, s1, out _);
    }

    /// <summary>Tip height touching the edge point at parameter s; concave in s because the profile is convex.</summary>
    private struct EdgeDrop : IScalarFunction
    {
        public Float3 P0;
        public Float3 E;
        public CutterProfile Cutter;

        public readonly float Evaluate(float s)
        {
            var x = P0.X + (s * E.X);
            var y = P0.Y + (s * E.Y);
            var d = MathF.Min(MathF.Sqrt((x * x) + (y * y)), Cutter.Radius);
            return P0.Z + (s * E.Z) - Cutter.HeightAt(d);
        }
    }
}
