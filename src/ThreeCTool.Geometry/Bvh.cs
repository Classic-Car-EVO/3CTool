using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ThreeCTool.Geometry;

/// <summary>32-byte node: leaf when <see cref="Count"/> &gt; 0 (triangles First..First+Count), else children at LeftChild and LeftChild + 1.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BvhNode
{
    public Vector3 Min;
    public int LeftOrFirst;
    public Vector3 Max;
    public int Count;
}

public readonly record struct RayHit(int Triangle, float Distance, float U, float V);

/// <summary>Bounding volume hierarchy over a mesh, stored as flat arrays for GPU upload.</summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Flat arrays are passed directly to GPU kernels.")]
public sealed class Bvh
{
    private const int MaxLeafSize = 4;
    private const int BinCount = 12;

    private readonly Mesh _mesh;
    private readonly Vector3[] _centroids;
    private readonly BoundingBox[] _triangleBounds;
    private readonly List<BvhNode> _nodes = [];
    private int _depth;

    public Bvh(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _mesh = mesh;
        var count = mesh.TriangleCount;
        TriangleOrder = new int[count];
        _centroids = new Vector3[count];
        _triangleBounds = new BoundingBox[count];
        for (var t = 0; t < count; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            TriangleOrder[t] = t;
            _centroids[t] = (a + b + c) / 3f;
            _triangleBounds[t] = new BoundingBox(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
        }

        _nodes.Add(default);
        if (count > 0)
        {
            Build(0, 0, count, 1);
        }

        Nodes = [.. _nodes];
    }

    public Mesh Mesh => _mesh;

    public BvhNode[] Nodes { get; }

    /// <summary>Depth of the tree; a traversal stack needs at most Depth + 1 entries.</summary>
    public int Depth => _depth;

    /// <summary>Triangle indices in leaf order; leaves reference ranges of this array.</summary>
    public int[] TriangleOrder { get; }

    /// <summary>Closest hit along the ray within <paramref name="maxDistance"/>.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit) =>
        Trace(origin, direction, maxDistance, anyHit: false, out hit);

    /// <summary>True if anything is hit within <paramref name="maxDistance"/> (occlusion query).</summary>
    public bool Occluded(Vector3 origin, Vector3 direction, float maxDistance) =>
        Trace(origin, direction, maxDistance, anyHit: true, out _);

    /// <summary>Calls <paramref name="visit"/> for each triangle whose bounds overlap <paramref name="box"/>.</summary>
    public void Query(BoundingBox box, Action<int> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        if (TriangleOrder.Length == 0)
        {
            return;
        }

        Span<int> stack = _depth < 256 ? stackalloc int[256] : new int[_depth + 1];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var node = ref Nodes[stack[--top]];
            if (!box.Intersects(new BoundingBox(node.Min, node.Max)))
            {
                continue;
            }

            if (node.Count > 0)
            {
                for (var i = 0; i < node.Count; i++)
                {
                    var t = TriangleOrder[node.LeftOrFirst + i];
                    if (box.Intersects(_triangleBounds[t]))
                    {
                        visit(t);
                    }
                }
            }
            else
            {
                stack[top++] = node.LeftOrFirst;
                stack[top++] = node.LeftOrFirst + 1;
            }
        }
    }

    private bool Trace(Vector3 origin, Vector3 direction, float maxDistance, bool anyHit, out RayHit hit)
    {
        hit = default;
        if (TriangleOrder.Length == 0)
        {
            return false;
        }

        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        var closest = maxDistance;
        var found = false;
        Span<int> stack = _depth < 256 ? stackalloc int[256] : new int[_depth + 1];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var node = ref Nodes[stack[--top]];
            if (!SlabTest(node.Min, node.Max, origin, inverse, closest))
            {
                continue;
            }

            if (node.Count > 0)
            {
                for (var i = 0; i < node.Count; i++)
                {
                    var t = TriangleOrder[node.LeftOrFirst + i];
                    if (IntersectTriangle(t, origin, direction, closest, out var distance, out var u, out var v))
                    {
                        closest = distance;
                        hit = new RayHit(t, distance, u, v);
                        found = true;
                        if (anyHit)
                        {
                            return true;
                        }
                    }
                }
            }
            else
            {
                stack[top++] = node.LeftOrFirst;
                stack[top++] = node.LeftOrFirst + 1;
            }
        }

