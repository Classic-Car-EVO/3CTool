using System.Numerics;

using ThreeCTool.Geometry;

namespace ThreeCTool.Compute;

/// <summary>
/// Distance to a mesh sampled on a regular 3D lattice (e.g. conformal FDM layers, offset surfaces). Node (i, j, k)
/// sits at Origin + (i, j, k)·Resolution; values are clamped to ±MaxDistance.
/// </summary>
public sealed class DistanceField3D
{
    private readonly float[] _values;

    private DistanceField3D(Vector3 origin, float resolution, int countX, int countY, int countZ, float maxDistance, bool withSign, float[] values)
    {
        Origin = origin;
        Resolution = resolution;
        CountX = countX;
        CountY = countY;
        CountZ = countZ;
        MaxDistance = maxDistance;
        IsSigned = withSign;
        _values = values;
    }

    public Vector3 Origin { get; }

    public float Resolution { get; }

    public int CountX { get; }

    public int CountY { get; }

    public int CountZ { get; }

    public float MaxDistance { get; }

    /// <summary>Negative inside (requires a closed mesh).</summary>
    public bool IsSigned { get; }

    /// <summary>Values indexed (k·CountY + j)·CountX + i.</summary>
    public IReadOnlyList<float> Values => _values;

    public float this[int i, int j, int k] => _values[(((k * CountY) + j) * CountX) + i];

    /// <summary>
    /// Samples the mesh distance on lattice nodes covering <paramref name="bounds"/> (the upper side rounds up to whole
    /// cells). The nearest-triangle search is limited to <paramref name="maxDistance"/>.
    /// </summary>
    public static DistanceField3D Build(GpuMesh mesh, BoundingBox bounds, float resolution, float maxDistance, bool withSign)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDistance);
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("Bounds must not be empty.", nameof(bounds));
        }

        var counts = (bounds.Size / resolution) + Vector3.One;
        var countX = (int)MathF.Ceiling(counts.X - 1e-4f);
        var countY = (int)MathF.Ceiling(counts.Y - 1e-4f);
        var countZ = (int)MathF.Ceiling(counts.Z - 1e-4f);
        if ((long)countX * countY * countZ > Array.MaxLength)
        {
            throw new ArgumentException("Too many lattice nodes; increase the resolution.", nameof(resolution));
        }

        var points = new Vector3[countX * countY * countZ];
        for (var k = 0; k < countZ; k++)
        {
            for (var j = 0; j < countY; j++)
            {
                for (var i = 0; i < countX; i++)
                {
                    points[(((k * countY) + j) * countX) + i] = bounds.Min + (new Vector3(i, j, k) * resolution);
                }
            }
        }

        var closest = withSign ? MeshQueries.SignedDistance(mesh, points, maxDistance) : MeshQueries.Closest(mesh, points, maxDistance);
        var values = new float[points.Length];
        for (var n = 0; n < values.Length; n++)
        {
            values[n] = Math.Clamp(closest[n].Distance, -maxDistance, maxDistance);
        }

        return new(bounds.Min, resolution, countX, countY, countZ, maxDistance, withSign, values);
    }

    /// <summary>Trilinear interpolation, clamped to the lattice.</summary>
    public float Sample(Vector3 point)
    {
        var g = (point - Origin) / Resolution;
        Cell(g.X, CountX, out var i, out var fx);
        Cell(g.Y, CountY, out var j, out var fy);
        Cell(g.Z, CountZ, out var k, out var fz);
        var i1 = Math.Min(i + 1, CountX - 1);
        var j1 = Math.Min(j + 1, CountY - 1);
        var k1 = Math.Min(k + 1, CountZ - 1);
        var c00 = Lerp(this[i, j, k], this[i1, j, k], fx);
        var c10 = Lerp(this[i, j1, k], this[i1, j1, k], fx);
        var c01 = Lerp(this[i, j, k1], this[i1, j, k1], fx);
        var c11 = Lerp(this[i, j1, k1], this[i1, j1, k1], fx);
        return Lerp(Lerp(c00, c10, fy), Lerp(c01, c11, fy), fz);
    }

    /// <summary>Gradient of the interpolated field by central differences of one cell.</summary>
    public Vector3 Gradient(Vector3 point)
    {
        var h = Resolution;
        return new Vector3(
            Sample(point + new Vector3(h, 0, 0)) - Sample(point - new Vector3(h, 0, 0)),
            Sample(point + new Vector3(0, h, 0)) - Sample(point - new Vector3(0, h, 0)),
            Sample(point + new Vector3(0, 0, h)) - Sample(point - new Vector3(0, 0, h))) / (2 * h);
    }

    private static void Cell(float g, int count, out int index, out float fraction)
    {
        var clamped = Math.Clamp(g, 0, count - 1);
        index = Math.Min((int)clamped, Math.Max(count - 2, 0));
        fraction = clamped - index;
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
