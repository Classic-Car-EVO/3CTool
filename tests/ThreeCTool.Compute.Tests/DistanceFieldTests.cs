using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute.Tests;

public class DistanceFieldTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    [Theory]
    [InlineData(37, 23, 0.05)]
    [InlineData(64, 1, 0.1)]
    [InlineData(1, 50, 0.1)]
    [InlineData(40, 40, 0.002)]
    public void SiteDistancesAreExact(int countX, int countY, double density)
    {
        var random = new Random(countX + countY);
        var sites = new bool[countX * countY];
        for (var i = 0; i < sites.Length; i++)
        {
            sites[i] = random.NextDouble() < density;
        }

        sites[sites.Length / 2] = true;
        var field = DistanceField2D.FromSites(fixture.Compute, sites, countX, countY, 0.5f);
        for (var y = 0; y < countY; y++)
        {
            for (var x = 0; x < countX; x++)
            {
                Assert.Equal(MathF.Sqrt(BruteSquared(sites, countX, countY, x, y, s => s)) * 0.5f, field[x, y]);
            }
        }
    }

    [Fact]
    public void EmptyMaskIsInfinite()
    {
        var field = DistanceField2D.FromSites(fixture.Compute, new bool[12], 4, 3, 1);
        Assert.All(field.Values, v => Assert.Equal(float.PositiveInfinity, v));
    }

    [Fact]
    public void SignedDiskMatchesBruteForceAndRadius()
    {
        const int count = 64;
        const float resolution = 0.25f;
        var center = new Vector2(8.1f, 7.7f);
        var inside = new bool[count * count];
        for (var y = 0; y < count; y++)
        {
            for (var x = 0; x < count; x++)
            {
                inside[(y * count) + x] = Vector2.Distance(Cell(x, y, resolution), center) <= 5;
            }
        }

        var field = DistanceField2D.FromRegion(fixture.Compute, inside, count, count, resolution, Vector2.Zero);
        for (var y = 0; y < count; y++)
        {
            for (var x = 0; x < count; x++)
            {
                var isInside = inside[(y * count) + x];
                var d = MathF.Sqrt(BruteSquared(inside, count, count, x, y, s => s != isInside));
                Assert.Equal(isInside ? -(d - 0.5f) * resolution : (d - 0.5f) * resolution, field[x, y]);
                Assert.Equal(Vector2.Distance(Cell(x, y, resolution), center) - 5, field[x, y], 1.2f * resolution);
            }
        }

        Assert.Equal(Vector2.Distance(new Vector2(14, 1), center) - 5, field.Sample(new Vector2(14, 1)), 0.4f);
    }

    [Fact]
    public void SphereFieldIsRadialDistance()
    {
        using var mesh = new GpuMesh(fixture.Compute, TestMeshes.Sphere(10, 48, 96));
        var field = DistanceField3D.Build(mesh, new BoundingBox(new Vector3(-15), new Vector3(15)), 1, 6, withSign: true);
        Assert.Equal(31, field.CountX);
        Assert.Equal(31, field.CountZ);
        for (var k = 0; k < field.CountZ; k += 3)
        {
            for (var j = 0; j < field.CountY; j++)
            {
                for (var i = 0; i < field.CountX; i++)
                {
                    var p = field.Origin + (new Vector3(i, j, k) * field.Resolution);
                    Assert.Equal(Math.Clamp(p.Length() - 10, -6, 6), field[i, j, k], 0.03f);
                }
            }
        }

        var random = new Random(5);
        for (var n = 0; n < 200; n++)
        {
            var direction = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var p = direction * (7 + (5 * random.NextSingle()));
            Assert.Equal(p.Length() - 10, field.Sample(p), 0.1f);
            Assert.True(Vector3.Dot(Vector3.Normalize(field.Gradient(p)), direction) > 0.99f);
        }
    }

    [Fact]
    public void UnsignedFieldIsPositive()
    {
        using var mesh = new GpuMesh(fixture.Compute, TestMeshes.Sphere(10, 24, 48));
        var field = DistanceField3D.Build(mesh, new BoundingBox(new Vector3(-12), new Vector3(12)), 2, 4, withSign: false);
        Assert.False(field.IsSigned);
        Assert.Equal(4, field[6, 6, 6]);
        Assert.All(field.Values, v => Assert.InRange(v, 0, 4));
    }

    private static Vector2 Cell(int x, int y, float resolution) => new((x + 0.5f) * resolution, (y + 0.5f) * resolution);

    private static int BruteSquared(bool[] mask, int countX, int countY, int x, int y, Func<bool, bool> isSite)
    {
        var best = int.MaxValue;
        for (var qy = 0; qy < countY; qy++)
        {
            for (var qx = 0; qx < countX; qx++)
            {
                if (isSite(mask[(qy * countX) + qx]))
                {
                    best = Math.Min(best, ((qx - x) * (qx - x)) + ((qy - y) * (qy - y)));
                }
            }
        }

        return best;
    }
}

