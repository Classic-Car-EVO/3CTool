using System.Diagnostics;
using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Geometry;

using Xunit.Abstractions;

namespace ThreeCTool.Gpu.Tests;

[Trait("Category", "Gpu")]
public class DexelStockGpuTests(GpuAndCpuFixture fixture, ITestOutputHelper output) : IClassFixture<GpuAndCpuFixture>
{
    private static readonly BoundingBox Block = new(Vector3.Zero, new Vector3(30, 30, 30));

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0.3f, -0.5f, 1f)]
    public void GpuMatchesCpu(float ax, float ay, float az)
    {
        var axis = Vector3.Normalize(new Vector3(ax, ay, az));
        var random = new Random(9);
        var moves = new ToolMove[20];
        for (var i = 0; i < moves.Length; i++)
        {
            var from = new Vector3(random.NextSingle() * 30, random.NextSingle() * 30, 5 + (random.NextSingle() * 25));
            var to = new Vector3(random.NextSingle() * 30, random.NextSingle() * 30, 5 + (random.NextSingle() * 25));
            moves[i] = new ToolMove(from, to, axis);
        }

        using var gpu = DexelStock.Box(fixture.Gpu, Block, 0.5f);
        using var cpu = DexelStock.Box(fixture.Cpu, Block, 0.5f);
        var cutter = CutterProfile.BullNose(6, 1, 15);
        gpu.Remove(cutter, moves);
        cpu.Remove(cutter, moves);
        gpu.Deposit(moves.AsSpan(0, 5), 0.6f, 0.3f);
        cpu.Deposit(moves.AsSpan(0, 5), 0.6f, 0.3f);
        foreach (var dexelAxis in new[] { DexelAxis.Z, DexelAxis.X, DexelAxis.Y })
        {
            DexelAssert.GridsClose(cpu.Read(dexelAxis), gpu.Read(dexelAxis), 2e-3f);
        }

        Assert.Equal(cpu.Volume(), gpu.Volume(), 0);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    public void RasterFinishingIsFast(float tiltDegrees)
    {
        // Ball-end raster over a wavy surface on 100 × 100 × 40 stock at 0.1 mm: 200 lines of 1 mm moves.
        var (sin, cos) = MathF.SinCos(tiltDegrees * MathF.PI / 180);
        var axis = new Vector3(0, -sin, cos);
        var moves = new List<ToolMove>();
        for (var line = 0; line < 200; line++)
        {
            var y = line * 0.5f;
            for (var x = 0; x < 100; x++)
            {
                moves.Add(new ToolMove(Tip(x, y), Tip(x + 1, y), axis));
            }
        }

        using var stock = DexelStock.Box(fixture.Gpu, new BoundingBox(Vector3.Zero, new Vector3(100, 100, 40)), 0.1f);
        stock.Remove(CutterProfile.Ball(6, 20), moves.Take(10).ToArray());
        var watch = Stopwatch.StartNew();
        stock.Remove(CutterProfile.Ball(6, 20), moves.ToArray());
        watch.Stop();
        output.WriteLine($"{fixture.Gpu.Device.Name}: {moves.Count} moves, tilt {tiltDegrees}°, {watch.ElapsedMilliseconds} ms, {stock.OverflowCount} overflowed rays");

        // Side rays grazing the scallop cusps can need more than the default capacity; that must stay rare.
        var rays = (1000 * 1000) + (2 * 1000 * 400);
        Assert.True(stock.OverflowCount <= rays / 1000, $"{stock.OverflowCount} overflowed rays");

        static Vector3 Tip(float x, float y) => new(x, y, 30 + (3 * MathF.Sin(x / 8) * MathF.Cos(y / 11)));
    }
}
