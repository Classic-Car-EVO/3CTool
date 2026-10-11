using System.Diagnostics;
using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

using Xunit.Abstractions;

namespace ThreeCTool.Gpu.Tests;

/// <summary>A GPU context and a CPU-accelerator context running the same kernels, for equivalence checks.</summary>
public sealed class GpuAndCpuFixture : IDisposable
{
    public ComputeContext Gpu { get; } = ComputeContext.Create();

    public ComputeContext Cpu { get; } = ComputeContext.Create(ComputeBackend.Cpu);

    public void Dispose()
    {
        Gpu.Dispose();
        Cpu.Dispose();
    }
}

[Trait("Category", "Gpu")]
public class DropCutterGpuTests(GpuAndCpuFixture fixture, ITestOutputHelper output) : IClassFixture<GpuAndCpuFixture>
{
    private static readonly CutterProfile[] Cutters =
    [
        CutterProfile.Flat(6),
        CutterProfile.Ball(6),
        CutterProfile.BullNose(6, 1),
        CutterProfile.Cone(6, 60, 0.4),
        CutterProfile.TaperedBall(6, 1, 10),
    ];

    [Fact]
    public void GpuMatchesCpuOnWavySurface()
    {
        Assert.NotEqual(ComputeBackend.Cpu, fixture.Gpu.Device.Backend);
        var surface = TestMeshes.Heightfield(new Vector2(0, 0), new Vector2(60, 60), 60, (x, y) => (4 * MathF.Sin(x / 6) * MathF.Cos(y / 9)) + (0.3f * y));
        using var gpuMesh = new GpuMesh(fixture.Gpu, surface);
        using var cpuMesh = new GpuMesh(fixture.Cpu, surface);
        var axis = Vector3.Normalize(new Vector3(0.2f, 0.1f, 1));
        foreach (var cutter in Cutters)
        {
            var gpu = DropCutter.Grid(gpuMesh, cutter, new Vector2(-2, -2), 0.5f, 129, 129);
            var cpu = DropCutter.Grid(cpuMesh, cutter, new Vector2(-2, -2), 0.5f, 129, 129);
            AssertClose(cpu, gpu, 1e-3f);

            Vector3[] origins = [.. Enumerable.Range(0, 500).Select(i => new Vector3(i % 60, i / 8.5f, 0))];
            Vector3[] axes = [.. origins.Select(_ => axis)];
            AssertClose(DropCutter.Drop(cpuMesh, cutter, origins, axes), DropCutter.Drop(gpuMesh, cutter, origins, axes), 1e-3f);
        }
    }

    [Fact]
    public void CarBodyGridDropIsFast()
    {
        if (TestModels.Find("CarBody.stl") is not { } path)
        {
            return;
        }

        var mesh = MeshImporter.Load(path).Bodies[0].Mesh;
        using var gpuMesh = new GpuMesh(fixture.Gpu, mesh);
        var bounds = mesh.Bounds;
        var step = 0.25f;
        var countX = (int)(bounds.Size.X / step) + 1;
        var countY = (int)(bounds.Size.Y / step) + 1;
        var origin = new Vector2(bounds.Min.X, bounds.Min.Y);
        _ = DropCutter.Grid(gpuMesh, CutterProfile.Ball(6), origin, step, 4, 4);

        var watch = Stopwatch.StartNew();
        var heights = DropCutter.Grid(gpuMesh, CutterProfile.Ball(6), origin, step, countX, countY);
        watch.Stop();
        output.WriteLine($"{fixture.Gpu.Device.Name}: {heights.Length} drops on {mesh.TriangleCount} triangles in {watch.ElapsedMilliseconds} ms");

        Assert.Contains(heights, h => h > bounds.Max.Z - 6);
        Assert.All(heights, h => Assert.True(float.IsNegativeInfinity(h) || h <= bounds.Max.Z + 1e-3f));

        // Spot-check against the CPU accelerator on a coarse subset.
        using var cpuMesh = new GpuMesh(fixture.Cpu, mesh);
        var coarseGpu = DropCutter.Grid(gpuMesh, CutterProfile.Ball(6), origin, 5, countX / 20, countY / 20);
        var coarseCpu = DropCutter.Grid(cpuMesh, CutterProfile.Ball(6), origin, 5, countX / 20, countY / 20);
        AssertClose(coarseCpu, coarseGpu, 2e-3f);
    }

    [Fact]
    public void PushCutterGpuMatchesCpu()
    {
        var surface = TestMeshes.Heightfield(new Vector2(0, 0), new Vector2(60, 60), 60, (x, y) => (4 * MathF.Sin(x / 6) * MathF.Cos(y / 9)) + (0.3f * y));
        using var gpuMesh = new GpuMesh(fixture.Gpu, surface);
        using var cpuMesh = new GpuMesh(fixture.Cpu, surface);
        foreach (var cutter in Cutters)
        {
            foreach (var z in new[] { 0f, 5f, 12f })
            {
                var gpu = PushCutter.Grid(gpuMesh, cutter, z, new Vector2(-5, -5), new Vector2(65, 65), 0.7f);
                var cpu = PushCutter.Grid(cpuMesh, cutter, z, new Vector2(-5, -5), new Vector2(65, 65), 0.7f);
                AssertSameIntervals(cpu.XIntervals, gpu.XIntervals);
                AssertSameIntervals(cpu.YIntervals, gpu.YIntervals);
            }
        }
    }

    [Fact]
    public void CarBodyWaterlineIsFast()
    {
        if (TestModels.Find("CarBody.stl") is not { } path)
        {
            return;
        }

        var mesh = MeshImporter.Load(path).Bodies[0].Mesh;
        using var gpuMesh = new GpuMesh(fixture.Gpu, mesh);
        var bounds = mesh.Bounds;
        var min = new Vector2(bounds.Min.X - 5, bounds.Min.Y - 5);
        var max = new Vector2(bounds.Max.X + 5, bounds.Max.Y + 5);
        _ = PushCutter.Grid(gpuMesh, CutterProfile.Ball(6), bounds.Center.Z, min, max, 50);

        var watch = Stopwatch.StartNew();
        var result = PushCutter.Grid(gpuMesh, CutterProfile.Ball(6), bounds.Center.Z, min, max, 0.1f);
        watch.Stop();
        output.WriteLine($"{fixture.Gpu.Device.Name}: {result.XFibers.Length + result.YFibers.Length} fibers on {mesh.TriangleCount} triangles in {watch.ElapsedMilliseconds} ms");
        Assert.Contains(result.XIntervals, list => list.Length > 0);
    }

    private static void AssertSameIntervals(FiberInterval[][] expected, FiberInterval[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Length == actual[i].Length, $"fiber {i}: {expected[i].Length} vs {actual[i].Length} intervals");
            for (var k = 0; k < expected[i].Length; k++)
            {
                Assert.True(MathF.Abs(expected[i][k].Enter - actual[i][k].Enter) < 2e-3f, $"fiber {i}: {expected[i][k]} vs {actual[i][k]}");
                Assert.True(MathF.Abs(expected[i][k].Exit - actual[i][k].Exit) < 2e-3f, $"fiber {i}: {expected[i][k]} vs {actual[i][k]}");
            }
        }
    }

    private static void AssertClose(float[] expected, float[] actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (float.IsNegativeInfinity(expected[i]))
            {
                Assert.True(float.IsNegativeInfinity(actual[i]), $"index {i}: expected no contact, got {actual[i]}");
            }
            else
            {
                Assert.True(MathF.Abs(expected[i] - actual[i]) <= tolerance, $"index {i}: expected {expected[i]}, got {actual[i]}");
            }
        }
    }
}
