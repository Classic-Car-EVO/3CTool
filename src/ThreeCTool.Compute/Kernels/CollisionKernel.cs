using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

internal struct CollisionBodyView
{
    /// <summary>Per pose: tip, U, V, W (tool frame axes in the part frame).</summary>
    public ArrayView<Float3> Poses;

    /// <summary>Per primitive: centre and half extents in the tool frame (cylinder: radius, radius, half height).</summary>
    public ArrayView<Float3> Primitives;

    /// <summary>Per primitive: 0 = cylinder along the tool axis, 1 = box.</summary>
    public ArrayView<int> Shapes;
    public int PrimitiveCount;
    public float Clearance;

    /// <summary>Per pose: bit k set when primitive k collides.</summary>
    public ArrayView<int> Masks;

    public readonly void Load(int index, out int pose, out int primitive, out Float3 center, out Float3 half, out Float3 u, out Float3 v, out Float3 w, out bool box)
    {
        pose = index / PrimitiveCount;
        primitive = index - (pose * PrimitiveCount);
        var tip = Poses[4 * pose];
        u = Poses[(4 * pose) + 1];
        v = Poses[(4 * pose) + 2];
        w = Poses[(4 * pose) + 3];
        var local = Primitives[2 * primitive];
        center = tip + (u * local.X) + (v * local.Y) + (w * local.Z);
        half = Primitives[(2 * primitive) + 1] + new Float3(Clearance, Clearance, Clearance);
        box = Shapes[primitive] != 0;
    }
}

internal struct CollisionMeshArgs
{
    public CollisionBodyView Body;
    public MeshView Mesh;

    /// <summary>Non-zero for a closed mesh: a primitive entirely inside it also collides.</summary>
    public int Solid;
}

internal struct CollisionStockArgs
{
    public CollisionBodyView Body;

    /// <summary>The stock's Z dexel grid.</summary>
    public DexelGridView Grid;
}

internal static class CollisionKernel
{
    /// <summary>One thread per (pose, primitive): exact primitive–triangle tests over the BVH.</summary>
    public static void AgainstMesh(Index1D index, CollisionMeshArgs args)
    {
        args.Body.Load(index, out var pose, out var primitive, out var center, out var half, out var u, out var v, out var w, out var box);
        var extent = (Float3.Abs(u) * half.X) + (Float3.Abs(v) * half.Y) + (Float3.Abs(w) * half.Z);
        var min = center - extent;
        var max = center + extent;
        var mesh = args.Mesh;
        var count = mesh.NodeCount;
        var hit = false;
        var i = 0;
        while (i < count)
        {
            var node = mesh.Nodes[i];
            var next = node.Skip;
            if (DeviceMath.BoxesOverlap(min, max, node.Min, node.Max))
            {
                if (node.Count > 0)
                {
                    for (var k = 0; k < node.Count && !hit; k++)
                    {
                        var triangle = mesh.Triangles[node.Start + k];
                        var a = ToLocal(triangle.A - center, u, v, w);
                        var b = ToLocal(triangle.B - center, u, v, w);
                        var c = ToLocal(triangle.C - center, u, v, w);
                        hit = box ? TriangleBox(a, b, c, half) : TriangleCylinder(a, b, c, half.X, half.Z);
                    }
                }
                else
                {
                    next = i + 1;
                }
            }

            i = hit ? count : next;
        }

        if (!hit && args.Solid != 0)
        {
            var votes = 0;
            for (var k = 0; k < 3; k++)
            {
                votes += MeshQueryKernels.CountCrossings(mesh, center, MeshQueryKernels.ParityDirection(k)) & 1;
            }

            hit = votes >= 2;
        }

        if (hit)
        {
            Atomic.Or(ref args.Body.Masks[pose], 1 << primitive);
        }
    }

