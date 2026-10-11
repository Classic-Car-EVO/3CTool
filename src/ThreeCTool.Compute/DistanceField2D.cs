using System.Numerics;

using ILGPU;
using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;

namespace ThreeCTool.Compute;

/// <summary>
/// Exact Euclidean distance field on a 2D cell grid (e.g. layer masks for tree supports). Cell (ix, iy) is centred at
/// origin + (i + 0.5)·resolution; values are in mm and +∞ where there is nothing to measure to.
/// </summary>
public sealed class DistanceField2D
{
    /// <summary>Largest grid side: keeps squared cell distances within 32-bit integers.</summary>
    public const int MaxCount = 32767;

    private DistanceField2D(Vector2 origin, float resolution, int countX, int countY, float[] values)
    {
        Origin = origin;
        Resolution = resolution;
        CountX = countX;
        CountY = countY;
        Values = values;
    }

    public Vector2 Origin { get; }

    public float Resolution { get; }

    public int CountX { get; }

    public int CountY { get; }

    /// <summary>Row-major values, index iy·CountX + ix.</summary>
    public IReadOnlyList<float> Values { get; }

    public float this[int ix, int iy] => Values[(iy * CountX) + ix];

    /// <summary>Distance from each cell centre to the nearest site cell centre.</summary>
    public static DistanceField2D FromSites(ComputeContext compute, ReadOnlySpan<bool> sites, int countX, int countY, float resolution, Vector2 origin = default)
    {
        var squared = SquaredDistances(compute, sites, countX, countY, resolution, invert: false);
        var values = new float[squared.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = squared[i] == DistanceTransformKernel.Infinite ? float.PositiveInfinity : MathF.Sqrt(squared[i]) * resolution;
        }

        return new(origin, resolution, countX, countY, values);
    }

    /// <summary>
    /// Signed distance to the boundary of the <paramref name="inside"/> cells, negative inside. The boundary is taken
    /// half a cell from the nearest cell centre of the other kind, so cells either side of an edge read ∓resolution/2.
    /// </summary>
    public static DistanceField2D FromRegion(ComputeContext compute, ReadOnlySpan<bool> inside, int countX, int countY, float resolution, Vector2 origin = default)
    {
        var toInside = SquaredDistances(compute, inside, countX, countY, resolution, invert: false);
        var toOutside = SquaredDistances(compute, inside, countX, countY, resolution, invert: true);
        var values = new float[toInside.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = inside[i]
                ? toOutside[i] == DistanceTransformKernel.Infinite ? float.NegativeInfinity : -(MathF.Sqrt(toOutside[i]) - 0.5f) * resolution
                : toInside[i] == DistanceTransformKernel.Infinite ? float.PositiveInfinity : (MathF.Sqrt(toInside[i]) - 0.5f) * resolution;
        }

        return new(origin, resolution, countX, countY, values);
    }

    /// <summary>Bilinear interpolation between cell centres, clamped to the grid.</summary>
    public float Sample(Vector2 point)
    {
        var x = Math.Clamp(((point.X - Origin.X) / Resolution) - 0.5f, 0, CountX - 1);
        var y = Math.Clamp(((point.Y - Origin.Y) / Resolution) - 0.5f, 0, CountY - 1);
        var ix = Math.Min((int)x, Math.Max(CountX - 2, 0));
        var iy = Math.Min((int)y, Math.Max(CountY - 2, 0));
        var fx = x - ix;
        var fy = y - iy;
        var ix1 = Math.Min(ix + 1, CountX - 1);
        var iy1 = Math.Min(iy + 1, CountY - 1);
        var bottom = (this[ix, iy] * (1 - fx)) + (this[ix1, iy] * fx);
        var top = (this[ix, iy1] * (1 - fx)) + (this[ix1, iy1] * fx);
        return (bottom * (1 - fy)) + (top * fy);
    }

    /// <summary>Squared distance in cells from each cell to the nearest site (cells where sites[i] != invert).</summary>
    internal static int[] SquaredDistances(ComputeContext compute, ReadOnlySpan<bool> sites, int countX, int countY, float resolution, bool invert)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countY);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(countX, MaxCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(countY, MaxCount);
        var cells = countX * countY;
        if (sites.Length != cells)
        {
            throw new ArgumentException("Mask size must be countX·countY.", nameof(sites));
        }

        var initial = new int[cells];
        for (var i = 0; i < cells; i++)
        {
            initial[i] = sites[i] != invert ? 0 : DistanceTransformKernel.Infinite;
        }

        var result = new int[cells];
        lock (compute.Gate)
        {
            var accelerator = compute.Accelerator;
            using var a = accelerator.Allocate1D(initial);
            using var b = accelerator.Allocate1D<int>(cells);
            using var vertices = accelerator.Allocate1D<int>(cells);
            var kernel = compute.Kernel<DistanceTransformArgs>(DistanceTransformKernel.Run);
            kernel(countY, new DistanceTransformArgs
            {
                Input = a.View,
                Output = b.View,
                Vertices = vertices.View,
                Length = countX,
                Stride = 1,
                LineStride = countX,
            });
            kernel(countX, new DistanceTransformArgs
            {
                Input = b.View,
                Output = a.View,
                Vertices = vertices.View,
                Length = countY,
                Stride = countX,
                LineStride = 1,
            });
            compute.Download(a.View, result);
        }

        return result;
    }
}
