using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

internal struct RaycastArgs
{
    public MeshView Mesh;
    public ArrayView<Float3> Origins;
    public ArrayView<Float3> Directions;
    public float MaxDistance;

    /// <summary>Per ray: distance, barycentric u, barycentric v (distance &lt; 0 = miss).</summary>
    public ArrayView<Float3> Hits;
    public ArrayView<int> Triangles;
}

internal struct ClosestPointArgs
{
    public MeshView Mesh;
    public ArrayView<Float3> Points;
    public float MaxDistance;
    public ArrayView<Float3> Closest;
    public ArrayView<float> Distances;
    public ArrayView<int> Triangles;

    /// <summary>When non-zero, distances are signed (negative inside) by majority ray parity.</summary>
    public int Signed;
}

internal static class MeshQueryKernels
{
    // Three fixed, non-axis-aligned directions for parity voting (edge/vertex hits on all three are unlikely).
    private static Float3 ParityDirection(int k) => k switch
    {
        0 => Float3.Normalize(new Float3(0.5773f, 0.5791f, 0.5755f)),
        1 => Float3.Normalize(new Float3(-0.6211f, 0.3122f, 0.7187f)),
        _ => Float3.Normalize(new Float3(0.2459f, -0.8713f, -0.4247f)),
    };

    public static void Raycast(Index1D index, RaycastArgs args)
    {
        var origin = args.Origins[index];
        var direction = args.Directions[index];
        var best = args.MaxDistance;
        var bestTriangle = -1;
        float bestU = 0;
        float bestV = 0;
        var mesh = args.Mesh;
        var count = mesh.NodeCount;
        var i = 0;
        while (i < count)
        {
            var node = mesh.Nodes[i];
            var next = node.Skip;
            var enter = 0f;
            var exit = best;
            if (DeviceMath.Slab(origin.X, direction.X, node.MinX, node.MaxX, ref enter, ref exit)
                && DeviceMath.Slab(origin.Y, direction.Y, node.MinY, node.MaxY, ref enter, ref exit)
                && DeviceMath.Slab(origin.Z, direction.Z, node.MinZ, node.MaxZ, ref enter, ref exit))
            {
                if (node.Count > 0)
                {
                    for (var k = 0; k < node.Count; k++)
                    {
                        var triangle = mesh.Triangles[node.Start + k];
                        if (Intersect(origin, direction, triangle, out var t, out var u, out var v) && t >= 0 && t < best)
                        {
                            best = t;
                            bestU = u;
                            bestV = v;
                            bestTriangle = mesh.TriangleIds[node.Start + k];
                        }
                    }
                }
                else
                {
                    next = i + 1;
                }
            }

            i = next;
        }

        args.Hits[index] = new Float3(bestTriangle >= 0 ? best : -1, bestU, bestV);
        args.Triangles[index] = bestTriangle;
    }

    public static void ClosestPoint(Index1D index, ClosestPointArgs args)
    {
        var p = args.Points[index];
        var bestSquared = args.MaxDistance * args.MaxDistance;
        var bestPoint = p;
        var bestTriangle = -1;
        var mesh = args.Mesh;
        var count = mesh.NodeCount;
        var i = 0;
        while (i < count)
        {
            var node = mesh.Nodes[i];
            var next = node.Skip;
            if (DeviceMath.BoxDistanceSquared(p, node.Min, node.Max) <= bestSquared)
            {
                if (node.Count > 0)
                {
                    for (var k = 0; k < node.Count; k++)
                    {
                        var triangle = mesh.Triangles[node.Start + k];
                        var q = ClosestOnTriangle(p, triangle.A, triangle.B, triangle.C);
                        var delta = q - p;
                        var squared = Float3.Dot(delta, delta);
                        if (squared < bestSquared)
                        {
                            bestSquared = squared;
                            bestPoint = q;
                            bestTriangle = mesh.TriangleIds[node.Start + k];
                        }
                    }
                }
                else
                {
                    next = i + 1;
                }
            }

            i = next;
        }

        var distance = bestTriangle >= 0 ? MathF.Sqrt(bestSquared) : args.MaxDistance;
        if (args.Signed != 0)
        {
            var votes = 0;
            for (var k = 0; k < 3; k++)
            {
                votes += CountCrossings(mesh, p, ParityDirection(k)) & 1;
            }

            if (votes >= 2)
            {
                distance = -distance;
            }
        }

        args.Closest[index] = bestPoint;
        args.Distances[index] = distance;
        args.Triangles[index] = bestTriangle;
    }

    private static int CountCrossings(MeshView mesh, Float3 origin, Float3 direction)
    {
        var crossings = 0;
        var count = mesh.NodeCount;
        var i = 0;
        while (i < count)
        {
            var node = mesh.Nodes[i];
            var next = node.Skip;
            var enter = 0f;
            var exit = 1e30f;
            if (DeviceMath.Slab(origin.X, direction.X, node.MinX, node.MaxX, ref enter, ref exit)
                && DeviceMath.Slab(origin.Y, direction.Y, node.MinY, node.MaxY, ref enter, ref exit)
                && DeviceMath.Slab(origin.Z, direction.Z, node.MinZ, node.MaxZ, ref enter, ref exit))
            {
                if (node.Count > 0)
                {
                    for (var k = 0; k < node.Count; k++)
                    {
                        if (Intersect(origin, direction, mesh.Triangles[node.Start + k], out var t, out _, out _) && t > 0)
                        {
                            crossings++;
                        }
                    }
                }
                else
                {
                    next = i + 1;
                }
            }

            i = next;
        }

        return crossings;
    }

    /// <summary>Möller–Trumbore, two-sided.</summary>
    public static bool Intersect(Float3 origin, Float3 direction, GpuTriangle triangle, out float t, out float u, out float v)
    {
        t = 0;
        u = 0;
        v = 0;
        var e1 = triangle.B - triangle.A;
        var e2 = triangle.C - triangle.A;
        var pv = Float3.Cross(direction, e2);
        var det = Float3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-12f)
        {
            return false;
        }

        var inv = 1f / det;
        var tv = origin - triangle.A;
        u = Float3.Dot(tv, pv) * inv;
        if (u < 0 || u > 1)
        {
            return false;
        }

        var qv = Float3.Cross(tv, e1);
        v = Float3.Dot(direction, qv) * inv;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        t = Float3.Dot(e2, qv) * inv;
        return true;
    }

    /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    public static Float3 ClosestOnTriangle(Float3 p, Float3 a, Float3 b, Float3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = Float3.Dot(ab, ap);
        var d2 = Float3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
        {
            return a;
        }

        var bp = p - b;
        var d3 = Float3.Dot(ab, bp);
        var d4 = Float3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
        {
            return b;
        }

        var vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            return a + (ab * (d1 / (d1 - d3)));
        }

        var cp = p - c;
        var d5 = Float3.Dot(ab, cp);
        var d6 = Float3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
        {
            return c;
        }

        var vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            return a + (ac * (d2 / (d2 - d6)));
        }

        var va = (d3 * d6) - (d5 * d4);
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        {
            return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
        }

        var denominator = 1f / (va + vb + vc);
        return a + (ab * (vb * denominator)) + (ac * (vc * denominator));
    }
}
