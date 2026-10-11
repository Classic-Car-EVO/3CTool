using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

internal struct HeightfieldOffsetArgs
{
    /// <summary>Row-major heights; <see cref="DeviceMath.NoContact"/> where there is no surface.</summary>
    public ArrayView<float> Heights;
    public ArrayView<float> Result;
    public int CountX;
    public int CountY;
    public float Resolution;
    public int Reach;
    public CutterProfile Cutter;
}

internal static class HeightfieldKernel
{
    /// <summary>Lowest tip height over a cell such that no sample within the cutter radius is above the cutter.</summary>
    public static void Offset(Index1D index, HeightfieldOffsetArgs args)
    {
        int cell = index;
        var iy = cell / args.CountX;
        var ix = cell - (iy * args.CountX);
        var cutter = args.Cutter;
        var radius2 = cutter.Radius * cutter.Radius;
        var x0 = ix - args.Reach < 0 ? 0 : ix - args.Reach;
        var x1 = ix + args.Reach >= args.CountX ? args.CountX - 1 : ix + args.Reach;
        var y0 = iy - args.Reach < 0 ? 0 : iy - args.Reach;
        var y1 = iy + args.Reach >= args.CountY ? args.CountY - 1 : iy + args.Reach;
        var best = DeviceMath.NoContact;
        for (var qy = y0; qy <= y1; qy++)
        {
            var dy = (qy - iy) * args.Resolution;
            for (var qx = x0; qx <= x1; qx++)
            {
                var dx = (qx - ix) * args.Resolution;
                var d2 = (dx * dx) + (dy * dy);
                var h = args.Heights[(qy * args.CountX) + qx];
                if (d2 <= radius2 && h > DeviceMath.NoContact)
                {
                    best = MathF.Max(best, h - cutter.HeightAt(MathF.Sqrt(d2)));
                }
            }
        }

        args.Result[index] = best;
    }
}
