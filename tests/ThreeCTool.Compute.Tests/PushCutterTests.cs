using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute.Tests;

public class PushCutterTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    private readonly ComputeContext _compute = fixture.Compute;

    private static Fiber XFiber(float y, float z, float from = -10, float to = 50) =>
        new(new Vector3(from, y, z), Vector2.UnitX, to - from);

    [Fact]
    public void BoxWallsBlockCutterRadiusAround()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(40, 40, 10)));
        foreach (var cutter in new[] { CutterProfile.Flat(6), CutterProfile.Ball(6), CutterProfile.BullNose(6, 1) })
        {
            var result = PushCutter.Run(mesh, cutter, [XFiber(20, 5)]);
            var interval = Assert.Single(result[0]);
            Assert.Equal(-3 + 10, interval.Enter, 3);
            Assert.Equal(43 + 10, interval.Exit, 3);
        }
    }

    [Fact]
    public void NearTopBallReachIsReduced()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(40, 40, 10)));
        var reach = MathF.Sqrt(5);
        var result = PushCutter.Run(mesh, CutterProfile.Ball(6), [XFiber(20, 9), XFiber(41, 5), XFiber(20, 10.5f)]);

        var near = Assert.Single(result[0]);
        Assert.Equal(10 - reach, near.Enter, 3);
        Assert.Equal(50 + reach, near.Exit, 3);

        var beside = Assert.Single(result[1]);
        Assert.Equal(10 - MathF.Sqrt(8), beside.Enter, 3);
        Assert.Equal(50 + MathF.Sqrt(8), beside.Exit, 3);

        Assert.Empty(result[2]);
    }

    [Fact]
    public void OverhangBlocksTheShank()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Mushroom());
        var result = PushCutter.Run(mesh, CutterProfile.Ball(6), [new Fiber(new Vector3(-50, 0, 10), Vector2.UnitX, 100)]);
        var interval = Assert.Single(result[0]);
        Assert.Equal(-33 + 50, interval.Enter, 3);
        Assert.Equal(33 + 50, interval.Exit, 3);
    }

    [Fact]
    public void SeparateObstaclesGiveSeparateIntervals()
    {
        var mesh = Mesh.Merge(
            [TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10)), TestMeshes.Box(new Vector3(20, 0, 0), new Vector3(30, 10, 10))],
            "two");
        using var gpuMesh = new GpuMesh(_compute, mesh);
        var result = PushCutter.Run(gpuMesh, CutterProfile.Flat(4), [new Fiber(new Vector3(-10, 5, 5), Vector2.UnitX, 50)]);
        Assert.Equal(2, result[0].Length);
        Assert.Equal(new FiberInterval(8, 22), result[0][0], (a, b) => MathF.Abs(a.Enter - b.Enter) < 1e-3f && MathF.Abs(a.Exit - b.Exit) < 1e-3f);
        Assert.Equal(new FiberInterval(28, 42), result[0][1], (a, b) => MathF.Abs(a.Enter - b.Enter) < 1e-3f && MathF.Abs(a.Exit - b.Exit) < 1e-3f);
    }

    [Fact]
    public void ManySmallObstaclesOverflowAndAreSplit()
    {
        var boxes = Enumerable.Range(0, 80).Select(i => TestMeshes.Box(new Vector3(i * 4, 0, 0), new Vector3((i * 4) + 1, 1, 5))).ToArray();
        using var mesh = new GpuMesh(_compute, Mesh.Merge(boxes, "comb"));
        var result = PushCutter.Run(mesh, CutterProfile.Flat(1), [new Fiber(new Vector3(-5, 0.5f, 1), Vector2.UnitX, 330)]);
        Assert.Equal(80, result[0].Length);
        for (var i = 0; i < 80; i++)
        {
            Assert.Equal((i * 4) - 0.5f + 5, result[0][i].Enter, 3);
            Assert.Equal((i * 4) + 1.5f + 5, result[0][i].Exit, 3);
        }
    }

    [Fact]
    public void WavySurfaceMatchesBruteForce()
    {
        var surface = Reference.Wavy();
        using var mesh = new GpuMesh(_compute, surface);
        Fiber[] fibers =
        [
            new(new Vector3(-5, 7, 1), Vector2.UnitX, 40),
            new(new Vector3(-5, 15.5f, 3), Vector2.UnitX, 40),
            new(new Vector3(12, -5, 2), Vector2.UnitY, 40),
            new(new Vector3(-5, -5, 0), Vector2.Normalize(new Vector2(1, 1)), 55),
        ];
        foreach (var cutter in Reference.Cutters)
        {
            var result = PushCutter.Run(mesh, cutter, fibers);
            for (var f = 0; f < fibers.Length; f++)
            {
                var expected = BruteForce(surface, cutter, fibers[f]);
                Assert.Equal(expected.Count, result[f].Length);
                for (var k = 0; k < expected.Count; k++)
                {
                    // The reference samples the triangles, so it sees slightly less than the exact interval.
                    Assert.InRange(result[f][k].Enter, expected[k].Enter - 0.06f, expected[k].Enter + 1e-3f);
                    Assert.InRange(result[f][k].Exit, expected[k].Exit - 1e-3f, expected[k].Exit + 0.06f);
                }
            }
        }
    }

    /// <summary>Union over densely sampled triangle points of the t-range each point blocks.</summary>
    private static List<FiberInterval> BruteForce(Mesh mesh, CutterProfile cutter, Fiber fiber, int samples = 50)
    {
        var d = Vector2.Normalize(fiber.Direction);
        var start = new Vector2(fiber.Start.X, fiber.Start.Y);
        var spans = new List<FiberInterval>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            var lo = float.PositiveInfinity;
            var hi = float.NegativeInfinity;
            for (var i = 0; i <= samples; i++)
            {
                for (var j = 0; i + j <= samples; j++)
                {
                    var p = a + ((b - a) * i / samples) + ((c - a) * j / samples);
                    var height = p.Z - fiber.Start.Z;
                    if (height < 0)
                    {
                        continue;
                    }

                    var rho = cutter.RadiusAt(height);
                    var q = new Vector2(p.X, p.Y) - start;
                    var along = Vector2.Dot(q, d);
                    var perp = (q.Y * d.X) - (q.X * d.Y);
                    if (MathF.Abs(perp) > rho)
                    {
                        continue;
                    }

                    var half = MathF.Sqrt((rho * rho) - (perp * perp));
                    lo = MathF.Min(lo, along - half);
                    hi = MathF.Max(hi, along + half);
                }
            }

            lo = MathF.Max(lo, 0);
            hi = MathF.Min(hi, fiber.Length);
            if (lo <= hi)
            {
                spans.Add(new FiberInterval(lo, hi));
            }
        }

        spans.Sort((x, y) => x.Enter.CompareTo(y.Enter));
        var merged = new List<FiberInterval>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Enter <= merged[^1].Exit + 0.05f)
            {
                merged[^1] = merged[^1] with { Exit = MathF.Max(merged[^1].Exit, span.Exit) };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }
}
