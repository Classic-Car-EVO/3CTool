using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Compute.Kernels;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute.Tests;

public class CollisionTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    private static readonly BoundingBox Block = new(Vector3.Zero, new Vector3(10));

    [Fact]
    public void TriangleCylinderMatchesSampling()
    {
        var random = new Random(7);
        var hits = 0;
        for (var n = 0; n < 1000; n++)
        {
            var a = RandomPoint(random, 3);
            var b = RandomPoint(random, 3);
            var c = RandomPoint(random, 3);
            var radius = 0.3f + (2 * random.NextSingle());
            var halfHeight = 0.3f + (2 * random.NextSingle());
            var result = CollisionKernel.TriangleCylinder(F(a), F(b), F(c), radius, halfHeight);
            hits += result ? 1 : 0;

            // Sampling finds a lower bound; with the cylinder grown by the sampling gap it must see every true hit.
            Assert.False(SampledInCylinder(a, b, c, radius, halfHeight) && !result);
            Assert.False(result && !SampledInCylinder(a, b, c, radius + 0.05f, halfHeight + 0.05f), $"case {n}");
        }

        Assert.InRange(hits, 100, 900);
    }

    [Fact]
    public void TriangleBoxMatchesSampling()
    {
        var random = new Random(11);
        var hits = 0;
        for (var n = 0; n < 1000; n++)
        {
            var a = RandomPoint(random, 3);
            var b = RandomPoint(random, 3);
            var c = RandomPoint(random, 3);
            var half = new Vector3(0.3f) + (2 * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()));
            var result = CollisionKernel.TriangleBox(F(a), F(b), F(c), F(half));
            hits += result ? 1 : 0;
            Assert.False(SampledInBox(a, b, c, half) && !result);
            Assert.False(result && !SampledInBox(a, b, c, half + new Vector3(0.05f)), $"case {n}");
        }

        Assert.InRange(hits, 100, 900);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    public void VerticalHolderAgainstBlockMeshIsAnalytic(float clearance)
    {
        using var mesh = new GpuMesh(fixture.Compute, TestMeshes.Box(Block.Min, Block.Max));
        var (poses, expected) = VerticalSweep(clearance, sampled: false);
        var masks = CollisionSweep.AgainstMesh(mesh, [CollisionPrimitive.Cylinder(0, 20, 2)], poses, clearance);
        for (var i = 0; i < poses.Length; i++)
        {
            Assert.True(expected[i] == (masks[i] == 1), $"pose {poses[i].Tip}");
        }
    }

    [Fact]
    public void VerticalHolderAgainstBlockStockIsAnalytic()
    {
        using var stock = DexelStock.Box(fixture.Compute, Block, 0.1f);
        var (poses, expected) = VerticalSweep(0, sampled: true);
        var masks = CollisionSweep.AgainstStock(stock, [CollisionPrimitive.Cylinder(0, 20, 2)], poses);
        for (var i = 0; i < poses.Length; i++)
        {
            Assert.True(expected[i] == (masks[i] == 1), $"pose {poses[i].Tip}");
        }
    }

    [Fact]
    public void ShankInsideSolidMeshCollidesOnlyWhenSolid()
    {
        using var mesh = new GpuMesh(fixture.Compute, TestMeshes.Box(Block.Min, Block.Max));
        CollisionPrimitive[] body = [CollisionPrimitive.Cylinder(0, 2, 1), CollisionPrimitive.Box(new Vector3(-1, -1, 30), new Vector3(1, 1, 40))];
        ToolPose[] poses = [new(new Vector3(5, 5, 4), Vector3.UnitZ)];
        Assert.Equal(0u, CollisionSweep.AgainstMesh(mesh, body, poses)[0]);
        Assert.Equal(1u, CollisionSweep.AgainstMesh(mesh, body, poses, solid: true)[0]);

        // Only the second primitive reaches the block.
        ToolPose[] low = [new(new Vector3(5, 5, -25), Vector3.UnitZ)];
        Assert.Equal(2u, CollisionSweep.AgainstMesh(mesh, body, low)[0]);
    }

    [Fact]
    public void TiltedBoxesMatchSeparatingAxisReference()
    {
        using var mesh = new GpuMesh(fixture.Compute, TestMeshes.Box(Block.Min, Block.Max));
        using var stock = DexelStock.Box(fixture.Compute, Block, 0.1f);
        var random = new Random(3);
        var primitive = CollisionPrimitive.Box(new Vector3(-1.5f, -0.5f, 2), new Vector3(2.5f, 1, 9));
        var poses = new ToolPose[2000];
        for (var i = 0; i < poses.Length; i++)
        {
            var tip = new Vector3(-8 + (26 * random.NextSingle()), -8 + (26 * random.NextSingle()), -8 + (26 * random.NextSingle()));
            poses[i] = new ToolPose(tip, RandomPoint(random, 1), RandomPoint(random, 1));
        }

        var meshMasks = CollisionSweep.AgainstMesh(mesh, [primitive], poses, solid: true);
        var stockMasks = CollisionSweep.AgainstStock(stock, [primitive], poses);
        var hits = 0;
        for (var i = 0; i < poses.Length; i++)
        {
            var frame = CollisionSweep.Frame(poses[i]);
            var center = poses[i].Tip + (frame.U * primitive.Center.X) + (frame.V * primitive.Center.Y) + (frame.W * primitive.Center.Z);
            // Growing (or shrinking) both boxes by g moves the overlap test by a ball of radius g.
            bool Overlaps(float grow) => BoxesOverlap(center, frame, primitive.HalfExtents + new Vector3(grow), new BoundingBox(Block.Min - new Vector3(grow), Block.Max + new Vector3(grow)));
            hits += Overlaps(0) ? 1 : 0;
            Assert.False(Overlaps(-1e-3f) && meshMasks[i] == 0, $"mesh miss at {i}");
            Assert.False(!Overlaps(1e-3f) && meshMasks[i] != 0, $"mesh false hit at {i}");

            // Stock rays sit at cell centres: a ball of radius 0.15 always contains one.
            Assert.False(Overlaps(-0.15f) && stockMasks[i] == 0, $"stock miss at {i}");
            Assert.False(!Overlaps(1e-3f) && stockMasks[i] != 0, $"stock false hit at {i}");
        }

        Assert.InRange(hits, 200, 1800);
    }

    [Fact]
    public void FrameFollowsReference()
    {
        var (u, v, w) = CollisionSweep.Frame(new ToolPose(Vector3.Zero, new Vector3(0, 0, 2), new Vector3(0, 1, 1)));
        Assert.Equal(Vector3.UnitY, u);
        Assert.Equal(-Vector3.UnitX, v);
        Assert.Equal(Vector3.UnitZ, w);
        var (u2, _, _) = CollisionSweep.Frame(new ToolPose(Vector3.Zero, Vector3.UnitX));
        Assert.Equal(Vector3.UnitY, u2);
    }

    /// <summary>Cylinder (radius 2, 0…20 above the tip) over a grid of tips at y = 5 against the 10 mm block.</summary>
    private static (ToolPose[] Poses, bool[] Expected) VerticalSweep(float clearance, bool sampled)
    {
        var poses = new List<ToolPose>();
        var expected = new List<bool>();
        for (var ix = 0; ix < 72; ix++)
        {
            for (var iz = 0; iz < 80; iz++)
            {
                var x = -4.1f + (0.25f * ix);
                var z = -25.1f + (0.5f * iz);
                poses.Add(new ToolPose(new Vector3(x, 5, z), Vector3.UnitZ));

                // Stock material is only seen on rays at cell centres 0.05 … 9.95.
                var inset = sampled ? 0.05f : 0;
                var dx = MathF.Max(MathF.Max(inset - x, x - (10 - inset)), 0);
                var r = 2 + clearance;
                expected.Add(z - clearance <= 10 && z + 20 + clearance >= 0 && ((dx * dx) + (sampled ? 0.0025f : 0)) <= r * r);
            }
        }

        return ([.. poses], [.. expected]);
    }

    private static bool BoxesOverlap(Vector3 center, (Vector3 U, Vector3 V, Vector3 W) frame, Vector3 half, BoundingBox box)
    {
        var boxCenter = box.Center;
        var boxHalf = box.Size / 2;
        Vector3[] obb = [frame.U, frame.V, frame.W];
        float[] obbHalf = [half.X, half.Y, half.Z];
        Vector3[] aabb = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        var axes = new List<Vector3>(obb);
        axes.AddRange(aabb);
        foreach (var a in obb)
        {
            foreach (var b in aabb)
            {
                var cross = Vector3.Cross(a, b);
                if (cross.LengthSquared() > 1e-8f)
                {
                    axes.Add(cross);
                }
            }
        }

        foreach (var axis in axes)
        {
            var distance = MathF.Abs(Vector3.Dot(center - boxCenter, axis));
            var r1 = (obbHalf[0] * MathF.Abs(Vector3.Dot(obb[0], axis))) + (obbHalf[1] * MathF.Abs(Vector3.Dot(obb[1], axis))) + (obbHalf[2] * MathF.Abs(Vector3.Dot(obb[2], axis)));
            var r2 = (boxHalf.X * MathF.Abs(axis.X)) + (boxHalf.Y * MathF.Abs(axis.Y)) + (boxHalf.Z * MathF.Abs(axis.Z));
            if (distance > r1 + r2)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SampledInCylinder(Vector3 a, Vector3 b, Vector3 c, float radius, float halfHeight) =>
        Sample(a, b, c).Any(p => MathF.Abs(p.Z) <= halfHeight && ((p.X * p.X) + (p.Y * p.Y)) <= radius * radius);

    private static bool SampledInBox(Vector3 a, Vector3 b, Vector3 c, Vector3 half) =>
        Sample(a, b, c).Any(p => MathF.Abs(p.X) <= half.X && MathF.Abs(p.Y) <= half.Y && MathF.Abs(p.Z) <= half.Z);

    private static IEnumerable<Vector3> Sample(Vector3 a, Vector3 b, Vector3 c)
    {
        const int n = 400;
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; i + j <= n; j++)
            {
                yield return a + ((b - a) * (i / (float)n)) + ((c - a) * (j / (float)n));
            }
        }
    }

    private static Vector3 RandomPoint(Random random, float scale) =>
        new Vector3((2 * random.NextSingle()) - 1, (2 * random.NextSingle()) - 1, (2 * random.NextSingle()) - 1) * scale;

    private static Float3 F(Vector3 v) => Float3.From(v);
}
