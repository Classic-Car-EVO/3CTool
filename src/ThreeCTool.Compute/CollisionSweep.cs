using System.Numerics;

using ILGPU;
using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;

namespace ThreeCTool.Compute;

public enum CollisionShape
{
    /// <summary>Cylinder parallel to the tool axis.</summary>
    Cylinder = 0,
    Box = 1,
}

/// <summary>
/// Collision body part in the tool frame: tip at the origin, +Z along the tool axis towards the spindle, +X along the
/// pose's reference direction. For a cylinder <see cref="HalfExtents"/> is (radius, radius, half height).
/// </summary>
public readonly record struct CollisionPrimitive(CollisionShape Shape, Vector3 Center, Vector3 HalfExtents)
{
    /// <summary>Cylinder from <paramref name="bottom"/> to <paramref name="top"/> above the tip, optionally off-axis at (x, y).</summary>
    public static CollisionPrimitive Cylinder(float bottom, float top, float radius, float x = 0, float y = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(top, bottom);
        return new(CollisionShape.Cylinder, new Vector3(x, y, 0.5f * (bottom + top)), new Vector3(radius, radius, 0.5f * (top - bottom)));
    }

    public static CollisionPrimitive Box(Vector3 min, Vector3 max)
    {
        if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
        {
            throw new ArgumentException("Box must have a positive size.", nameof(max));
        }

        return new(CollisionShape.Box, 0.5f * (min + max), 0.5f * (max - min));
    }
}

/// <summary>
/// Tool placement in the part frame. <see cref="Reference"/> orients the tool frame's +X (projected perpendicular to
/// the axis); zero picks part +X (or +Y when the axis is along X), which suits rotationally symmetric bodies.
/// </summary>
public readonly record struct ToolPose(Vector3 Tip, Vector3 Axis, Vector3 Reference)
{
    public ToolPose(Vector3 tip, Vector3 axis)
        : this(tip, axis, Vector3.Zero)
    {
    }
}

/// <summary>
/// Collision sweeps of a body (holder, shank, FDM head parts; at most <see cref="MaxPrimitives"/> primitives) over
/// many poses. Each result is a bitmask per pose: bit k is set when primitive k collides.
/// </summary>
public static class CollisionSweep
{
    public const int MaxPrimitives = 32;

    /// <summary>
    /// Exact tests against the mesh triangles, with every primitive grown by <paramref name="clearance"/> (boxes grow
    /// their faces, so corners are conservative). With <paramref name="solid"/> the mesh is treated as a closed
    /// volume and a primitive fully inside it collides too.
    /// </summary>
    public static uint[] AgainstMesh(GpuMesh mesh, IReadOnlyList<CollisionPrimitive> body, ReadOnlySpan<ToolPose> poses, float clearance = 0, bool solid = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var compute = mesh.Compute;
        return Run(compute, body, poses, clearance, (kernelBody, count) =>
            compute.Kernel<CollisionMeshArgs>(CollisionKernel.AgainstMesh)(count, new CollisionMeshArgs
            {
                Body = kernelBody,
                Mesh = mesh.View,
                Solid = solid ? 1 : 0,
            }));
    }

    /// <summary>
    /// Tests against the material of the stock's Z dexels: a primitive collides when its span along any Z ray in its
    /// footprint overlaps a material segment (features narrower than the stock resolution can be missed).
    /// </summary>
    public static uint[] AgainstStock(DexelStock stock, IReadOnlyList<CollisionPrimitive> body, ReadOnlySpan<ToolPose> poses, float clearance = 0)
    {
        ArgumentNullException.ThrowIfNull(stock);
        var compute = stock.Compute;
        return Run(compute, body, poses, clearance, (kernelBody, count) =>
            compute.Kernel<CollisionStockArgs>(CollisionKernel.AgainstStock)(count, new CollisionStockArgs
            {
                Body = kernelBody,
                Grid = stock.GridView(DexelAxis.Z),
            }));
    }

    /// <summary>Unit tool frame (U, V, W) for a pose.</summary>
    public static (Vector3 U, Vector3 V, Vector3 W) Frame(ToolPose pose)
    {
        var w = Vector3.Normalize(pose.Axis);
        if (!float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Z))
        {
            throw new ArgumentException("Tool axis must be non-zero.", nameof(pose));
        }

        var reference = pose.Reference - (w * Vector3.Dot(pose.Reference, w));
        if (reference.LengthSquared() < 1e-12f)
        {
            var fallback = MathF.Abs(w.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            reference = fallback - (w * Vector3.Dot(fallback, w));
        }

        var u = Vector3.Normalize(reference);
        return (u, Vector3.Cross(w, u), w);
    }

    private static uint[] Run(ComputeContext compute, IReadOnlyList<CollisionPrimitive> body, ReadOnlySpan<ToolPose> poses, float clearance, Action<CollisionBodyView, int> launch)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(clearance);
        if (body.Count is 0 or > MaxPrimitives)
        {
            throw new ArgumentException($"A collision body needs 1 to {MaxPrimitives} primitives.", nameof(body));
        }

        var result = new uint[poses.Length];
        if (poses.Length == 0)
        {
            return result;
        }

        var poseData = new Float3[4 * poses.Length];
        for (var i = 0; i < poses.Length; i++)
        {
            var (u, v, w) = Frame(poses[i]);
            poseData[4 * i] = Float3.From(poses[i].Tip);
            poseData[(4 * i) + 1] = Float3.From(u);
            poseData[(4 * i) + 2] = Float3.From(v);
            poseData[(4 * i) + 3] = Float3.From(w);
        }

        var primitives = new Float3[2 * body.Count];
        var shapes = new int[body.Count];
        for (var k = 0; k < body.Count; k++)
        {
            primitives[2 * k] = Float3.From(body[k].Center);
            primitives[(2 * k) + 1] = Float3.From(body[k].HalfExtents);
            shapes[k] = (int)body[k].Shape;
        }

        var masks = new int[poses.Length];
        var batch = Math.Min(poses.Length, compute.BatchSize(4 * 12 * body.Count));
        lock (compute.Gate)
        {
            var accelerator = compute.Accelerator;
            using var poseBuffer = accelerator.Allocate1D<Float3>(4L * batch);
            using var primitiveBuffer = accelerator.Allocate1D(primitives);
            using var shapeBuffer = accelerator.Allocate1D(shapes);
            using var maskBuffer = accelerator.Allocate1D<int>(batch);
            for (var start = 0; start < poses.Length; start += batch)
            {
                var count = Math.Min(batch, poses.Length - start);
                compute.Upload(poseBuffer.View, poseData.AsSpan(4 * start, 4 * count));
                maskBuffer.MemSetToZero();
                launch(
                    new CollisionBodyView
                    {
                        Poses = poseBuffer.View,
                        Primitives = primitiveBuffer.View,
                        Shapes = shapeBuffer.View,
                        PrimitiveCount = body.Count,
                        Clearance = clearance,
                        Masks = maskBuffer.View,
                    },
                    count * body.Count);
                compute.Download(maskBuffer.View, masks.AsSpan(start, count));
            }
        }

        for (var i = 0; i < masks.Length; i++)
        {
            result[i] = unchecked((uint)masks[i]);
        }

        return result;
    }
}