        return found;
    }

    private static bool SlabTest(Vector3 min, Vector3 max, Vector3 origin, Vector3 inverse, float maxDistance)
    {
        var t1 = (min - origin) * inverse;
        var t2 = (max - origin) * inverse;
        var tMin = Vector3.Min(t1, t2);
        var tMax = Vector3.Max(t1, t2);
        var enter = MathF.Max(MathF.Max(tMin.X, tMin.Y), MathF.Max(tMin.Z, 0f));
        var exit = MathF.Min(MathF.Min(tMax.X, tMax.Y), MathF.Min(tMax.Z, maxDistance));
        return enter <= exit;
    }

    // Möller–Trumbore, double-sided.
    private bool IntersectTriangle(int triangle, Vector3 origin, Vector3 direction, float maxDistance, out float distance, out float u, out float v)
    {
        distance = u = v = 0;
        var (a, b, c) = _mesh.GetTriangle(triangle);
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-12f)
        {
            return false;
        }

        var inverse = 1f / determinant;
        var s = origin - a;
        u = Vector3.Dot(s, p) * inverse;
        if (u < 0 || u > 1)
        {
            return false;
        }

        var q = Vector3.Cross(s, edge1);
        v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        distance = Vector3.Dot(edge2, q) * inverse;
        return distance > 0 && distance < maxDistance;
    }

    private void Build(int nodeIndex, int first, int count, int depth)
    {
        _depth = Math.Max(_depth, depth);
        var bounds = BoundingBox.Empty;
        var centroidBounds = BoundingBox.Empty;
        for (var i = first; i < first + count; i++)
        {
            bounds = bounds.Union(_triangleBounds[TriangleOrder[i]]);
            centroidBounds = centroidBounds.Include(_centroids[TriangleOrder[i]]);
        }

        if (count <= MaxLeafSize || !TrySplit(first, count, centroidBounds, out var mid))
        {
            _nodes[nodeIndex] = new BvhNode { Min = bounds.Min, Max = bounds.Max, LeftOrFirst = first, Count = count };
            return;
        }

        var left = _nodes.Count;
        _nodes.Add(default);
        _nodes.Add(default);
        _nodes[nodeIndex] = new BvhNode { Min = bounds.Min, Max = bounds.Max, LeftOrFirst = left, Count = 0 };
        Build(left, first, mid - first, depth + 1);
        Build(left + 1, mid, first + count - mid, depth + 1);
    }

    // Binned surface-area heuristic on the longest centroid axis; falls back to a median split.
    private bool TrySplit(int first, int count, BoundingBox centroidBounds, out int mid)
    {
        var extent = centroidBounds.Size;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var lo = Component(centroidBounds.Min, axis);
        var span = Component(extent, axis);
        mid = first + (count / 2);
        if (span <= 0)
        {
            return false;
        }

        Span<int> binCounts = stackalloc int[BinCount];
        var binBounds = new BoundingBox[BinCount];
        binBounds.AsSpan().Fill(BoundingBox.Empty);
        var scale = BinCount / span;
        for (var i = first; i < first + count; i++)
        {
            var t = TriangleOrder[i];
            var bin = Math.Min(BinCount - 1, (int)((Component(_centroids[t], axis) - lo) * scale));
            binCounts[bin]++;
            binBounds[bin] = binBounds[bin].Union(_triangleBounds[t]);
        }

        var bestCost = float.PositiveInfinity;
        var bestSplit = -1;
        for (var split = 1; split < BinCount; split++)
        {
            var leftBox = BoundingBox.Empty;
            var rightBox = BoundingBox.Empty;
            int leftCount = 0, rightCount = 0;
            for (var b = 0; b < split; b++)
            {
                leftBox = leftBox.Union(binBounds[b]);
                leftCount += binCounts[b];
            }

            for (var b = split; b < BinCount; b++)
            {
                rightBox = rightBox.Union(binBounds[b]);
                rightCount += binCounts[b];
            }

            if (leftCount == 0 || rightCount == 0)
            {
                continue;
            }

            var cost = (leftCount * Area(leftBox)) + (rightCount * Area(rightBox));
            if (cost < bestCost)
            {
                bestCost = cost;
                bestSplit = split;
            }
        }

        if (bestSplit < 0)
        {
            return false;
        }

        var i0 = first;
        var j = first + count - 1;
        while (i0 <= j)
        {
            var t = TriangleOrder[i0];
            var bin = Math.Min(BinCount - 1, (int)((Component(_centroids[t], axis) - lo) * scale));
            if (bin < bestSplit)
            {
                i0++;
            }
            else
            {
                (TriangleOrder[i0], TriangleOrder[j]) = (TriangleOrder[j], TriangleOrder[i0]);
                j--;
            }
        }

        mid = i0;
        return mid > first && mid < first + count;
    }

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static float Area(BoundingBox box)
    {
        var s = box.Size;
        return (s.X * s.Y) + (s.Y * s.Z) + (s.Z * s.X);
    }
}
