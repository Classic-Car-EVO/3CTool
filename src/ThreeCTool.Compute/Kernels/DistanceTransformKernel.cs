using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

internal struct DistanceTransformArgs
{
    /// <summary>Squared distances in cells; <see cref="DistanceTransformKernel.Infinite"/> marks no site.</summary>
    public ArrayView<int> Input;
    public ArrayView<int> Output;

    /// <summary>Lower-envelope parabola vertices, <see cref="Length"/> per line.</summary>
    public ArrayView<int> Vertices;
    public int Length;
    public int Stride;
    public int LineStride;
}

/// <summary>
/// Exact squared Euclidean distance transform, one 1D pass per line (Felzenszwalb &amp; Huttenlocher 2012). Parabola
/// intersections are compared as exact rationals in 64-bit integers, so results are bit-identical on every backend.
/// </summary>
internal static class DistanceTransformKernel
{
    public const int Infinite = int.MaxValue;

    public static void Run(Index1D line, DistanceTransformArgs args)
    {
        var start = line * args.LineStride;
        var stride = args.Stride;
        var vertexStart = line * args.Length;
        var input = args.Input;
        var v = args.Vertices;

        // Lower envelope of the parabolas (x - q)² + f(q) over the finite sites.
        var k = -1;
        for (var q = 0; q < args.Length; q++)
        {
            var fq = input[start + (q * stride)];
            if (fq != Infinite)
            {
                while (k >= 1 && !Before(
                    v[vertexStart + k - 1], input[start + (v[vertexStart + k - 1] * stride)],
                    v[vertexStart + k], input[start + (v[vertexStart + k] * stride)],
                    q, fq))
                {
                    k--;
                }

                k++;
                v[vertexStart + k] = q;
            }
        }

        var last = k;
        k = 0;
        for (var q = 0; q < args.Length; q++)
        {
            var result = Infinite;
            if (last >= 0)
            {
                // Advance while the next parabola's boundary lies strictly left of q.
                while (k < last && Intersection(v[vertexStart + k], input[start + (v[vertexStart + k] * stride)], v[vertexStart + k + 1], input[start + (v[vertexStart + k + 1] * stride)], out var denominator) < (long)q * denominator)
                {
                    k++;
                }

                var p = v[vertexStart + k];
                result = ((q - p) * (q - p)) + input[start + (p * stride)];
            }

            args.Output[start + (q * stride)] = result;
        }
    }

    /// <summary>Numerator of the x where parabolas at p &lt; q meet; the value is numerator / denominator.</summary>
    private static long Intersection(int p, int fp, int q, int fq, out long denominator)
    {
        denominator = 2L * (q - p);
        return ((long)fq + ((long)q * q)) - ((long)fp + ((long)p * p));
    }

    /// <summary>True when s(a, b) &lt; s(b, c): parabola b is still part of the envelope after adding c.</summary>
    private static bool Before(int a, int fa, int b, int fb, int c, int fc)
    {
        var left = Intersection(a, fa, b, fb, out var leftDenominator);
        var right = Intersection(b, fb, c, fc, out var rightDenominator);
        return left * rightDenominator < right * leftDenominator;
    }
}
