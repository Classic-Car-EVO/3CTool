using System.Numerics;

namespace ThreeCTool.Compute;

/// <summary>A point projected onto a surface; <see cref="Triangle"/> is -1 when the projection missed.</summary>
public readonly record struct SurfacePoint(Vector3 Point, Vector3 Normal, int Triangle)
{
    public bool IsHit => Triangle >= 0;
}

/// <summary>Vertical projection of XY points onto the topmost mesh surface or its outward offset (conformal FDM paths).</summary>
public static class SurfaceProjection
{
    /// <summary>Topmost surface point below each XY point, with the triangle's normal.</summary>
    public static SurfacePoint[] Project(GpuMesh mesh, ReadOnlySpan<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var top = mesh.Bounds.Max.Z + 1;
        var origins = new Vector3[points.Length];
        var directions = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            origins[i] = new Vector3(points[i], top);
            directions[i] = -Vector3.UnitZ;
        }

        var hits = MeshQueries.Raycast(mesh, origins, directions);
        var result = new SurfacePoint[points.Length];
        for (var i = 0; i < hits.Length; i++)
        {
            result[i] = hits[i].IsHit
                ? new SurfacePoint(hits[i].Point, hits[i].Normal, hits[i].Triangle)
                : new SurfacePoint(new Vector3(points[i], float.NaN), Vector3.Zero, -1);
        }

        return result;
    }

    /// <summary>
    /// Topmost point of the surface offset by <paramref name="distance"/> (the envelope of spheres on the mesh) above
    /// each XY point, i.e. the centre of a ball of that radius dropped onto the mesh; the normal points from the
    /// touched mesh point to the ball centre.
    /// </summary>
    public static SurfacePoint[] ProjectOffset(GpuMesh mesh, ReadOnlySpan<Vector2> points, float distance)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(distance);
        var origins = new Vector3[points.Length];
        var axes = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            origins[i] = new Vector3(points[i], 0);
            axes[i] = Vector3.UnitZ;
        }

        var tips = DropCutter.Drop(mesh, CutterProfile.Ball(2 * distance), origins, axes);
        var centers = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            centers[i] = new Vector3(points[i], tips[i] + distance);
        }

        var closest = MeshQueries.Closest(mesh, centers, 1.01f * distance);
        var result = new SurfacePoint[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            result[i] = float.IsFinite(tips[i]) && closest[i].Found
                ? new SurfacePoint(centers[i], Vector3.Normalize(centers[i] - closest[i].Point), closest[i].Triangle)
                : new SurfacePoint(new Vector3(points[i], float.NaN), Vector3.Zero, -1);
        }

        return result;
    }
}
