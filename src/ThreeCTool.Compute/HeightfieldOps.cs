using ILGPU;
using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;

namespace ThreeCTool.Compute;

/// <summary>
/// Cutter offsets of sampled heightfields (row-major, index iy·countX + ix, spacing resolution; NaN or −∞ = no
/// surface). Results are −∞ where no surface sample lies within the cutter radius.
/// </summary>
public static class HeightfieldOps
{
    /// <summary>Largest cutter radius in cells.</summary>
    public const int MaxReach = 1024;

    /// <summary>
    /// Lowest tip height at each sample for a vertical cutter that does not cut into any sample within its radius
    /// (the inverse-tool offset; exact for the samples, so features between samples may be shaved).
    /// </summary>
    public static float[] CutterOffset(ComputeContext compute, ReadOnlySpan<float> heights, int countX, int countY, float resolution, CutterProfile cutter)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        if (heights.Length != countX * countY)
        {
            throw new ArgumentException("Heights must have countX·countY samples.", nameof(heights));
        }

        var reach = (int)MathF.Floor(cutter.Radius / resolution);
        if (reach > MaxReach)
        {
            throw new ArgumentException($"Cutter radius exceeds {MaxReach} cells; use a coarser resolution.", nameof(cutter));
        }

        var input = new float[heights.Length];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = float.IsNaN(heights[i]) || heights[i] <= DeviceMath.NoContact ? DeviceMath.NoContact : heights[i];
        }

        var result = new float[input.Length];
        lock (compute.Gate)
        {
            var accelerator = compute.Accelerator;
            using var heightBuffer = accelerator.Allocate1D(input);
            using var resultBuffer = accelerator.Allocate1D<float>(input.Length);
            compute.Kernel<HeightfieldOffsetArgs>(HeightfieldKernel.Offset)(input.Length, new HeightfieldOffsetArgs
            {
                Heights = heightBuffer.View,
                Result = resultBuffer.View,
                CountX = countX,
                CountY = countY,
                Resolution = resolution,
                Reach = reach,
                Cutter = cutter,
            });
            compute.Download(resultBuffer.View, result);
        }

        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] <= DeviceMath.NoContact)
            {
                result[i] = float.NegativeInfinity;
            }
        }

        return result;
    }

    /// <summary>Upward offset surface of a heightfield by <paramref name="radius"/>: the centre height of a dropped ball.</summary>
    public static float[] BallOffset(ComputeContext compute, ReadOnlySpan<float> heights, int countX, int countY, float resolution, float radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        var result = CutterOffset(compute, heights, countX, countY, resolution, CutterProfile.Ball(2 * radius));
        for (var i = 0; i < result.Length; i++)
        {
            result[i] += radius;
        }

        return result;
    }
}