    /// <summary>One thread per (pose, primitive): primitive interval on each Z dexel ray in its footprint vs. material.</summary>
    public static void AgainstStock(Index1D index, CollisionStockArgs args)
    {
        args.Body.Load(index, out var pose, out var primitive, out var center, out var half, out var u, out var v, out var w, out var box);
        var extent = (Float3.Abs(u) * half.X) + (Float3.Abs(v) * half.Y) + (Float3.Abs(w) * half.Z);
        var grid = args.Grid;
        var res = grid.Resolution;
        var iu0 = (int)MathF.Max(MathF.Ceiling(((center.X - extent.X - grid.OriginU) / res) - 0.5f), 0);
        var iu1 = (int)MathF.Min(MathF.Floor(((center.X + extent.X - grid.OriginU) / res) - 0.5f), grid.CountU - 1);
        var iv0 = (int)MathF.Max(MathF.Ceiling(((center.Y - extent.Y - grid.OriginV) / res) - 0.5f), 0);
        var iv1 = (int)MathF.Min(MathF.Floor(((center.Y + extent.Y - grid.OriginV) / res) - 0.5f), grid.CountV - 1);

        // The ray direction (0, 0, 1) in the primitive frame.
        var d = new Float3(u.Z, v.Z, w.Z);
        var hit = false;
        for (var iv = iv0; iv <= iv1 && !hit; iv++)
        {
            for (var iu = iu0; iu <= iu1 && !hit; iu++)
            {
                var o = ToLocal(new Float3(grid.OriginU + ((iu + 0.5f) * res), grid.OriginV + ((iv + 0.5f) * res), 0) - center, u, v, w);
                var enter = -1e30f;
                var exit = 1e30f;
                var inside = box
                    ? DeviceMath.Slab(o.X, d.X, -half.X, half.X, ref enter, ref exit)
                        && DeviceMath.Slab(o.Y, d.Y, -half.Y, half.Y, ref enter, ref exit)
                        && DeviceMath.Slab(o.Z, d.Z, -half.Z, half.Z, ref enter, ref exit)
                    : DeviceMath.Slab(o.Z, d.Z, -half.Z, half.Z, ref enter, ref exit)
                        && CircleRange(o.X, o.Y, d.X, d.Y, half.X, ref enter, ref exit);
                if (inside)
                {
                    var ray = (iv * grid.CountU) + iu;
                    var segments = grid.Counts[ray];
                    var start = ray * 2 * grid.Capacity;
                    for (var s = 0; s < segments && !hit; s++)
                    {
                        hit = grid.Segments[start + (2 * s)] < exit && grid.Segments[start + (2 * s) + 1] > enter;
                    }
                }
            }
        }

        if (hit)
        {
            Atomic.Or(ref args.Body.Masks[pose], 1 << primitive);
        }
    }

    public static Float3 ToLocal(Float3 p, Float3 u, Float3 v, Float3 w) => new(Float3.Dot(p, u), Float3.Dot(p, v), Float3.Dot(p, w));

