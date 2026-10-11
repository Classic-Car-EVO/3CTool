using System.Numerics;

using ILGPU;
using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;

namespace ThreeCTool.Compute;

/// <summary>
/// 2D material raster for one Z level of adaptive roughing: evaluates cutter engagement angles and removes the
/// material swept by tool paths. Cell (ix, iy) covers [origin + i·resolution, origin + (i + 1)·resolution).
/// </summary>
public sealed class EngagementMap : IDisposable
{
    private const int MaxEntriesPerLaunch = 1 << 26;

    private readonly MemoryBuffer1D<int, Stride1D.Dense> _cells;

    private EngagementMap(ComputeContext compute, Vector2 origin, float resolution, int countX, int countY, ReadOnlySpan<bool> material)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countY);
        if (material.Length != countX * countY)
        {
            throw new ArgumentException("Material mask size does not match the raster.", nameof(material));
        }

        Compute = compute;
        Origin = origin;
        Resolution = resolution;
        CountX = countX;
        CountY = countY;
        var cells = new int[material.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = material[i] ? 1 : 0;
        }

        _cells = compute.Accelerator.Allocate1D(cells);
    }

    public ComputeContext Compute { get; }

    public Vector2 Origin { get; }

    public float Resolution { get; }

    public int CountX { get; }

    public int CountY { get; }

    internal RasterView View => new()
    {
        Cells = _cells.View,
        CountX = CountX,
        CountY = CountY,
        OriginX = Origin.X,
        OriginY = Origin.Y,
        Resolution = Resolution,
    };

    /// <summary>Raster from a row-major material mask (index iy·countX + ix).</summary>
    public static EngagementMap FromMask(ComputeContext compute, Vector2 origin, float resolution, int countX, int countY, ReadOnlySpan<bool> material) =>
        new(compute, origin, resolution, countX, countY, material);

    /// <summary>Raster where <paramref name="isMaterial"/>(x, y) holds at the cell centres.</summary>
    public static EngagementMap FromFunction(ComputeContext compute, Vector2 origin, float resolution, int countX, int countY, Func<float, float, bool> isMaterial)
    {
        ArgumentNullException.ThrowIfNull(isMaterial);
        var mask = new bool[countX * countY];
        for (var iy = 0; iy < countY; iy++)
        {
            for (var ix = 0; ix < countX; ix++)
            {
                mask[(iy * countX) + ix] = isMaterial(origin.X + ((ix + 0.5f) * resolution), origin.Y + ((iy + 0.5f) * resolution));
            }
        }

        return new(compute, origin, resolution, countX, countY, mask);
    }

    /// <summary>Row-major material mask.</summary>
    public bool[] Read()
    {
        var cells = new int[CountX * CountY];
        lock (Compute.Gate)
        {
            Compute.Download(_cells.View, cells);
        }

        return [.. cells.Select(c => c != 0)];
    }

    /// <summary>
    /// Engagement angle (radians, 0…2π) of a cutter of <paramref name="radius"/> at each centre: the angular share
    /// of the circle half a cell outside the cutter that lies in material.
    /// </summary>
    public float[] Engagement(ReadOnlySpan<Vector2> centers, float radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        var result = new float[centers.Length];
        if (centers.Length == 0)
        {
            return result;
        }

        var sampleRadius = radius + (0.5f * Resolution);
        var samples = Math.Clamp((int)MathF.Ceiling(4 * MathF.PI * sampleRadius / Resolution), 64, 8192);
        var data = new float[2 * centers.Length];
        for (var i = 0; i < centers.Length; i++)
        {
            data[2 * i] = centers[i].X;
            data[(2 * i) + 1] = centers[i].Y;
        }

        lock (Compute.Gate)
        {
            using var centerBuffer = Compute.Accelerator.Allocate1D(data);
            using var resultBuffer = Compute.Accelerator.Allocate1D<float>(centers.Length);
            Compute.Kernel<EngagementArgs>(EngagementKernel.Engagement)(centers.Length, new EngagementArgs
            {
                Raster = View,
                Centers = centerBuffer.View,
                SampleRadius = sampleRadius,
                Samples = samples,
                Result = resultBuffer.View,
            });
            Compute.Download(resultBuffer.View, result);
        }

        return result;
    }

    /// <summary>Clears the cells swept by a cutter of <paramref name="radius"/> along the polyline; returns the removed area (mm²).</summary>
    public double Remove(ReadOnlySpan<Vector2> path, float radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        if (path.Length == 0)
        {
            return 0;
        }

        var segments = new List<float>();
        var boxes = new List<int>();
        var offsets = new List<long>();
        long entries = 0;
        for (var i = 0; i < Math.Max(path.Length - 1, 1); i++)
        {
            var a = path[i];
            var b = path[Math.Min(i + 1, path.Length - 1)];
            var ix0 = Math.Max((int)MathF.Floor((MathF.Min(a.X, b.X) - radius - Origin.X) / Resolution), 0);
            var iy0 = Math.Max((int)MathF.Floor((MathF.Min(a.Y, b.Y) - radius - Origin.Y) / Resolution), 0);
            var ix1 = Math.Min((int)MathF.Floor((MathF.Max(a.X, b.X) + radius - Origin.X) / Resolution), CountX - 1);
            var iy1 = Math.Min((int)MathF.Floor((MathF.Max(a.Y, b.Y) + radius - Origin.Y) / Resolution), CountY - 1);
            if (ix0 <= ix1 && iy0 <= iy1)
            {
                segments.AddRange([a.X, a.Y, b.X, b.Y]);
                boxes.AddRange([ix0, iy0, ix1 - ix0 + 1]);
                offsets.Add(entries);
                entries += (long)(ix1 - ix0 + 1) * (iy1 - iy0 + 1);
            }
        }

        if (entries == 0)
        {
            return 0;
        }

        var removed = new int[1];
        lock (Compute.Gate)
        {
            var accelerator = Compute.Accelerator;
            using var segmentBuffer = accelerator.Allocate1D(segments.ToArray());
            using var boxBuffer = accelerator.Allocate1D(boxes.ToArray());
            using var offsetBuffer = accelerator.Allocate1D(offsets.ToArray());
            using var removedBuffer = accelerator.Allocate1D<int>(1);
            removedBuffer.MemSetToZero();
            var kernel = Compute.Kernel<RasterRemoveArgs>(EngagementKernel.Remove);
            for (long start = 0; start < entries; start += MaxEntriesPerLaunch)
            {
                kernel((int)Math.Min(MaxEntriesPerLaunch, entries - start), new RasterRemoveArgs
                {
                    Raster = View,
                    Segments = segmentBuffer.View,
                    Boxes = boxBuffer.View,
                    Offsets = offsetBuffer.View,
                    SegmentCount = offsets.Count,
                    BaseEntry = start,
                    Radius = radius,
                    Removed = removedBuffer.View,
                });
            }

            Compute.Download(removedBuffer.View, removed);
        }

        return removed[0] * (double)Resolution * Resolution;
    }

    public void Dispose() => _cells.Dispose();
}
