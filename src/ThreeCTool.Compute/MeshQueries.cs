using System.Numerics;

using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;

namespace ThreeCTool.Compute;

/// <summary>Closest ray hit. <see cref="Triangle"/> is −1 on a miss (other fields are then meaningless).</summary>
public readonly record struct RayHit(float Distance, int Triangle, Vector3 Point, Vector3 Normal, Vector2 Barycentric)
{
    public bool IsHit => Triangle >= 0;
}

/// <summary>
/// Closest mesh point. <see cref="Triangle"/> is −1 when nothing lies within the search distance. Distance is
/// signed (negative inside) only for <see cref="MeshQueries.SignedDistance"/>.
/// </summary>
public readonly record struct ClosestPoint(float Distance, int Triangle, Vector3 Point)
{
    public bool Found => Triangle >= 0;
}

/// <summary>Batched mesh queries: raycasts, closest points and signed distance.</summary>
public static class MeshQueries
{
    /// <summary>Closest two-sided hit per ray within <paramref name="maxDistance"/>; directions are normalized here.</summary>
    public static RayHit[] Raycast(GpuMesh mesh, ReadOnlySpan<Vector3> origins, ReadOnlySpan<Vector3> directions, float maxDistance = float.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (origins.Length != directions.Length)
        {
            throw new ArgumentException("Origins and directions must have the same length.", nameof(directions));
        }

        var originData = new Float3[origins.Length];
        var directionData = new Float3[origins.Length];
        for (var i = 0; i < origins.Length; i++)
        {
            originData[i] = Float3.From(origins[i]);
            directionData[i] = Float3.From(Vector3.Normalize(directions[i]));
        }

        var hits = new Float3[origins.Length];
        var triangles = new int[origins.Length];
        var compute = mesh.Compute;
        if (origins.Length > 0)
        {
            var batch = Math.Min(origins.Length, compute.BatchSize(40));
            var accelerator = compute.Accelerator;
            lock (compute.Gate)
            {
                using var originBuffer = accelerator.Allocate1D<Float3>(batch);
                using var directionBuffer = accelerator.Allocate1D<Float3>(batch);
                using var hitBuffer = accelerator.Allocate1D<Float3>(batch);
                using var triangleBuffer = accelerator.Allocate1D<int>(batch);
                var kernel = compute.Kernel<RaycastArgs>(MeshQueryKernels.Raycast);
                for (var start = 0; start < origins.Length; start += batch)
                {
                    var count = Math.Min(batch, origins.Length - start);
                    compute.Upload(originBuffer.View, originData.AsSpan(start, count));
                    compute.Upload(directionBuffer.View, directionData.AsSpan(start, count));
                    kernel(count, new RaycastArgs
                    {
                        Mesh = mesh.View,
                        Origins = originBuffer.View,
                        Directions = directionBuffer.View,
                        MaxDistance = MathF.Min(maxDistance, 1e30f),
                        Hits = hitBuffer.View,
                        Triangles = triangleBuffer.View,
                    });
                    compute.Download(hitBuffer.View, hits.AsSpan(start, count));
                    compute.Download(triangleBuffer.View, triangles.AsSpan(start, count));
                }
            }
        }

        var result = new RayHit[origins.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var triangle = triangles[i];
            if (triangle < 0)
            {
                result[i] = new RayHit(float.PositiveInfinity, -1, default, default, default);
                continue;
            }

            var (a, b, c) = mesh.Mesh.GetTriangle(triangle);
            var u = hits[i].Y;
            var v = hits[i].Z;
            var point = a + ((b - a) * u) + ((c - a) * v);
            result[i] = new RayHit(hits[i].X, triangle, point, mesh.Mesh.FaceNormal(triangle), new Vector2(u, v));
        }

        return result;
    }

    /// <summary>Closest point on the mesh to each query point, searching up to <paramref name="maxDistance"/>.</summary>
    public static ClosestPoint[] Closest(GpuMesh mesh, ReadOnlySpan<Vector3> points, float maxDistance = float.MaxValue) =>
        RunClosest(mesh, points, maxDistance, signed: false);

    /// <summary>
    /// Signed distance (negative inside) for a closed mesh; inside/outside by majority parity of three fixed rays.
    /// Unsigned search is limited to <paramref name="maxDistance"/>, beyond which ±maxDistance is reported.
    /// </summary>
    public static ClosestPoint[] SignedDistance(GpuMesh mesh, ReadOnlySpan<Vector3> points, float maxDistance = float.MaxValue) =>
        RunClosest(mesh, points, maxDistance, signed: true);

    private static ClosestPoint[] RunClosest(GpuMesh mesh, ReadOnlySpan<Vector3> points, float maxDistance, bool signed)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDistance);
        var pointData = new Float3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            pointData[i] = Float3.From(points[i]);
        }

        var closest = new Float3[points.Length];
        var distances = new float[points.Length];
        var triangles = new int[points.Length];
        var compute = mesh.Compute;
        if (points.Length > 0)
        {
            var batch = Math.Min(points.Length, compute.BatchSize(32));
            var accelerator = compute.Accelerator;
            lock (compute.Gate)
            {
                using var pointBuffer = accelerator.Allocate1D<Float3>(batch);
                using var closestBuffer = accelerator.Allocate1D<Float3>(batch);
                using var distanceBuffer = accelerator.Allocate1D<float>(batch);
                using var triangleBuffer = accelerator.Allocate1D<int>(batch);
                var kernel = compute.Kernel<ClosestPointArgs>(MeshQueryKernels.ClosestPoint);
                for (var start = 0; start < points.Length; start += batch)
                {
                    var count = Math.Min(batch, points.Length - start);
                    compute.Upload(pointBuffer.View, pointData.AsSpan(start, count));
                    kernel(count, new ClosestPointArgs
                    {
                        Mesh = mesh.View,
                        Points = pointBuffer.View,
                        MaxDistance = MathF.Min(maxDistance, 1e18f),
                        Closest = closestBuffer.View,
                        Distances = distanceBuffer.View,
                        Triangles = triangleBuffer.View,
                        Signed = signed ? 1 : 0,
                    });
                    compute.Download(closestBuffer.View, closest.AsSpan(start, count));
                    compute.Download(distanceBuffer.View, distances.AsSpan(start, count));
                    compute.Download(triangleBuffer.View, triangles.AsSpan(start, count));
                }
            }
        }

        var result = new ClosestPoint[points.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new ClosestPoint(distances[i], triangles[i], closest[i].ToVector3());
        }

        return result;
    }
}
