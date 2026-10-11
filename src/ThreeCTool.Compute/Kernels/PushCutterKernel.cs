using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

internal struct PushCutterArgs
{
    public MeshView Mesh;
    public CutterProfile Cutter;

    /// <summary>Per fiber: start point (z = fiber height).</summary>
    public ArrayView<Float3> Starts;

    /// <summary>Per fiber: (dx, dy, length) with (dx, dy) a unit XY direction.</summary>
    public ArrayView<Float3> Directions;

    /// <summary>Per fiber: <see cref="PushCutterKernel.Capacity"/> pairs of (enter, exit).</summary>
    public ArrayView<float> Intervals;

    /// <summary>Per fiber: interval count; negative when the list overflowed (results then conservatively merged).</summary>
    public ArrayView<int> Counts;
}

/// <summary>
/// Push-cutter (waterline fibers): for a fiber at height z, the parameter intervals where a cutter whose tip
/// moves along the fiber would intersect the mesh. The cutter is the profile extended upwards as an infinite
/// cylinder (3-axis: nothing may be above it). Per triangle the blocked set is convex in t, so its interval is the
/// hull of all touching positions: facet contacts (up-facing) and the exact t-range of each edge.
/// </summary>
internal static class PushCutterKernel
{
    public const int Capacity = 32;

    public static void Run(Index1D index, PushCutterArgs args)
    {
        var start = args.Starts[index];
        var direction = args.Directions[index];
        var d = new Float3(direction.X, direction.Y, 0);
        var length = direction.Z;
        var cutter = args.Cutter;
        var radius = cutter.Radius;

        var list = LocalMemory.Allocate<float>(2 * Capacity);
        var count = 0;
        var overflow = false;

        var end = start + (d * length);
        var boxMin = new Float3(MathF.Min(start.X, end.X) - radius, MathF.Min(start.Y, end.Y) - radius, start.Z);
        var boxMax = new Float3(MathF.Max(start.X, end.X) + radius, MathF.Max(start.Y, end.Y) + radius, 1e30f);

        var mesh = args.Mesh;
        var nodeCount = mesh.NodeCount;
        var i = 0;
        while (i < nodeCount)
        {
            var node = mesh.Nodes[i];
            var next = node.Skip;
            if (DeviceMath.BoxesOverlap(node.Min, node.Max, boxMin, boxMax) && !Covered(list, count, node, start, d, radius, length))
            {
                if (node.Count > 0)
                {
                    for (var k = 0; k < node.Count; k++)
                    {
                        var triangle = mesh.Triangles[node.Start + k];
                        if (Triangle(triangle, cutter, start, d, out var enter, out var exit))
                        {
                            enter = MathF.Max(enter, 0);
                            exit = MathF.Min(exit, length);
                            if (enter <= exit)
                            {
                                count = IntervalList.Insert(list, count, Capacity, enter, exit, ref overflow);
                            }
                        }
                    }

                    // The whole segment is blocked: nothing can change any more.
                    if (count == 1 && list[0] <= 0 && list[1] >= length)
                    {
                        next = nodeCount;
                    }
                }
                else
                {
                    next = i + 1;
                }
            }

            i = next;
        }

        var offset = index * 2 * Capacity;
        for (var k = 0; k < 2 * count; k++)
        {
            args.Intervals[offset + k] = list[k];
        }

        args.Counts[index] = overflow ? -count : count;
    }

    /// <summary>True when every t the node could block is already inside one blocked interval.</summary>
    private static bool Covered(ArrayView<float> list, int count, GpuNode node, Float3 start, Float3 d, float radius, float length)
    {
        if (count == 0)
        {
            return false;
        }

        var centerT = ((0.5f * (node.MinX + node.MaxX) - start.X) * d.X) + ((0.5f * (node.MinY + node.MaxY) - start.Y) * d.Y);
        var halfT = (0.5f * (node.MaxX - node.MinX) * MathF.Abs(d.X)) + (0.5f * (node.MaxY - node.MinY) * MathF.Abs(d.Y)) + radius;
        var a = MathF.Max(centerT - halfT, 0);
        var b = MathF.Min(centerT + halfT, length);
        var covered = false;
        for (var k = 0; k < count; k++)
        {
            covered |= list[2 * k] <= a && list[(2 * k) + 1] >= b;
        }

        return covered;
    }

