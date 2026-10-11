using System.Diagnostics;
using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

using Xunit.Abstractions;

namespace ThreeCTool.Gpu.Tests;

[Trait("Category", "Gpu")]
public class ComputeFieldsGpuTests(GpuAndCpuFixture fixture, ITestOutputHelper output) : IClassFixture<GpuAndCpuFixture>
{
    [Fact]
    public void EngagementMatchesCpu()
    {
        Assert.NotEqual(ComputeBackend.Cpu, fixture.Gpu.Device.Backend);
        Vector2[] path = [.. Enumerable.Range(0, 100).Select(i => new Vector2(5 + (i % 2 == 0 ? 0 : 90), 5 + (i * 0.88f)))];
        Vector2[] centers = [.. Enumerable.Range(0, 20000).Select(i => new Vector2(3 + (i % 200 * 0.47f), 3 + (i / 200 * 0.93f)))];

        (double Area, float[] Angles) Run(ComputeContext compute, out TimeSpan elapsed)
        {
            using var map = EngagementMap.FromFunction(compute, Vector2.Zero, 0.05f, 2000, 2000, (x, y) => (x - 50) * (x - 50) + ((y - 50) * (y - 50)) < 1600);
            var watch = Stopwatch.StartNew();
            var area = map.Remove(path, 3);
            var angles = map.Engagement(centers, 3);
            elapsed = watch.Elapsed;
            return (area, angles);
        }

        Run(fixture.Gpu, out _);
        var gpu = Run(fixture.Gpu, out var gpuTime);
        var cpu = Run(fixture.Cpu, out var cpuTime);
        output.WriteLine($"Engagement 2000² raster, 100-segment path + 20k centres: GPU {gpuTime.TotalMilliseconds:F0} ms, CPU accelerator {cpuTime.TotalMilliseconds:F0} ms");

        // Cells exactly on the cutter edge may round differently.
        Assert.Equal(cpu.Area, gpu.Area, cpu.Area * 1e-4);
        var step = 2 * MathF.PI / 8192;
        var mismatched = cpu.Angles.Zip(gpu.Angles).Count(p => MathF.Abs(p.First - p.Second) > 4 * step);
        Assert.True(mismatched <= cpu.Angles.Length / 1000, $"{mismatched} engagement mismatches");
    }