    /// <summary>Clips [enter, exit] to where the 2D line (ox, oy) + t·(dx, dy) is within <paramref name="radius"/> of the origin.</summary>
    public static bool CircleRange(float ox, float oy, float dx, float dy, float radius, ref float enter, ref float exit)
    {
        var a = (dx * dx) + (dy * dy);
        var c = (ox * ox) + (oy * oy) - (radius * radius);
        if (a < 1e-12f)
        {
            return c <= 0 && enter <= exit;
        }

        var b = (ox * dx) + (oy * dy);
        var discriminant = (b * b) - (a * c);
        if (discriminant < 0)
        {
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        enter = MathF.Max(enter, (-b - root) / a);
        exit = MathF.Min(exit, (-b + root) / a);
        return enter <= exit;
    }

    /// <summary>Triangle vs. axis-aligned box centred at the origin: separating axis test over 13 axes.</summary>
    public static bool TriangleBox(Float3 a, Float3 b, Float3 c, Float3 h)
    {
        if (Separated(new Float3(1, 0, 0), a, b, c, h) || Separated(new Float3(0, 1, 0), a, b, c, h) || Separated(new Float3(0, 0, 1), a, b, c, h))
        {
            return false;
        }

        var e0 = b - a;
        var e1 = c - b;
        var e2 = a - c;
        if (Separated(Float3.Cross(e0, e1), a, b, c, h))
        {
            return false;
        }

        return !(SeparatedByEdge(e0, a, b, c, h) || SeparatedByEdge(e1, a, b, c, h) || SeparatedByEdge(e2, a, b, c, h));
    }

    /// <summary>
    /// Triangle vs. cylinder along z (radius r, |z| ≤ hz). The distance to the axis is convex, so its minimum over
    /// the triangle clipped to the slab is either zero (the axis segment hits the triangle) or lies on the clipped
    /// boundary: triangle edges inside the slab and the chords cut by the two cap planes.
    /// </summary>
    public static bool TriangleCylinder(Float3 a, Float3 b, Float3 c, float r, float hz)
    {
        if ((a.Z > hz && b.Z > hz && c.Z > hz) || (a.Z < -hz && b.Z < -hz && c.Z < -hz))
        {
            return false;
        }

        var triangle = new GpuTriangle { A = a, B = b, C = c };
        if (MeshQueryKernels.Intersect(new Float3(0, 0, -hz), new Float3(0, 0, 2 * hz), triangle, out var t, out _, out _) && t >= 0 && t <= 1)
        {
            return true;
        }

        return EdgeNear(a, b, r, hz) || EdgeNear(b, c, r, hz) || EdgeNear(c, a, r, hz)
            || CapNear(a, b, c, r, hz) || CapNear(a, b, c, r, -hz);
    }

    private static bool Separated(Float3 axis, Float3 a, Float3 b, Float3 c, Float3 h)
    {
        var pa = Float3.Dot(axis, a);
        var pb = Float3.Dot(axis, b);
        var pc = Float3.Dot(axis, c);
        var r = (h.X * MathF.Abs(axis.X)) + (h.Y * MathF.Abs(axis.Y)) + (h.Z * MathF.Abs(axis.Z));
        return MathF.Min(MathF.Min(pa, pb), pc) > r || MathF.Max(MathF.Max(pa, pb), pc) < -r;
    }

    /// <summary>Axes e × x̂, e × ŷ, e × ẑ.</summary>
    private static bool SeparatedByEdge(Float3 e, Float3 a, Float3 b, Float3 c, Float3 h) =>
        Separated(new Float3(0, e.Z, -e.Y), a, b, c, h)
        || Separated(new Float3(-e.Z, 0, e.X), a, b, c, h)
        || Separated(new Float3(e.Y, -e.X, 0), a, b, c, h);

    /// <summary>Part of edge pq with |z| ≤ hz within r of the z axis.</summary>
    private static bool EdgeNear(Float3 p, Float3 q, float r, float hz)
    {
        var t0 = 0f;
        var t1 = 1f;
        if (!DeviceMath.Slab(p.Z, q.Z - p.Z, -hz, hz, ref t0, ref t1))
        {
            return false;
        }

        var a = p + ((q - p) * t0);
        var b = p + ((q - p) * t1);
        return SegmentDistanceSquared2D(a.X, a.Y, b.X, b.Y) <= r * r;
    }

    /// <summary>Chord of the triangle in the plane z = <paramref name="z"/> within r of the axis.</summary>
    private static bool CapNear(Float3 a, Float3 b, Float3 c, float r, float z)
    {
        var found = 0;
        var first = default(Float3);
        var second = default(Float3);
        Crossing(a, b, z, ref found, ref first, ref second);
        Crossing(b, c, z, ref found, ref first, ref second);
        Crossing(c, a, z, ref found, ref first, ref second);
        return found > 0 && SegmentDistanceSquared2D(first.X, first.Y, found > 1 ? second.X : first.X, found > 1 ? second.Y : first.Y) <= r * r;
    }

    private static void Crossing(Float3 p, Float3 q, float z, ref int found, ref Float3 first, ref Float3 second)
    {
        var dp = p.Z - z;
        var dq = q.Z - z;
        if (dp * dq <= 0 && dp != dq)
        {
            var point = p + ((q - p) * (dp / (dp - dq)));
            if (found == 0)
            {
                first = point;
            }
            else if (found == 1 || DistanceSquared(point, first) > DistanceSquared(second, first))
            {
                // A vertex on the plane is reported by both of its edges: keep the chord's two farthest points.
                second = point;
            }

            found++;
        }
    }

    private static float DistanceSquared(Float3 a, Float3 b) => Float3.Dot(a - b, a - b);

    /// <summary>Squared distance from the origin to the 2D segment ab.</summary>
    private static float SegmentDistanceSquared2D(float ax, float ay, float bx, float by)
    {
        var ex = bx - ax;
        var ey = by - ay;
        var length2 = (ex * ex) + (ey * ey);
        var t = length2 > 0 ? MathF.Min(MathF.Max(-((ax * ex) + (ay * ey)) / length2, 0), 1) : 0;
        var x = ax + (t * ex);
        var y = ay + (t * ey);
        return (x * x) + (y * y);
    }
}