public class SurfaceProjectionTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    [Fact]
    public void ProjectsOntoSphereTop()
    {
        using var mesh = new GpuMesh(fixture.Compute, TestMeshes.Sphere(10, 48, 96));
        Vector2[] points = [new(0, 0), new(3, 4), new(-6, 5), new(8.5f, -2), new(11, 0)];
        var projected = SurfaceProjection.Project(mesh, points);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(projected[i].IsHit);
            Assert.Equal(MathF.Sqrt(100 - points[i].LengthSquared()), projected[i].Point.Z, 0.05f);
            Assert.True(Vector3.Dot(projected[i].Normal, Vector3.Normalize(projected[i].Point)) > 0.99f);
        }

        Assert.False(projected[4].IsHit);
    }

    [Fact]
    public void OffsetProjectionFollowsGrownSphereAndBoxEdge()
    {
        using var sphere = new GpuMesh(fixture.Compute, TestMeshes.Sphere(10, 48, 96));
        Vector2[] points = [new(0, 0), new(5, 5), new(-9, 6), new(13, 0)];
        var projected = SurfaceProjection.ProjectOffset(sphere, points, 2);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(MathF.Sqrt(144 - points[i].LengthSquared()), projected[i].Point.Z, 0.05f);
            Assert.True(Vector3.Dot(projected[i].Normal, Vector3.Normalize(projected[i].Point)) > 0.995f);
        }

        Assert.False(projected[3].IsHit);

        using var box = new GpuMesh(fixture.Compute, TestMeshes.Box(Vector3.Zero, new Vector3(10)));
        var edge = SurfaceProjection.ProjectOffset(box, [new(5, 5), new(10.5f, 5)], 2);
        Assert.Equal(12, edge[0].Point.Z, 1e-4f);
        Assert.True(Vector3.Dot(edge[0].Normal, Vector3.UnitZ) > 0.9999f);
        Assert.Equal(10 + MathF.Sqrt(3.75f), edge[1].Point.Z, 1e-3f);
        var expectedNormal = Vector3.Normalize(new Vector3(0.5f, 0, MathF.Sqrt(3.75f)));
        Assert.True(Vector3.Dot(edge[1].Normal, expectedNormal) > 0.9999f);
    }
}

public class HeightfieldOpsTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    [Fact]
    public void MatchesBruteForceWithHoles()
    {
        const int countX = 41;
        const int countY = 29;
        const float resolution = 0.25f;
        var random = new Random(9);
        var heights = new float[countX * countY];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = random.NextDouble() < 0.1 ? float.NaN : (3 * MathF.Sin(i * 0.37f)) + random.NextSingle();
        }

        foreach (var cutter in Reference.Cutters)
        {
            var result = HeightfieldOps.CutterOffset(fixture.Compute, heights, countX, countY, resolution, cutter);
            for (var y = 0; y < countY; y++)
            {
                for (var x = 0; x < countX; x++)
                {
                    var best = float.NegativeInfinity;
                    for (var qy = 0; qy < countY; qy++)
                    {
                        for (var qx = 0; qx < countX; qx++)
                        {
                            var d = MathF.Sqrt((((qx - x) * resolution) * ((qx - x) * resolution)) + (((qy - y) * resolution) * ((qy - y) * resolution)));
                            var h = heights[(qy * countX) + qx];
                            if (d <= cutter.Radius && !float.IsNaN(h))
                            {
                                best = MathF.Max(best, h - cutter.HeightAt(d));
                            }
                        }
                    }

                    Assert.Equal(best, result[(y * countX) + x], 1e-4f);
                }
            }
        }
    }

    [Fact]
    public void SpikeGivesInvertedCutter()
    {
        const int count = 61;
        var heights = Enumerable.Repeat(float.NegativeInfinity, count * count).ToArray();
        heights[(30 * count) + 30] = 5;
        var ball = HeightfieldOps.BallOffset(fixture.Compute, heights, count, count, 0.1f, 2);
        for (var y = 0; y < count; y++)
        {
            for (var x = 0; x < count; x++)
            {
                var d = 0.1f * MathF.Sqrt(((x - 30) * (x - 30)) + ((y - 30) * (y - 30)));
                var value = ball[(y * count) + x];
                if (d <= 2)
                {
                    Assert.Equal(5 + MathF.Sqrt(4 - (d * d)), value, 1e-4f);
                }
                else
                {
                    Assert.Equal(float.NegativeInfinity, value);
                }
            }
        }

        var plane = HeightfieldOps.BallOffset(fixture.Compute, Enumerable.Repeat(1.5f, 400).ToArray(), 20, 20, 0.2f, 1);
        Assert.All(plane, v => Assert.Equal(2.5f, v, 1e-6f));
    }
}