    [Fact]
    public void CollisionSweepsMatchCpu()
    {
        var mesh = TestMeshes.Mushroom();
        using var gpuMesh = new GpuMesh(fixture.Gpu, mesh);
        using var cpuMesh = new GpuMesh(fixture.Cpu, mesh);
        var bounds = mesh.Bounds;
        using var gpuStock = DexelStock.Box(fixture.Gpu, bounds, 0.2f);
        using var cpuStock = DexelStock.Box(fixture.Cpu, bounds, 0.2f);
        CollisionPrimitive[] body =
        [
            CollisionPrimitive.Cylinder(8, 30, 3),
            CollisionPrimitive.Cylinder(30, 60, 12),
            CollisionPrimitive.Box(new Vector3(-20, -6, 60), new Vector3(25, 6, 90)),
        ];
        var random = new Random(1);
        var poses = new ToolPose[100000];
        for (var i = 0; i < poses.Length; i++)
        {
            var tip = bounds.Min + (bounds.Size * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()));
            var axis = new Vector3((random.NextSingle() - 0.5f) * 1.5f, (random.NextSingle() - 0.5f) * 1.5f, 1);
            poses[i] = new ToolPose(tip, axis, new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, 0));
        }

        CollisionSweep.AgainstMesh(gpuMesh, body, poses.AsSpan(0, 100));
        var watch = Stopwatch.StartNew();
        var gpuMeshMasks = CollisionSweep.AgainstMesh(gpuMesh, body, poses, 0.5f, solid: true);
        var meshTime = watch.Elapsed;
        CollisionSweep.AgainstStock(gpuStock, body, poses.AsSpan(0, 100));
        watch.Restart();
        var gpuStockMasks = CollisionSweep.AgainstStock(gpuStock, body, poses, 0.5f);
        var stockTime = watch.Elapsed;
        output.WriteLine($"Collision, {poses.Length} poses × {body.Length} primitives: mesh {meshTime.TotalMilliseconds:F0} ms, stock {stockTime.TotalMilliseconds:F0} ms (GPU)");

        // The CPU accelerator is slow on the large holder box: compare a subset.
        var subset = poses.AsSpan(0, 10000);
        var cpuMeshMasks = CollisionSweep.AgainstMesh(cpuMesh, body, subset, 0.5f, solid: true);
        var cpuStockMasks = CollisionSweep.AgainstStock(cpuStock, body, subset, 0.5f);
        var meshMismatch = cpuMeshMasks.Zip(gpuMeshMasks).Count(p => p.First != p.Second);
        var stockMismatch = cpuStockMasks.Zip(gpuStockMasks).Count(p => p.First != p.Second);
        output.WriteLine($"Mismatches: mesh {meshMismatch}, stock {stockMismatch}; colliding poses {gpuMeshMasks.Count(m => m != 0)}");
        Assert.True(meshMismatch <= subset.Length / 2000);
        Assert.True(stockMismatch <= subset.Length / 2000);
        Assert.True(gpuMeshMasks.Count(m => m != 0) > poses.Length / 10);
    }

    [Fact]
    public void DistanceTransformIsBitIdentical()
    {
        const int count = 2048;
        var random = new Random(2);
        var inside = new bool[count * count];
        for (var i = 0; i < inside.Length; i++)
        {
            var x = i % count;
            var y = i / count;
            inside[i] = ((x - 1000) * (x - 1000)) + ((y - 900) * (y - 900)) < 600 * 600 || random.NextDouble() < 1e-4;
        }

        DistanceField2D.FromRegion(fixture.Gpu, inside.AsSpan(0, 64 * 64), 64, 64, 0.1f);
        var watch = Stopwatch.StartNew();
        var gpu = DistanceField2D.FromRegion(fixture.Gpu, inside, count, count, 0.1f);
        output.WriteLine($"Signed distance transform {count}²: GPU {watch.ElapsedMilliseconds} ms");
        var cpu = DistanceField2D.FromRegion(fixture.Cpu, inside, count, count, 0.1f);
        Assert.Equal(cpu.Values, gpu.Values);
    }

    [Fact]
    public void HeightfieldOffsetMatchesCpu()
    {
        const int count = 300;
        var heights = new float[count * count];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = (5 * MathF.Sin(i % count * 0.05f) * MathF.Cos(i / count * 0.04f)) + ((i * 7919 % 13) * 0.01f);
        }

        var cutter = CutterProfile.BullNose(3, 0.5);
        var gpu = HeightfieldOps.CutterOffset(fixture.Gpu, heights, count, count, 0.05f, cutter);
        var cpu = HeightfieldOps.CutterOffset(fixture.Cpu, heights, count, count, 0.05f, cutter);
        Assert.All(cpu.Zip(gpu), p => Assert.Equal(p.First, p.Second, 1e-4f));

        var large = new float[2000 * 2000];
        for (var i = 0; i < large.Length; i++)
        {
            large[i] = 3 * MathF.Sin(i % 2000 * 0.01f) * MathF.Cos(i / 2000 * 0.013f);
        }

        var watch = Stopwatch.StartNew();
        HeightfieldOps.BallOffset(fixture.Gpu, large, 2000, 2000, 0.05f, 1.5f);
        output.WriteLine($"Ball offset 2000² heightfield, R 1.5 mm at 0.05 mm (61² neighbourhood): GPU {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void DistanceField3DAndProjectionMatchCpu()
    {
        var mesh = TestMeshes.Mushroom();
        using var gpuMesh = new GpuMesh(fixture.Gpu, mesh);
        using var cpuMesh = new GpuMesh(fixture.Cpu, mesh);
        var bounds = new BoundingBox(mesh.Bounds.Min - new Vector3(3), mesh.Bounds.Max + new Vector3(3));
        var watch = Stopwatch.StartNew();
        var gpu = DistanceField3D.Build(gpuMesh, bounds, 0.5f, 3, withSign: true);
        output.WriteLine($"Signed field {gpu.CountX}×{gpu.CountY}×{gpu.CountZ}: GPU {watch.ElapsedMilliseconds} ms");
        var cpu = DistanceField3D.Build(cpuMesh, bounds, 0.5f, 3, withSign: true);
        var mismatched = cpu.Values.Zip(gpu.Values).Count(p => MathF.Abs(p.First - p.Second) > 1e-3f);
        Assert.True(mismatched <= cpu.Values.Count / 10000, $"{mismatched} field mismatches");

        Vector2[] points = [.. Enumerable.Range(0, 5000).Select(i => new Vector2(bounds.Min.X + (i % 70 * bounds.Size.X / 70), bounds.Min.Y + (i / 70 * bounds.Size.Y / 72)))];
        var gpuOffset = SurfaceProjection.ProjectOffset(gpuMesh, points, 1.2f);
        var cpuOffset = SurfaceProjection.ProjectOffset(cpuMesh, points, 1.2f);
        for (var i = 0; i < points.Length; i++)
        {
            Assert.Equal(cpuOffset[i].IsHit, gpuOffset[i].IsHit);
            if (cpuOffset[i].IsHit)
            {
                Assert.Equal(cpuOffset[i].Point.Z, gpuOffset[i].Point.Z, 1e-3f);
            }
        }
    }
}
