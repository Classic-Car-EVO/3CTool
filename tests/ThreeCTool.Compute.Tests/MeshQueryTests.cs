using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute.Tests;

public class MeshQueryTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    private readonly ComputeContext _compute = fixture.Compute;

    [Fact]
    public void RaycastHitsBoxFaces()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10)));
        var hits = MeshQueries.Raycast(
            mesh,
            [new Vector3(5, 5, 20), new Vector3(-5, 3, 4), new Vector3(5, 5, 5), new Vector3(20, 20, 20), new Vector3(5, 5, 20)],
            [-Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ],
            maxDistance: 1000);

        Assert.Equal(10, hits[0].Distance, 4);
        AssertNear(new Vector3(5, 5, 10), hits[0].Point);
        AssertNear(Vector3.UnitZ, hits[0].Normal);

        Assert.Equal(5, hits[1].Distance, 4);
        AssertNear(-Vector3.UnitX, hits[1].Normal);

        Assert.Equal(5, hits[2].Distance, 4); // from inside: two-sided
        Assert.False(hits[3].IsHit);
        Assert.Equal(hits[0], hits[4]);
    }

    [Fact]
    public void RaycastRespectsMaxDistance()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10)));
        var hits = MeshQueries.Raycast(mesh, [new Vector3(5, 5, 20)], [-Vector3.UnitZ], maxDistance: 9);
        Assert.False(hits[0].IsHit);
    }

    [Fact]
    public void RaycastMatchesBruteForce()
    {
        var source = TestMeshes.Sphere(10, 24, 48);
        using var mesh = new GpuMesh(_compute, source);
        var random = new Random(7);
        var origins = new Vector3[500];
        var directions = new Vector3[500];
        for (var i = 0; i < origins.Length; i++)
        {
            origins[i] = RandomPoint(random, 15);
            directions[i] = Vector3.Normalize(RandomPoint(random, 1));
        }

        var hits = MeshQueries.Raycast(mesh, origins, directions);
        for (var i = 0; i < origins.Length; i++)
        {
            var expected = float.PositiveInfinity;
            for (var t = 0; t < source.TriangleCount; t++)
            {
                var (a, b, c) = source.GetTriangle(t);
                if (RayTriangle(origins[i], directions[i], a, b, c, out var d) && d >= 0)
                {
                    expected = MathF.Min(expected, d);
                }
            }

            if (float.IsPositiveInfinity(expected))
            {
                Assert.False(hits[i].IsHit);
            }
            else
            {
                Assert.True(hits[i].IsHit);
                Assert.Equal(expected, hits[i].Distance, 3);
                AssertNear(origins[i] + (directions[i] * expected), hits[i].Point, 1e-3f);
            }
        }
    }

    [Fact]
    public void ClosestPointOnBox()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10)));
        var result = MeshQueries.Closest(mesh, [new Vector3(5, 5, 13), new Vector3(13, 14, 5), new Vector3(-1, -2, -2), new Vector3(5, 5, 4)]);
        Assert.Equal(3, result[0].Distance, 4);
        AssertNear(new Vector3(5, 5, 10), result[0].Point);
        Assert.Equal(5, result[1].Distance, 4);
        AssertNear(new Vector3(10, 10, 5), result[1].Point);
        Assert.Equal(3, result[2].Distance, 4);
        AssertNear(Vector3.Zero, result[2].Point);
        Assert.Equal(4, result[3].Distance, 4); // inside: unsigned
    }

    [Fact]
    public void ClosestRespectsMaxDistance()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10)));
        var result = MeshQueries.Closest(mesh, [new Vector3(5, 5, 30)], maxDistance: 5);
        Assert.False(result[0].Found);
        Assert.Equal(5, result[0].Distance);
    }

    [Fact]
    public void ClosestMatchesBruteForce()
    {
        var source = Mesh.Merge([TestMeshes.Sphere(10, 16, 32), TestMeshes.Mushroom()], "mix");
        using var mesh = new GpuMesh(_compute, source);
        var random = new Random(11);
        var points = new Vector3[400];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = RandomPoint(random, 40);
        }

        var result = MeshQueries.Closest(mesh, points);
        for (var i = 0; i < points.Length; i++)
        {
            var expected = float.PositiveInfinity;
            for (var t = 0; t < source.TriangleCount; t++)
            {
                var (a, b, c) = source.GetTriangle(t);
                expected = MathF.Min(expected, Vector3.Distance(points[i], BruteClosest(points[i], a, b, c)));
            }

            Assert.Equal(expected, result[i].Distance, 3);
            Assert.Equal(expected, Vector3.Distance(points[i], result[i].Point), 3);
        }
    }

    [Fact]
    public void SignedDistanceOfSphere()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Sphere(10, 48, 96));
        var random = new Random(3);
        var points = new Vector3[1000];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = RandomPoint(random, 20);
        }

        var result = MeshQueries.SignedDistance(mesh, points);
        for (var i = 0; i < points.Length; i++)
        {
            var expected = points[i].Length() - 10;
            Assert.True(MathF.Abs(expected - result[i].Distance) < 0.08f, $"{points[i]}: {result[i].Distance} vs {expected}");
        }
    }

    [Fact]
    public void SignedDistanceOnGridAlignedBox()
    {
        // Query points share coordinates with box edges/vertices, the worst case for parity rays.
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10)));
        var points = new List<Vector3>();
        for (var x = -2; x <= 12; x += 2)
        {
            for (var y = -2; y <= 12; y += 2)
            {
                for (var z = -2; z <= 12; z += 2)
                {
                    points.Add(new Vector3(x, y, z));
                }
            }
        }

        var result = MeshQueries.SignedDistance(mesh, points.ToArray());
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var inside = p.X is > 0 and < 10 && p.Y is > 0 and < 10 && p.Z is > 0 and < 10;
            var onSurface = MathF.Abs(result[i].Distance) < 1e-4f;
            Assert.True(onSurface || inside == result[i].Distance < 0, $"{p}: {result[i].Distance}");
        }
    }

    [Fact]
    public void EmptyInputs()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.UnitCube());
        Assert.Empty(MeshQueries.Raycast(mesh, [], []));
        Assert.Empty(MeshQueries.Closest(mesh, []));
    }

    internal static Vector3 RandomPoint(Random random, float extent) =>
        new(((random.NextSingle() * 2) - 1) * extent, ((random.NextSingle() * 2) - 1) * extent, ((random.NextSingle() * 2) - 1) * extent);

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        // Plane hit then inside test with double precision (independent of the kernel's Möller–Trumbore).
        t = 0;
        var n = Vector3.Cross(b - a, c - a);
        var denominator = (double)Vector3.Dot(n, d);
        if (Math.Abs(denominator) < 1e-12)
        {
            return false;
        }

        t = (float)(Vector3.Dot(n, a - o) / denominator);
        var p = o + (d * t);
        return Vector3.Dot(Vector3.Cross(b - a, p - a), n) >= 0
            && Vector3.Dot(Vector3.Cross(c - b, p - b), n) >= 0
            && Vector3.Dot(Vector3.Cross(a - c, p - c), n) >= 0;
    }

    private static Vector3 BruteClosest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        // Projection when inside, else closest point on the three edges.
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        var q = p - (n * Vector3.Dot(p - a, n));
        if (Vector3.Dot(Vector3.Cross(b - a, q - a), n) >= 0
            && Vector3.Dot(Vector3.Cross(c - b, q - b), n) >= 0
            && Vector3.Dot(Vector3.Cross(a - c, q - c), n) >= 0)
        {
            return q;
        }

        var best = OnSegment(p, a, b);
        foreach (var candidate in new[] { OnSegment(p, b, c), OnSegment(p, c, a) })
        {
            if (Vector3.DistanceSquared(p, candidate) < Vector3.DistanceSquared(p, best))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static Vector3 OnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return a + (ab * t);
    }
}
