using System.Numerics;

using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;

namespace ThreeCTool.Compute;

/// <summary>
/// Drop-cutter queries: how far along the tool axis the cutter tip can descend before touching the mesh.
/// Results are tip positions along the axis; <see cref="float.NegativeInfinity"/> means no contact.
/// </summary>
public static class DropCutter
{
    /// <summary>General form: per query an origin and a tool axis (tool "up", normalized here).</summary>
    public static float[] Drop(GpuMesh mesh, CutterProfile cutter, ReadOnlySpan<Vector3> origins, ReadOnlySpan<Vector3> axes)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (origins.Length != axes.Length)
        {
            throw new ArgumentException("Origins and axes must have the same length.", nameof(axes));
        }

        var originData = new Float3[origins.Length];
        var axisData = new Float3[axes.Length];
        for (var i = 0; i < origins.Length; i++)
        {
            originData[i] = Float3.From(origins[i]);
            axisData[i] = Float3.From(Vector3.Normalize(axes[i]));
        }

        return Run(mesh, cutter, originData, axisData);
    }

    /// <summary>
    /// 3-axis grid: tool along +Z at (origin.X + i·step, origin.Y + j·step). Returns tip Z, row-major
    /// (index j·countX + i).
    /// </summary>
    public static float[] Grid(GpuMesh mesh, CutterProfile cutter, Vector2 origin, float step, int countX, int countY)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countY);
        var origins = new Float3[countX * countY];
        var axes = new Float3[origins.Length];
        for (var j = 0; j < countY; j++)
        {
            for (var i = 0; i < countX; i++)
            {
                origins[(j * countX) + i] = new Float3(origin.X + (i * step), origin.Y + (j * step), 0);
                axes[(j * countX) + i] = new Float3(0, 0, 1);
            }
        }

        return Run(mesh, cutter, origins, axes);
    }

    /// <summary>
    /// Rotary (4th axis) drop: the mesh is in the part frame with the A axis along X through Y = Z = 0. For each
    /// angle and X the tool points radially along (0, sin A, cos A) (machine +Z at that A). Returns the tip radius,
    /// index a·xs.Length + i.
    /// </summary>
    public static float[] Rotary(GpuMesh mesh, CutterProfile cutter, ReadOnlySpan<float> xs, ReadOnlySpan<float> angleDegrees)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var origins = new Float3[xs.Length * angleDegrees.Length];
        var axes = new Float3[origins.Length];
        for (var a = 0; a < angleDegrees.Length; a++)
        {
            var (sin, cos) = MathF.SinCos(angleDegrees[a] * MathF.PI / 180f);
            for (var i = 0; i < xs.Length; i++)
            {
                origins[(a * xs.Length) + i] = new Float3(xs[i], 0, 0);
                axes[(a * xs.Length) + i] = new Float3(0, sin, cos);
            }
        }

        return Run(mesh, cutter, origins, axes);
    }

    private static float[] Run(GpuMesh mesh, CutterProfile cutter, Float3[] origins, Float3[] axes)
    {
        var compute = mesh.Compute;
        var result = new float[origins.Length];
        if (origins.Length == 0)
        {
            return result;
        }

        var batch = Math.Min(origins.Length, compute.BatchSize(28));
        var accelerator = compute.Accelerator;
        lock (compute.Gate)
        {
            using var originBuffer = accelerator.Allocate1D<Float3>(batch);
            using var axisBuffer = accelerator.Allocate1D<Float3>(batch);
            using var resultBuffer = accelerator.Allocate1D<float>(batch);
            var kernel = compute.Kernel<DropCutterArgs>(DropCutterKernel.Run);
            for (var start = 0; start < origins.Length; start += batch)
            {
                var count = Math.Min(batch, origins.Length - start);
                compute.Upload(originBuffer.View, origins.AsSpan(start, count));
                compute.Upload(axisBuffer.View, axes.AsSpan(start, count));
                kernel(count, new DropCutterArgs
                {
                    Mesh = mesh.View,
                    Cutter = cutter,
                    Origins = originBuffer.View,
                    Axes = axisBuffer.View,
                    Result = resultBuffer.View,
                });
                compute.Download(resultBuffer.View, result.AsSpan(start, count));
            }
        }

        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] <= DeviceMath.NoContact * 0.5f)
            {
                result[i] = float.NegativeInfinity;
            }
        }

        return result;
    }
}
