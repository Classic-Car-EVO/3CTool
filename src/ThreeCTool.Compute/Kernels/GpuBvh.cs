using ILGPU;
using ILGPU.Runtime;

using ThreeCTool.Geometry;

namespace ThreeCTool.Compute.Kernels;

/// <summary>BVH node in preorder: an inner node's first child follows it; <see cref="Skip"/> is the index after its subtree.</summary>
/// <remarks>
/// Traversal loops must have a single back-edge (no <c>continue</c>): ILGPU 1.5.3's OpenCL backend mis-assigns
/// loop-carried values across multiple back-edges and the kernel then reads garbage (the CPU backend is unaffected).
/// </remarks>
internal struct GpuNode
{
    public float MinX;
    public float MinY;
    public float MinZ;
    public float MaxX;
    public float MaxY;
    public float MaxZ;
    public int Start;
    public int Count;
    public int Skip;

    public readonly Float3 Min => new(MinX, MinY, MinZ);

    public readonly Float3 Max => new(MaxX, MaxY, MaxZ);
}

internal struct GpuTriangle
{
    public Float3 A;
    public Float3 B;
    public Float3 C;
}

/// <summary>Device-side view of a <see cref="GpuMesh"/>.</summary>
internal struct MeshView
{
    public ArrayView<GpuNode> Nodes;
    public ArrayView<GpuTriangle> Triangles;
    public ArrayView<int> TriangleIds;

    public readonly int NodeCount => (int)Nodes.Length;
}

/// <summary>Builds the flattened preorder node array from a <see cref="Bvh"/>.</summary>
internal static class GpuBvhBuilder
{
    public static (GpuNode[] Nodes, GpuTriangle[] Triangles, int[] Ids) Build(Bvh bvh)
    {
        var mesh = bvh.Mesh;
        var triangles = new GpuTriangle[bvh.TriangleOrder.Length];
        for (var i = 0; i < triangles.Length; i++)
        {
            var (a, b, c) = mesh.GetTriangle(bvh.TriangleOrder[i]);
            triangles[i] = new GpuTriangle { A = Float3.From(a), B = Float3.From(b), C = Float3.From(c) };
        }

        var nodes = new List<GpuNode>(bvh.Nodes.Length);
        if (triangles.Length > 0)
        {
            Emit(bvh.Nodes, 0, nodes);
        }

        return ([.. nodes], triangles, [.. bvh.TriangleOrder]);
    }

    private static void Emit(BvhNode[] source, int index, List<GpuNode> nodes)
    {
        var node = source[index];
        var self = nodes.Count;
        nodes.Add(default);
        if (node.Count == 0)
        {
            Emit(source, node.LeftOrFirst, nodes);
            Emit(source, node.LeftOrFirst + 1, nodes);
        }

        nodes[self] = new GpuNode
        {
            MinX = node.Min.X,
            MinY = node.Min.Y,
            MinZ = node.Min.Z,
            MaxX = node.Max.X,
            MaxY = node.Max.Y,
            MaxZ = node.Max.Z,
            Start = node.Count > 0 ? node.LeftOrFirst : -1,
            Count = node.Count,
            Skip = nodes.Count,
        };
    }
}