    /// <summary>Blocked t-interval of one triangle for the fiber start + t·d at height start.Z.</summary>
    public static bool Triangle(GpuTriangle triangle, CutterProfile cutter, Float3 start, Float3 d, out float enter, out float exit)
    {
        enter = 1e30f;
        exit = -1e30f;
        var z = start.Z;
        if (MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z)) < z)
        {
            return false;
        }

        Facet(triangle, cutter, start, d, ref enter, ref exit);
        Edge(triangle.A, triangle.B, cutter, start, d, ref enter, ref exit);
        Edge(triangle.B, triangle.C, cutter, start, d, ref enter, ref exit);
        Edge(triangle.C, triangle.A, cutter, start, d, ref enter, ref exit);
        return enter <= exit;
    }

    private static void Facet(GpuTriangle triangle, CutterProfile cutter, Float3 start, Float3 d, ref float enter, ref float exit)
    {
        var a = triangle.A;
        var n = Float3.Cross(triangle.B - a, triangle.C - a);
        var length = Float3.Length(n);
        if (length < 1e-12f)
        {
            return;
        }

        n *= 1f / length;
        if (n.Z < 0)
        {
            n = -n;
        }

        var nxy = MathF.Sqrt((n.X * n.X) + (n.Y * n.Y));
        var nd = (n.X * d.X) + (n.Y * d.Y);

        // Horizontal, vertical or fiber-parallel facets reach their extremes on edges.
        if (n.Z < 1e-6f || nxy < 1e-6f || MathF.Abs(nd) < 1e-9f)
        {
            return;
        }

        var contactRadius = cutter.ContactRadius(nxy / n.Z);
        var offset = new Float3(-contactRadius * n.X / nxy, -contactRadius * n.Y / nxy, cutter.HeightAt(contactRadius));
        var t = -Float3.Dot(n, start + offset - a) / nd;
        var contact = start + (d * t) + offset;
        if (DeviceMath.PointInTriangle2D(contact.X, contact.Y, a.X, a.Y, triangle.B.X, triangle.B.Y, triangle.C.X, triangle.C.Y))
        {
            enter = MathF.Min(enter, t);
            exit = MathF.Max(exit, t);
        }
    }

    private static void Edge(Float3 p0, Float3 p1, CutterProfile cutter, Float3 start, Float3 d, ref float enter, ref float exit)
    {
        // Part of the edge at or above the fiber height.
        var z = start.Z;
        float lo = 0;
        float hi = 1;
        var dz = p1.Z - p0.Z;
        if (MathF.Abs(dz) < 1e-12f)
        {
            if (p0.Z < z)
            {
                return;
            }
        }
        else
        {
            var sz = (z - p0.Z) / dz;
            if (dz > 0)
            {
                lo = MathF.Max(lo, sz);
            }
            else
            {
                hi = MathF.Min(hi, sz);
            }

            if (lo > hi)
            {
                return;
            }
        }

        var function = new EdgeReach { P0 = p0, E = p1 - p0, Start = start, D = d, Cutter = cutter, Mode = 0 };

        // ψ(s) = ρ(s) - |perp(s)| is concave: find its maximum, then the sub-range where it is non-negative.
        var best = DeviceMath.GoldenMax(function, lo, hi, out var peak);
        if (best < 0)
        {
            return;
        }

        if (function.Evaluate(lo) < 0)
        {
            lo = DeviceMath.Bisect(function, lo, peak);
        }

        if (function.Evaluate(hi) < 0)
        {
            hi = DeviceMath.Bisect(function, peak, hi);
        }

        function.Mode = 1;
        var tMax = DeviceMath.GoldenMax(function, lo, hi, out _);
        function.Mode = 2;
        var tMin = -DeviceMath.GoldenMax(function, lo, hi, out _);
        enter = MathF.Min(enter, tMin);
        exit = MathF.Max(exit, tMax);
    }

    /// <summary>
    /// For the edge point p(s): Mode 0 → ρ − |perp| (reach margin); Mode 1 → largest t touching p(s);
    /// Mode 2 → minus the smallest t touching p(s). All concave in s.
    /// </summary>
    private struct EdgeReach : IScalarFunction
    {
        public Float3 P0;
        public Float3 E;
        public Float3 Start;
        public Float3 D;
        public CutterProfile Cutter;
        public int Mode;

        public readonly float Evaluate(float s)
        {
            var px = P0.X + (s * E.X) - Start.X;
            var py = P0.Y + (s * E.Y) - Start.Y;
            var height = MathF.Max(P0.Z + (s * E.Z) - Start.Z, 0);
            var rho = Cutter.RadiusAt(height);
            var along = (px * D.X) + (py * D.Y);
            var perp = (py * D.X) - (px * D.Y);
            if (Mode == 0)
            {
                return rho - MathF.Abs(perp);
            }

            var half = MathF.Sqrt(MathF.Max((rho * rho) - (perp * perp), 0));
            return Mode == 1 ? along + half : half - along;
        }
    }
}
