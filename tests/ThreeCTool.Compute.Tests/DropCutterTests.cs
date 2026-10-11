using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute.Tests;

/// <summary>One CPU-accelerator context shared by a test class (kernel compilation is cached per context).</summary>
public sealed class CpuComputeFixture : IDisposable
{
    public ComputeContext Compute { get; } = ComputeContext.Create(ComputeBackend.Cpu);

    public void Dispose() => Compute.Dispose();
}

internal static class Reference
{
    public static readonly CutterProfile[] Cutters =
    [
        CutterProfile.Flat(6),
        CutterProfile.Ball(6),
        CutterProfile.BullNose(6, 1),
        CutterProfile.Cone(6, 60, 0.4),
        CutterProfile.TaperedBall(6, 1, 10),
    ];

    /// <summary>Wavy, tilted test surface.</summary>
    public static Mesh Wavy() => TestMeshes.Heightfield(
        new Vector2(0, 0), new Vector2(30, 30), 12, (x, y) => (3 * MathF.Sin(x / 5) * MathF.Cos(y / 7)) + (0.2f * x));

    /// <summary>
    /// Brute-force drop-cutter: densely samples every triangle (vertices and edges included) and takes the maximum of
    /// the tip height touching each sample. A lower bound that converges to the exact answer.
    /// </summary>
    public static float Drop(Mesh mesh, CutterProfile cutter, Vector3 origin, Vector3 axis, int samples = 60)
    {
        var w = Vector3.Normalize(axis);
        var best = float.NegativeInfinity;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            for (var i = 0; i <= samples; i++)
            {
                for (var j = 0; i + j <= samples; j++)
                {
                    var p = a + ((b - a) * i / samples) + ((c - a) * j / samples) - origin;
                    var along = Vector3.Dot(p, w);
                    var d = (p - (along * w)).Length();
                    if (d <= cutter.Radius)
                    {
                        best = MathF.Max(best, along - cutter.HeightAt(d));
                    }
                }
            }
        }

        return best;
    }
}

public class CutterProfileTests
{
    [Fact]
    public void ProfilesHaveExpectedShape()
    {
        var ball = CutterProfile.Ball(6);
        Assert.Equal(0, ball.HeightAt(0), 5);
        Assert.Equal(3, ball.HeightAt(3), 5);
        Assert.Equal(3 - MathF.Sqrt(5), ball.HeightAt(2), 5);

        var flat = CutterProfile.Flat(6, 20);
        Assert.Equal(0, flat.HeightAt(2.9f));
        Assert.Equal(20, flat.Height);

        var bull = CutterProfile.BullNose(10, 2);
        Assert.Equal(0, bull.HeightAt(3), 5);
        Assert.Equal(2, bull.HeightAt(5), 5);

        var vbit = CutterProfile.Cone(10, 90);
        Assert.Equal(4, vbit.HeightAt(4), 4);
        Assert.Equal(5, vbit.ProfileHeight, 4);

        var tapered = CutterProfile.TaperedBall(6, 1, 10);
        Assert.Equal(0.5f * MathF.Cos(10 * MathF.PI / 180), tapered.ArcEndRadius, 5);
        Assert.True(tapered.ProfileHeight > 10);
    }

    [Fact]
    public void RadiusAtInvertsHeightAt()
    {
        foreach (var cutter in Reference.Cutters)
        {
            for (var r = cutter.FlatRadius + 0.01f; r < cutter.Radius; r += 0.05f)
            {
                Assert.Equal(r, cutter.RadiusAt(cutter.HeightAt(r)), 2);
            }

            Assert.Equal(-1, cutter.RadiusAt(-0.1f));
            Assert.Equal(cutter.Radius, cutter.RadiusAt(cutter.ProfileHeight + 1));
        }
    }

    [Fact]
    public void ContactRadiusHasMatchingSlope()
    {
        foreach (var cutter in Reference.Cutters.Where(c => c.CornerRadius > 0))
        {
            foreach (var tan in new[] { 0.1f, 0.5f, 1f, 2f })
            {
                var r = cutter.ContactRadius(tan);
                if (r <= cutter.FlatRadius + 1e-4f || r >= cutter.ArcEndRadius - 1e-4f)
                {
                    continue;
                }

                var slope = (cutter.HeightAt(r + 1e-3f) - cutter.HeightAt(r - 1e-3f)) / 2e-3f;
                Assert.Equal(tan, slope, 1);
            }
        }
    }

    [Fact]
    public void FromToolMapsEveryType()
    {
        var tool = new Core.Tooling.Tool { Type = Core.Tooling.ToolType.VBit, Diameter = 12, TipAngle = 90, FluteLength = 15 };
        var profile = CutterProfile.FromTool(tool);
        Assert.Equal(6, profile.Radius);
        Assert.Equal(1, profile.ConeSlope, 5);
        Assert.Equal(15, profile.Height);
    }
}

