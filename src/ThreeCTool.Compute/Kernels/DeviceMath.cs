using System.Runtime.CompilerServices;

namespace ThreeCTool.Compute.Kernels;

/// <summary>Scalar function of one variable, implemented by structs so ILGPU resolves calls statically.</summary>
internal interface IScalarFunction
{
    float Evaluate(float s);
}

internal static class DeviceMath
{
    public const float NoContact = -1e30f;

    /// <summary>Golden-section iterations: shrinks the bracket by 0.618^36 ≈ 3e-8.</summary>
    public const int GoldenIterations = 36;

    public const int BisectIterations = 40;

    private const float InvPhi = 0.618034f;

    /// <summary>Maximum of a unimodal (e.g. concave) function on [a, b], endpoints included.</summary>
    public static float GoldenMax<TF>(TF f, float a, float b, out float arg)
        where TF : struct, IScalarFunction =>
        GoldenMax(f, a, b, GoldenIterations, out arg);

    public static float GoldenMax<TF>(TF f, float a, float b, int iterations, out float arg)
        where TF : struct, IScalarFunction
    {
        var fa = f.Evaluate(a);
        var fb = f.Evaluate(b);
        var best = fa;
        arg = a;
        if (fb > best)
        {
            best = fb;
            arg = b;
        }

        var x1 = b - (InvPhi * (b - a));
        var x2 = a + (InvPhi * (b - a));
        var f1 = f.Evaluate(x1);
        var f2 = f.Evaluate(x2);
        for (var i = 0; i < iterations; i++)
        {
            if (f1 < f2)
            {
                a = x1;
                x1 = x2;
                f1 = f2;
                x2 = a + (InvPhi * (b - a));
                f2 = f.Evaluate(x2);
            }
            else
            {
                b = x2;
                x2 = x1;
                f2 = f1;
                x1 = b - (InvPhi * (b - a));
                f1 = f.Evaluate(x1);
            }
        }

        if (f1 > best)
        {
            best = f1;
            arg = x1;
        }

        if (f2 > best)
        {
            best = f2;
            arg = x2;
        }

        return best;
    }

    /// <summary>Point in [a, b] where f crosses zero, given f(a) and f(b) of opposite sign (or zero).</summary>
    public static float Bisect<TF>(TF f, float a, float b)
        where TF : struct, IScalarFunction =>
        Bisect(f, a, b, BisectIterations);

    public static float Bisect<TF>(TF f, float a, float b, int iterations)
        where TF : struct, IScalarFunction
    {
        var positiveAtA = f.Evaluate(a) >= 0;
        for (var i = 0; i < iterations; i++)
        {
            var m = 0.5f * (a + b);
            if ((f.Evaluate(m) >= 0) == positiveAtA)
            {
                a = m;
            }
            else
            {
                b = m;
            }
        }

        return 0.5f * (a + b);
    }

    /// <summary>True if the infinite line o + t·d passes through the box.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool LineHitsBox(Float3 o, Float3 d, Float3 min, Float3 max)
    {
        var enter = -1e30f;
        var exit = 1e30f;
        return Slab(o.X, d.X, min.X, max.X, ref enter, ref exit)
            && Slab(o.Y, d.Y, min.Y, max.Y, ref enter, ref exit)
            && Slab(o.Z, d.Z, min.Z, max.Z, ref enter, ref exit);
    }

    /// <summary>Clips [enter, exit] (parameters along o + t·d) to one axis slab; false when empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Slab(float o, float d, float min, float max, ref float enter, ref float exit)
    {
        if (MathF.Abs(d) < 1e-12f)
        {
            return o >= min && o <= max;
        }

        var inv = 1f / d;
        var t1 = (min - o) * inv;
        var t2 = (max - o) * inv;
        enter = MathF.Max(enter, MathF.Min(t1, t2));
        exit = MathF.Min(exit, MathF.Max(t1, t2));
        return enter <= exit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool BoxesOverlap(Float3 aMin, Float3 aMax, Float3 bMin, Float3 bMax) =>
        aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

    /// <summary>Squared distance from a point to a box (0 inside).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float BoxDistanceSquared(Float3 p, Float3 min, Float3 max)
    {
        var dx = MathF.Max(MathF.Max(min.X - p.X, 0), p.X - max.X);
        var dy = MathF.Max(MathF.Max(min.Y - p.Y, 0), p.Y - max.Y);
        var dz = MathF.Max(MathF.Max(min.Z - p.Z, 0), p.Z - max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>Barycentric inside test for a 2D triangle (inclusive, either winding).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool PointInTriangle2D(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        var d1 = ((px - bx) * (ay - by)) - ((ax - bx) * (py - by));
        var d2 = ((px - cx) * (by - cy)) - ((bx - cx) * (py - cy));
        var d3 = ((px - ax) * (cy - ay)) - ((cx - ax) * (py - ay));
        var hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPositive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNegative && hasPositive);
    }

    /// <summary>Range of s in [0, 1] where |q + s·e| ≤ radius (2D). False when empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DiscRange(float qx, float qy, float ex, float ey, float radius, out float s0, out float s1)
    {
        s0 = 0;
        s1 = 1;
        var a = (ex * ex) + (ey * ey);
        var b = (qx * ex) + (qy * ey);
        var c = (qx * qx) + (qy * qy) - (radius * radius);
        if (a < 1e-20f)
        {
            return c <= 0;
        }

        var discriminant = (b * b) - (a * c);
        if (discriminant < 0)
        {
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        s0 = MathF.Max((-b - root) / a, 0);
        s1 = MathF.Min((-b + root) / a, 1);
        return s0 <= s1;
    }
}
