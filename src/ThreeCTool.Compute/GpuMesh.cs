using ILGPU;
using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute;

/// <summary>A mesh and its BVH uploaded to the accelerator for repeated queries.</summary>
public sealed class GpuMesh : IDisposable
{
    private readonly MemoryBuffer1D<GpuNode, Stride1D.Dense> _nodes;
    private readonly MemoryBuffer1D<GpuTriangle, Stride1D.Dense> _triangles;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _ids;

    public GpuMesh(ComputeContext compute, Mesh mesh)
        : this(compute, new Bvh(mesh))
    {
    }

    public GpuMesh(ComputeContext compute, Bvh bvh)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(bvh);
        Compute = compute;
        Mesh = bvh.Mesh;
        var (nodes, triangles, ids) = GpuBvhBuilder.Build(bvh);
        var accelerator = compute.Accelerator;

        // Zero-length buffers are not allowed on every backend; an empty mesh gets one inert element.
        _nodes = accelerator.Allocate1D(nodes.Length > 0 ? nodes : [new GpuNode { Skip = 1, MinX = 1, MinY = 1, MinZ = 1 }]);
        _triangles = accelerator.Allocate1D(triangles.Length > 0 ? triangles : [default(GpuTriangle)]);
        _ids = accelerator.Allocate1D(ids.Length > 0 ? ids : [-1]);
        NodeCount = nodes.Length;
    }

    public ComputeContext Compute { get; }

    public Mesh Mesh { get; }

    public BoundingBox Bounds => Mesh.Bounds;

    internal int NodeCount { get; }

    internal MeshView View => new()
    {
        Nodes = _nodes.View.SubView(0, NodeCount),
        Triangles = _triangles.View,
        TriangleIds = _ids.View,
    };

    public void Dispose()
    {
        _nodes.Dispose();
        _triangles.Dispose();
        _ids.Dispose();
    }
}
