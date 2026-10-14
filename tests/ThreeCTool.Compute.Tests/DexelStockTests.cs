using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute.Tests;

public class DexelStockTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    private readonly ComputeContext _compute = fixture.Compute;

    private static BoundingBox Block(float x, float y, float z) => new(Vector3.Zero, new Vector3(x, y, z));

    [Fact]
    public void BoxStockIsFull()
    {
        using var stock = DexelStock.Box(_compute, Block(20, 10, 5), 0.5f);
        Assert.Equal(1000, stock.Volume(), 3);
        Assert.All(stock.Heightmap(), h => Assert.Equal(5, h));
        var x = stock.Read(DexelAxis.X);
        Assert.Equal(20, x.CountU);
        Assert.Equal(10, x.CountV);
        Assert.Equal([0f, 20f], x.Ray(3, 4).ToArray());
    }

    [Fact]
    public void FlatSlotMatchesAnalytic()
    {
        using var stock = DexelStock.Box(_compute, Block(40, 20, 10), 0.25f);
        stock.Remove(CutterProfile.Flat(6, 20), [new ToolMove(new Vector3(5, 10, 7), new Vector3(35, 10, 7))]);

        var z = stock.Read(DexelAxis.Z);
        var heights = stock.Heightmap();
        for (var iv = 0; iv < z.CountV; iv++)
        {
            for (var iu = 0; iu < z.CountU; iu++)
            {
                var c = z.RayCenter(iu, iv);
                var distance = DistanceToSegment(c, new Vector2(5, 10), new Vector2(35, 10));
                if (MathF.Abs(distance - 3) > 1e-3f)
                {
                    Assert.Equal(distance < 3 ? 7 : 10, heights[(iv * z.CountU) + iu], 4);
                }
            }
        }

        // X rays above the slot floor are cut through between the two end arcs.
        var x = stock.Read(DexelAxis.X);
        for (var iv = 0; iv < x.CountV; iv++)
        {
            for (var iu = 0; iu < x.CountU; iu++)
            {
                var c = x.RayCenter(iu, iv);
                var dy = c.X - 10;
                if (c.Y > 7 && MathF.Abs(dy) < 3)
                {
                    var half = MathF.Sqrt(9 - (dy * dy));
                    var ray = x.Ray(iu, iv).ToArray();
                    Assert.Equal(4, ray.Length);
                    Assert.Equal(5 - half, ray[1], 3);
                    Assert.Equal(35 + half, ray[2], 3);
                }
            }
        }

        var removed = 8000 - stock.Volume();
        var expected = ((30 * 6) + (MathF.PI * 9)) * 3;
        Assert.True(Math.Abs(removed - expected) / expected < 0.01, $"{removed} vs {expected}");
        Assert.Equal(0, stock.OverflowCount);
    }

    [Fact]
    public void BallGrooveMatchesAnalytic()
    {
        using var stock = DexelStock.Box(_compute, Block(40, 20, 10), 0.25f);
        stock.Remove(CutterProfile.Ball(6, 20), [new ToolMove(new Vector3(5, 10, 7), new Vector3(35, 10, 7))]);
        var z = stock.Read(DexelAxis.Z);
        var heights = stock.Heightmap();
        for (var iv = 0; iv < z.CountV; iv++)
        {
            for (var iu = 0; iu < z.CountU; iu++)
            {
                var c = z.RayCenter(iu, iv);
                var dy = c.Y - 10;
                if (c.X is > 6 and < 34 && MathF.Abs(dy) < 2.99f)
                {
                    Assert.Equal(10 - MathF.Sqrt(9 - (dy * dy)), heights[(iv * z.CountU) + iu], 3);
                }
            }
        }

        // Y rays (over Z, X) through the groove: the ball cross-section at height z.
        var y = stock.Read(DexelAxis.Y);
        for (var iv = 0; iv < y.CountV; iv++)
        {
            for (var iu = 0; iu < y.CountU; iu++)
            {
                var c = y.RayCenter(iu, iv);
                if (c.Y is > 6 and < 34 && c.X > 7.01f)
                {
                    var half = c.X >= 10 ? 3 : MathF.Sqrt(9 - ((10 - c.X) * (10 - c.X)));
                    var ray = y.Ray(iu, iv).ToArray();
                    Assert.Equal([0, 10 - half, 10 + half, 20], ray, new FloatComparer(2e-3f));
                }
            }
        }
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(1f, 0f, 0f)]
    public void FastPathsMatchGeneralSolver(float ax, float ay, float az)
    {
        var axis = new Vector3(ax, ay, az);
        var moves = RandomMoves(new Random(5), 8, axis);
        foreach (var cutter in Reference.Cutters)
        {
            using var fast = DexelStock.Box(_compute, Block(30, 30, 30), 0.75f);
            using var general = DexelStock.Box(_compute, Block(30, 30, 30), 0.75f);
            general.ForceGeneralSolver = true;
            var tall = cutter.WithHeight(12);
            fast.Remove(tall, moves);
            general.Remove(tall, moves);
            // The general solver inscribes bull-nose arcs with frustums (normal error ≤ 3.1e-4·rc). A ray that grazes the
            // swept surface right where the arc meets the flat bottom (vertical tangent) magnifies that error, so a few
            // such rays may differ; the removed volume must still agree closely.
            var bullNose = cutter.FlatRadius > 0 && cutter.CornerRadius > 0;
            var tolerance = bullNose ? 0.025f * cutter.CornerRadius : 2e-3f;
            Assert.Equal(fast.Volume(), general.Volume(), fast.Volume() * 1e-4);
            foreach (var dexelAxis in new[] { DexelAxis.Z, DexelAxis.X, DexelAxis.Y })
            {
                try
                {
                    DexelAssert.GridsClose(fast.Read(dexelAxis), general.Read(dexelAxis), tolerance, bullNose ? 3 : 0);
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    throw new Xunit.Sdk.XunitException($"{cutter}: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void ObliqueSweepMatchesBruteForce()
    {
        var axis = Vector3.Normalize(new Vector3(0.3f, -0.5f, 1));
        var from = new Vector3(8, 9, 12);
        var to = new Vector3(21, 18, 10);
        foreach (var cutter in Reference.Cutters)
        {
            var tall = cutter.WithHeight(8);
            using var stock = DexelStock.Box(_compute, Block(30, 30, 30), 1f);
            stock.Remove(tall, [new ToolMove(from, to, axis)]);
            foreach (var dexelAxis in new[] { DexelAxis.Z, DexelAxis.X, DexelAxis.Y })
            {
                var grid = stock.Read(dexelAxis);
                var checkedRays = 0;
                for (var iv = 0; iv < grid.CountV; iv += 3)
                {
                    for (var iu = 0; iu < grid.CountU; iu += 3)
                    {
                        var ray = grid.Ray(iu, iv).ToArray();
                        if (ray.Length == 4)
                        {
                            var (enter, exit) = BruteInterval(grid, iu, iv, 0.5f * (ray[1] + ray[2]), from, to, axis, tall);
                            Assert.True(MathF.Abs(enter - ray[1]) < 0.02f, $"{dexelAxis} ({iu},{iv}) enter {ray[1]} vs {enter}");
                            Assert.True(MathF.Abs(exit - ray[2]) < 0.02f, $"{dexelAxis} ({iu},{iv}) exit {ray[2]} vs {exit}");
                            checkedRays++;
                        }
                    }
                }

                Assert.True(checkedRays > 3, $"{dexelAxis}: only {checkedRays} rays checked");
            }
        }
    }

    [Fact]
    public void CylinderStockVolume()
    {
        using var stock = DexelStock.Cylinder(_compute, -20, 30, 12, 0.2f);
        var expected = MathF.PI * 144 * 50;
        Assert.True(Math.Abs(stock.Volume() - expected) / expected < 0.005, $"{stock.Volume()} vs {expected}");
        var x = stock.Read(DexelAxis.X);
        Assert.Equal([-20f, 30f], x.Ray(x.CountU / 2, x.CountV / 2).ToArray());
    }

    [Fact]
    public void MeshStockFromSphere()
    {
        using var mesh = new GpuMesh(_compute, TestMeshes.Sphere(10, 64, 128));
        using var stock = DexelStock.FromMesh(mesh, new BoundingBox(new Vector3(-11), new Vector3(11)), 0.25f);
        var expected = 4 / 3f * MathF.PI * 1000;
        Assert.True(Math.Abs(stock.Volume() - expected) / expected < 0.01, $"{stock.Volume()} vs {expected}");
        var z = stock.Read(DexelAxis.Z);
        var heights = stock.Heightmap();
        for (var iv = 0; iv < z.CountV; iv++)
        {
            for (var iu = 0; iu < z.CountU; iu++)
            {
                var c = z.RayCenter(iu, iv);
                var r2 = c.LengthSquared();
                if (r2 < 81)
                {
                    Assert.Equal(MathF.Sqrt(100 - r2), heights[(iv * z.CountU) + iu], 0.05f);
                }
            }
        }

        Assert.Equal(0, stock.OverflowCount);
    }

    [Fact]
    public void MeshStockHandlesRaysThroughEdges()
    {
        // Ray centres land exactly on the face diagonals of the triangulated box.
        using var mesh = new GpuMesh(_compute, TestMeshes.Box(Vector3.Zero, new Vector3(10, 10, 10)));
        using var stock = DexelStock.FromMesh(mesh, new BoundingBox(new Vector3(-1), new Vector3(11)), 0.5f);
        foreach (var axis in new[] { DexelAxis.Z, DexelAxis.X, DexelAxis.Y })
        {
            var grid = stock.Read(axis);
            for (var iv = 0; iv < grid.CountV; iv++)
            {
                for (var iu = 0; iu < grid.CountU; iu++)
                {
                    var c = grid.RayCenter(iu, iv);
                    var inside = c.X is > 0 and < 10 && c.Y is > 0 and < 10;
                    Assert.Equal(inside ? [0f, 10f] : [], grid.Ray(iu, iv).ToArray());
                }
            }
        }
    }

    [Fact]
    public void DepositBuildsBeadsAndLayers()
    {
        using var stock = DexelStock.Empty(_compute, Block(40, 20, 2), 0.05f);
        stock.Deposit([new ToolMove(new Vector3(5, 10, 0.2f), new Vector3(35, 10, 0.2f))], 0.4f, 0.2f);
        var expected = ((30 * 0.4f) + (MathF.PI * 0.04f)) * 0.2f;
        Assert.True(Math.Abs(stock.Volume() - expected) / expected < 0.03, $"{stock.Volume()} vs {expected}");

        stock.Deposit([new ToolMove(new Vector3(5, 10, 0.4f), new Vector3(35, 10, 0.4f))], 0.4f, 0.2f);
        var z = stock.Read(DexelAxis.Z);
        var ray = z.Ray(400, 200).ToArray(); // (20.025, 10.025)
        Assert.Equal([0f, 0.4f], ray, new FloatComparer(1e-5f));
    }

    [Fact]
    public void OverflowKeepsMaterial()
    {
        using var stock = DexelStock.Box(_compute, Block(40, 10, 10), 0.5f, capacity: 2);
        stock.MaxCapacity = 2;
        ToolMove[] plunges = [.. Enumerable.Range(0, 5).Select(i => new ToolMove(new Vector3(5 + (7 * i), 5, 2), new Vector3(5 + (7 * i), 5, 2)))];
        stock.Remove(CutterProfile.Flat(3, 20), plunges);
        Assert.True(stock.OverflowCount > 0);
        Assert.Equal(2, stock.Capacity);
        var x = stock.Read(DexelAxis.X);
        var ray = x.Ray(10, 10).ToArray(); // y = 5.25, z = 5.25
        Assert.Equal(4, ray.Length);
        Assert.True(ray[0] == 0 && ray[^1] == 40);
    }

    [Fact]
    public void OverflowGrowsCapacityAndCompletesRemoval()
    {
        using var stock = DexelStock.Box(_compute, Block(40, 10, 10), 0.5f, capacity: 2);
        var before = stock.Volume();
        ToolMove[] plunges = [.. Enumerable.Range(0, 5).Select(i => new ToolMove(new Vector3(5 + (7 * i), 5, 2), new Vector3(5 + (7 * i), 5, 2)))];
        var removed = new double[plunges.Length];
        stock.Remove(CutterProfile.Flat(3, 20), plunges, removed);
        Assert.Equal(0, stock.OverflowCount);
        Assert.Equal(8, stock.Capacity);
        var ray = stock.Read(DexelAxis.X).Ray(10, 10).ToArray(); // y = 5.25, z = 5.25: five holes, six pieces
        Assert.Equal(12, ray.Length);
        for (var i = 0; i < 5; i++)
        {
            Assert.InRange(ray[(2 * i) + 1], 5 + (7 * i) - 1.6f, 5 + (7 * i) - 1.3f);
        }

        Assert.Equal(before - stock.Volume(), removed.Sum(), 1e-3 * before);

        using var copy = stock.Clone();
        Assert.Equal(8, copy.Capacity);
        using var small = DexelStock.Box(_compute, Block(40, 10, 10), 0.5f, capacity: 2);
        small.CopyFrom(stock);
        Assert.Equal(8, small.Capacity);
        DexelAssert.GridsClose(stock.Read(DexelAxis.X), small.Read(DexelAxis.X), 0);
    }

    [Fact]
    public void DeviationFindsGouges()
    {
        using var part = new GpuMesh(_compute, TestMeshes.Box(Vector3.Zero, new Vector3(20, 20, 8)));
        using var stock = DexelStock.Box(_compute, Block(20, 20, 10), 0.5f);
        stock.Remove(CutterProfile.Flat(4, 10), [new ToolMove(new Vector3(-5, 10, 7), new Vector3(25, 10, 7))]);
        var samples = stock.Deviation(part);
        var gouges = samples.Where(s => s.Distance < -1e-3f).ToArray();
        Assert.NotEmpty(gouges);
        Assert.All(gouges, s => Assert.True(MathF.Abs(s.Point.Y - 10) <= 2.01f && s.Distance >= -1.001f));
        Assert.Contains(samples, s => s.Distance > 1.9f);
    }

    [Fact]
    public void RejectsCutterWithoutHeight()
    {
        using var stock = DexelStock.Box(_compute, Block(10, 10, 10), 1);
        Assert.Throws<ArgumentException>(() => stock.Remove(CutterProfile.Flat(6), [new ToolMove(Vector3.Zero, Vector3.One)]));
    }

    internal static ToolMove[] RandomMoves(Random random, int count, Vector3 axis)
    {
        var moves = new ToolMove[count];
        for (var i = 0; i < count; i++)
        {
            var from = new Vector3(3 + (random.NextSingle() * 24), 3 + (random.NextSingle() * 24), 3 + (random.NextSingle() * 24));
            var to = random.Next(4) == 0 ? from : new Vector3(3 + (random.NextSingle() * 24), 3 + (random.NextSingle() * 24), 3 + (random.NextSingle() * 24));
            moves[i] = new ToolMove(from, to, axis);
        }

        return moves;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + (ab * t));
    }

    private static (float Enter, float Exit) BruteInterval(DexelGrid grid, int iu, int iv, float inside, Vector3 from, Vector3 to, Vector3 axis, CutterProfile cutter)
    {
        // The swept volume is convex: bisect the boundary on each side of a point verified to be inside it.
        Assert.True(Inside(inside));
        var (a, b) = (0f, inside);
        for (var i = 0; i < 24; i++)
        {
            var m = 0.5f * (a + b);
            (a, b) = Inside(m) ? (a, m) : (m, b);
        }

        var enter = 0.5f * (a + b);
        (a, b) = (inside, 30f);
        for (var i = 0; i < 24; i++)
        {
            var m = 0.5f * (a + b);
            (a, b) = Inside(m) ? (m, b) : (a, m);
        }

        return (enter, 0.5f * (a + b));

        bool Inside(float w)
        {
            var p = grid.Point(iu, iv, w);
            for (var i = 0; i <= 2000; i++)
            {
                var q = p - Vector3.Lerp(from, to, i / 2000f);
                var h = Vector3.Dot(q, axis);
                var rho = (q - (axis * h)).Length();
                if (rho <= cutter.Radius && h >= cutter.HeightAt(rho) && h <= cutter.Height)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public void Clone_IsIndependent_AndCopyFromRestores()
    {
        using var stock = DexelStock.Box(_compute, Block(40, 20, 10), 0.5f);
        stock.Remove(CutterProfile.Flat(6, 20), [new ToolMove(new Vector3(5, 10, 7), new Vector3(35, 10, 7))]);
        using var copy = stock.Clone();
        var cut = stock.Volume();
        Assert.Equal(cut, copy.Volume(), 6);

        stock.Remove(CutterProfile.Flat(6, 20), [new ToolMove(new Vector3(5, 5, 4), new Vector3(35, 5, 4))]);
        Assert.True(stock.Volume() < cut - 100);
        Assert.Equal(cut, copy.Volume(), 6);

        stock.CopyFrom(copy);
        foreach (var axis in new[] { DexelAxis.X, DexelAxis.Y, DexelAxis.Z })
        {
            DexelAssert.GridsClose(copy.Read(axis), stock.Read(axis), 0);
        }

        using var other = DexelStock.Box(_compute, Block(40, 20, 11), 0.5f);
        Assert.Throws<ArgumentException>(() => stock.CopyFrom(other));
    }

    [Fact]
    public void SaveLoad_RoundTripsSegmentsAndOverflow()
    {
        using var stock = DexelStock.Box(_compute, Block(30, 20, 10), 0.5f, capacity: 2);
        stock.MaxCapacity = 2;
        stock.Remove(CutterProfile.Ball(4, 20), [new ToolMove(new Vector3(2, 10, 6), new Vector3(28, 10, 6))]);
        stock.Remove(CutterProfile.Flat(3, 20), [new ToolMove(new Vector3(15, 2, 3), new Vector3(15, 18, 3))]);
        using var buffer = new MemoryStream();
        stock.Save(buffer);
        buffer.Position = 0;

        using var loaded = DexelStock.Load(_compute, buffer);
        Assert.Equal(stock.Bounds, loaded.Bounds);
        Assert.Equal(stock.Resolution, loaded.Resolution);
        Assert.Equal(stock.Capacity, loaded.Capacity);
        Assert.Equal(stock.OverflowCount, loaded.OverflowCount);
        Assert.Equal(stock.Volume(), loaded.Volume(), 6);
        foreach (var axis in new[] { DexelAxis.X, DexelAxis.Y, DexelAxis.Z })
        {
            DexelAssert.GridsClose(stock.Read(axis), loaded.Read(axis), 0);
        }

        Assert.Throws<InvalidDataException>(() => DexelStock.Load(_compute, new MemoryStream(new byte[16])));
    }

    [Fact]
    public void Remove_MeasuresVolumePerMove()
    {
        using var stock = DexelStock.Box(_compute, Block(60, 30, 10), 0.2f);
        var cutter = CutterProfile.Flat(6, 20);
        var before = stock.Volume();
        ToolMove[] slot =
        [
            new(new Vector3(10, 10, 7), new Vector3(20, 10, 7)),
            new(new Vector3(20, 10, 7), new Vector3(40, 10, 7)),
            new(new Vector3(40, 10, 7), new Vector3(20, 10, 7)),
        ];
        var removed = new double[slot.Length];
        stock.Remove(cutter, slot, removed);

        Assert.Equal((Math.PI * 9 * 3) + (10 * 6 * 3), removed[0], 0.03 * removed[0]);
        Assert.Equal(20 * 6 * 3, removed[1], 0.02 * removed[1]);
        Assert.Equal(0, removed[2], 1e-9);
        Assert.Equal(before - stock.Volume(), removed.Sum(), 0.01 * removed.Sum());

        // Half-width pass beside the slot: steady state removes 3 mm × 3 mm per mm of travel (± one row of rays).
        ToolMove[] side =
        [
            new(new Vector3(10, 13, 7), new Vector3(20, 13, 7)),
            new(new Vector3(20, 13, 7), new Vector3(40, 13, 7)),
        ];
        removed = new double[side.Length];
        stock.Remove(cutter, side, removed);
        Assert.Equal(20 * 3 * 3, removed[1], 0.2 * 20 * 3);

        Assert.Throws<ArgumentException>(() => stock.Remove(cutter, side, new double[1]));
    }

    internal sealed class FloatComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => MathF.Abs(x - y) <= tolerance;

        public int GetHashCode(float obj) => 0;
    }
}
