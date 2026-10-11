using System.Diagnostics;
using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

using Xunit.Abstractions;

namespace ThreeCTool.Gpu.Tests;

[Trait("Category", "Gpu")]
public class MeshQueryGpuTests(GpuAndCpuFixture fixture, ITestOutputHelper output) : IClassFixture<GpuAndCpuFixture>
{
    [Fact]
    public void GpuMatchesCpu()
    {
        var source = Mesh.Merge([TestMeshes.Sphere(10, 32, 64), TestMeshes.Mushroom()], "mix");
        using var gpuMesh = new GpuMesh(fixture.Gpu, source);
        using var cpuMesh = new GpuMesh(fixture.Cpu, source);
        var random = new Random(5);
        var points = new Vector3[5000];
        var directions = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new Vector3((random.NextSingle() - 0.5f) * 80, (random.NextSingle() - 0.5f) * 80, (random.NextSingle() - 0.5f) * 80);
            directions[i] = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f);
        }

        var gpuHits = MeshQueries.Raycast(gpuMesh, points, directions);
        var cpuHits = MeshQueries.Raycast(cpuMesh, points, directions);
        for (var i = 0; i < points.Length; i++)
        {
            Assert.Equal(cpuHits[i].IsHit, gpuHits[i].IsHit);
            if (cpuHits[i].IsHit)
            {
                Assert.True(MathF.Abs(cpuHits[i].Distance - gpuHits[i].Distance) < 1e-3f, $"ray {i}");
            }
        }

        var gpuDistances = MeshQueries.SignedDistance(gpuMesh, points);
        var cpuDistances = MeshQueries.SignedDistance(cpuMesh, points);
        for (var i = 0; i < points.Length; i++)
        {
            Assert.True(MathF.Abs(cpuDistances[i].Distance - gpuDistances[i].Distance) < 1e-3f, $"point {i}: {cpuDistances[i].Distance} vs {gpuDistances[i].Distance}");
        }
    }

    [Fact]
    public void CarBodySignedDistanceIsFast()
    {
        if (TestModels.Find("CarBody.stl") is not { } path)
        {
            return;
        }

        var mesh = MeshImporter.Load(path).Bodies[0].Mesh;
        using var gpuMesh = new GpuMesh(fixture.Gpu, mesh);
        var bounds = mesh.Bounds;
        var random = new Random(1);
        var points = new Vector3[200_000];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = bounds.Min + (bounds.Size * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()));
        }

        _ = MeshQueries.SignedDistance(gpuMesh, points.AsSpan(0, 16));
        var watch = Stopwatch.StartNew();
        var result = MeshQueries.SignedDistance(gpuMesh, points);
        watch.Stop();
        output.WriteLine($"{fixture.Gpu.Device.Name}: {points.Length} signed distances on {mesh.TriangleCount} triangles in {watch.ElapsedMilliseconds} ms");
        Assert.All(result, r => Assert.True(r.Found));
    }
}
