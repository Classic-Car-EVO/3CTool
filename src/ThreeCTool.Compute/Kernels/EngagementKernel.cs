using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

/// <summary>Device view of a material raster: 1 = material, 0 = empty; cell (ix, iy) at index iy·CountX + ix.</summary>
internal struct RasterView
{
    public ArrayView<int> Cells;
    public int CountX;
    public int CountY;
    public float OriginX;
    public float OriginY;
    public float Resolution;

    public readonly int At(float x, float y)
    {
        var ix = (int)MathF.Floor((x - OriginX) / Resolution);
        var iy = (int)MathF.Floor((y - OriginY) / Resolution);
        return ix >= 0 && iy >= 0 && ix < CountX && iy < CountY ? Cells[(iy * CountX) + ix] : 0;
    }
}

internal struct EngagementArgs
{
    public RasterView Raster;
    public ArrayView<float> Centers;
    public float SampleRadius;
    public int Samples;
    public ArrayView<float> Result;
}

internal struct RasterRemoveArgs
{
    public RasterView Raster;

    /// <summary>Per segment: x0, y0, x1, y1.</summary>
    public ArrayView<float> Segments;

    /// <summary>Per segment: first cell box ix0, iy0, width; and the running entry offset (length segments + 1).</summary>
    public ArrayView<int> Boxes;
    public ArrayView<long> Offsets;
    public int SegmentCount;
    public long BaseEntry;
    public float Radius;
    public ArrayView<int> Removed;
}

internal static class EngagementKernel
{
    /// <summary>Engaged angle: share of samples on the circle just outside the tool that hit material.</summary>
    public static void Engagement(Index1D index, EngagementArgs args)
    {
        var cx = args.Centers[2 * index];
        var cy = args.Centers[(2 * index) + 1];
        var step = 2 * MathF.PI / args.Samples;
        var hits = 0;
        for (var k = 0; k < args.Samples; k++)
        {
            var angle = k * step;
            hits += args.Raster.At(cx + (args.SampleRadius * MathF.Cos(angle)), cy + (args.SampleRadius * MathF.Sin(angle)));
        }

        args.Result[index] = hits * step;
    }

    /// <summary>One thread per (segment, cell in the segment's box): clears cells whose centre is within the radius.</summary>
    public static void Remove(Index1D index, RasterRemoveArgs args)
    {
        var entry = args.BaseEntry + index;
        var lo = 0;
        var hi = args.SegmentCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (args.Offsets[mid] <= entry)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var local = (int)(entry - args.Offsets[lo]);
        var width = args.Boxes[(3 * lo) + 2];
        var raster = args.Raster;
        var ix = args.Boxes[3 * lo] + (local % width);
        var iy = args.Boxes[(3 * lo) + 1] + (local / width);
        var px = raster.OriginX + ((ix + 0.5f) * raster.Resolution);
        var py = raster.OriginY + ((iy + 0.5f) * raster.Resolution);
        var x0 = args.Segments[4 * lo];
        var y0 = args.Segments[(4 * lo) + 1];
        var ex = args.Segments[(4 * lo) + 2] - x0;
        var ey = args.Segments[(4 * lo) + 3] - y0;
        var length2 = (ex * ex) + (ey * ey);
        var t = length2 > 0 ? MathF.Min(MathF.Max((((px - x0) * ex) + ((py - y0) * ey)) / length2, 0), 1) : 0;
        var dx = px - (x0 + (t * ex));
        var dy = py - (y0 + (t * ey));
        if ((dx * dx) + (dy * dy) <= args.Radius * args.Radius
            && Atomic.Exchange(ref raster.Cells[(iy * raster.CountX) + ix], 0) != 0)
        {
            Atomic.Add(ref args.Removed[0], 1);
        }
    }
}
