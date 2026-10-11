using System.Numerics;

namespace ThreeCTool.Geometry;

/// <summary>Axis-aligned bounding box. <see cref="Empty"/> has Min = +inf, Max = -inf.</summary>
public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    public static BoundingBox Empty { get; } = new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public Vector3 Center => (Min + Max) * 0.5f;

    public float Diagonal => Size.Length();

    public static BoundingBox FromPoints(ReadOnlySpan<Vector3> points)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new BoundingBox(min, max);
    }

    public BoundingBox Include(Vector3 point) => new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    public BoundingBox Union(BoundingBox other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.Y >= Min.Y && point.Z >= Min.Z &&
        point.X <= Max.X && point.Y <= Max.Y && point.Z <= Max.Z;

    public bool Intersects(BoundingBox other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    /// <summary>Bounds of the eight transformed corners.</summary>
    public BoundingBox Transform(Matrix4x4 matrix)
    {
        if (IsEmpty)
        {
            return this;
        }

        Span<Vector3> corners =
        [
            new(Min.X, Min.Y, Min.Z), new(Max.X, Min.Y, Min.Z), new(Min.X, Max.Y, Min.Z), new(Max.X, Max.Y, Min.Z),
            new(Min.X, Min.Y, Max.Z), new(Max.X, Min.Y, Max.Z), new(Min.X, Max.Y, Max.Z), new(Max.X, Max.Y, Max.Z),
        ];
        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = Vector3.Transform(corners[i], matrix);
        }

        return FromPoints(corners);
    }
}
