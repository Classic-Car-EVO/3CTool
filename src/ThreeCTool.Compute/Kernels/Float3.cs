using System.Numerics;
using System.Runtime.CompilerServices;

namespace ThreeCTool.Compute.Kernels;

/// <summary>Minimal float vector usable inside ILGPU kernels (System.Numerics types are not used on the device).</summary>
internal struct Float3
{
    public float X;
    public float Y;
    public float Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Float3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static Float3 operator +(Float3 a, Float3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Float3 operator -(Float3 a, Float3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Float3 operator -(Float3 a) => new(-a.X, -a.Y, -a.Z);

    public static Float3 operator *(Float3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Float3 operator *(float s, Float3 a) => new(a.X * s, a.Y * s, a.Z * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Float3 a, Float3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Float3 Cross(Float3 a, Float3 b) =>
        new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Length(Float3 a) => MathF.Sqrt(Dot(a, a));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Float3 Normalize(Float3 a)
    {
        var length = Length(a);
        return length > 0 ? a * (1f / length) : a;
    }

    public static Float3 Min(Float3 a, Float3 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

    public static Float3 Max(Float3 a, Float3 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));

    public static Float3 Abs(Float3 a) => new(MathF.Abs(a.X), MathF.Abs(a.Y), MathF.Abs(a.Z));

    /// <summary>Two unit vectors that, with <paramref name="w"/>, form a right-handed orthonormal frame (u × v = w).</summary>
    public static void Basis(Float3 w, out Float3 u, out Float3 v)
    {
        var helper = MathF.Abs(w.X) < 0.9f ? new Float3(1, 0, 0) : new Float3(0, 1, 0);
        u = Normalize(Cross(helper, w));
        v = Cross(w, u);
    }

    public static Float3 From(Vector3 v) => new(v.X, v.Y, v.Z);

    public readonly Vector3 ToVector3() => new(X, Y, Z);
}