public class DropCutterTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    private readonly ComputeContext _compute = fixture.Compute;

    [Fact]
    public void FlatTopGivesTopHeightForEveryCutter()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(40, 40, 10)));
        foreach (var cutter in Reference.Cutters)
        {
            var result = DropCutter.Grid(mesh, cutter, new Vector2(10, 10), 5, 5, 5);
            Assert.All(result, z => Assert.Equal(10, z, 4));
        }
    }

    [Fact]
    public void BoxEdgeContactsMatchAnalyticValues()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(40, 40, 10)));
        float[] offsets = [0.5f, 1.5f, 2.5f, 2.99f, 3.5f];
        var origins = offsets.Select(e => new Vector3(40 + e, 20, 0)).ToArray();
        var axes = origins.Select(_ => Vector3.UnitZ).ToArray();

        var flat = DropCutter.Drop(mesh, CutterProfile.Flat(6), origins, axes);
        var ball = DropCutter.Drop(mesh, CutterProfile.Ball(6), origins, axes);
        var vbit = DropCutter.Drop(mesh, CutterProfile.Cone(6, 90), origins, axes);
        for (var i = 0; i < offsets.Length; i++)
        {
            var e = offsets[i];
            if (e < 3)
            {
                Assert.Equal(10, flat[i], 4);
                Assert.Equal(10 - (3 - MathF.Sqrt(9 - (e * e))), ball[i], 3);
                Assert.Equal(10 - e, vbit[i], 3);
            }
            else
            {
                Assert.Equal(float.NegativeInfinity, flat[i]);
                Assert.Equal(float.NegativeInfinity, ball[i]);
            }
        }
    }

    [Fact]
    public void BallOnSphereMatchesAnalyticValue()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Sphere(10, 90, 180));
        var result = DropCutter.Grid(mesh, CutterProfile.Ball(6), new Vector2(0, 0), 2, 6, 1);
        for (var i = 0; i < result.Length; i++)
        {
            var d = 2f * i;
            var expected = MathF.Sqrt((13 * 13) - (d * d)) - 3;
            Assert.Equal(expected, result[i], 1);
        }
    }

    [Fact]
    public void WavySurfaceMatchesBruteForceForAllCutters()
    {
        var surface = Reference.Wavy();
        using var mesh = new GpuMesh(_compute, surface);
        var random = new Random(7);
        var origins = Enumerable.Range(0, 12)
            .Select(_ => new Vector3((float)(random.NextDouble() * 30), (float)(random.NextDouble() * 30), 0))
            .ToArray();
        var axes = origins.Select(_ => Vector3.UnitZ).ToArray();

        foreach (var cutter in Reference.Cutters)
        {
            var result = DropCutter.Drop(mesh, cutter, origins, axes);
            for (var i = 0; i < origins.Length; i++)
            {
                var expected = Reference.Drop(surface, cutter, origins[i], Vector3.UnitZ);
                Assert.InRange(result[i], expected - 1e-3f, expected + 0.06f);
            }
        }
    }

    [Fact]
    public void TiltedAxisMatchesBruteForce()
    {
        var surface = Reference.Wavy();
        using var mesh = new GpuMesh(_compute, surface);
        var axis = Vector3.Normalize(new Vector3(0.3f, -0.4f, 1));
        Vector3[] origins = [new(15, 15, 0), new(8, 20, -2), new(22, 9, 1)];
        foreach (var cutter in Reference.Cutters)
        {
            var result = DropCutter.Drop(mesh, cutter, origins, [axis, axis, axis]);
            for (var i = 0; i < origins.Length; i++)
            {
                var expected = Reference.Drop(surface, cutter, origins[i], axis);
                Assert.InRange(result[i], expected - 1e-3f, expected + 0.06f);
            }
        }
    }

    [Fact]
    public void RotaryDropOnBoxMatchesAnalyticRadius()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(-20, -5, -5), new Vector3(20, 5, 5)));
        var result = DropCutter.Rotary(mesh, CutterProfile.Flat(2), [0f, 10f], [0f, 45f, 90f, 180f, 270f]);

        Assert.Equal(5, result[0], 3);
        Assert.Equal(5 * MathF.Sqrt(2), result[2], 3);
        Assert.Equal(5, result[4], 3);
        Assert.Equal(5, result[6], 3);
        Assert.Equal(5, result[9], 3);
    }

    [Fact]
    public void EmptyMeshAndMissesReportNoContact()
    {
        using var empty = new GpuMesh(_compute, new Mesh([], [], "empty"));
        Assert.All(DropCutter.Grid(empty, CutterProfile.Ball(6), Vector2.Zero, 1, 3, 3), z => Assert.Equal(float.NegativeInfinity, z));

        using var mesh = new GpuMesh(_compute, TestMeshes.UnitCube());
        Assert.Equal(float.NegativeInfinity, DropCutter.Grid(mesh, CutterProfile.Ball(1), new Vector2(50, 50), 1, 1, 1)[0]);
    }

    [Fact]
    public void LargeBatchesAreChunked()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(40, 40, 10)));
        var result = DropCutter.Grid(mesh, CutterProfile.Ball(2), new Vector2(5, 5), 0.01f, 3000, 2);
        Assert.Equal(6000, result.Length);
        Assert.All(result, z => Assert.Equal(10, z, 4));
    }
}
