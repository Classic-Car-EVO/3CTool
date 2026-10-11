using System.Numerics;

using ThreeCTool.Compute;

namespace ThreeCTool.Compute.Tests;

public class EngagementTests(CpuComputeFixture fixture) : IClassFixture<CpuComputeFixture>
{
    private const float Resolution = 0.02f;
    private const float Radius = 3;
    private static readonly float SampleRadius = Radius + (0.5f * Resolution);

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.5f)]
    [InlineData(3f)]
    [InlineData(4.5f)]
    [InlineData(6f)]
    public void SideMillingMatchesAnalyticAngle(float radialDepth)
    {
        // Material below y = 20; the cutter edge reaches radialDepth into it.
        using var map = EngagementMap.FromFunction(fixture.Compute, Vector2.Zero, Resolution, 2000, 2000, (_, y) => y < 20);
        Vector2[] centers = [new(20, 20 + Radius - radialDepth), new(7.3f, 20 + Radius - radialDepth)];
        var expected = 2 * MathF.Acos(Math.Clamp((Radius - radialDepth) / SampleRadius, -1, 1));
        foreach (var angle in map.Engagement(centers, Radius))
        {
            Assert.InRange(angle, expected - 0.03f, expected + 0.03f);
        }
    }

    [Fact]
    public void SlotEndIsHalfCircle()
    {
        using var map = EngagementMap.FromFunction(fixture.Compute, Vector2.Zero, Resolution, 2000, 2000, (_, _) => true);
        map.Remove([new(5, 20), new(20, 20)], Radius);

        // The slot walls are tangent to the cutter, so a circle just outside it sees a sliver of them as well.
        var angle = map.Engagement([new(20, 20)], Radius)[0];
        Assert.InRange(angle, MathF.PI - 0.03f, MathF.PI + (2 * MathF.Acos(Radius / SampleRadius)) + 0.03f);
        Assert.Equal(0, map.Engagement([new(12, 20)], 2.5f)[0]);
        Assert.Equal(2 * MathF.PI, map.Engagement([new(30, 30)], Radius)[0], 1e-5f);
    }

    [Fact]
    public void RemovedAreaMatchesStadiumAndIsNotCountedTwice()
    {
        using var map = EngagementMap.FromFunction(fixture.Compute, Vector2.Zero, Resolution, 2000, 2000, (_, _) => true);
        var area = map.Remove([new(10, 10), new(30, 25)], Radius);
        var expected = (2 * Radius * 25) + (MathF.PI * Radius * Radius);
        Assert.Equal(expected, area, expected * 0.003);
        Assert.Equal(0, map.Remove([new(10, 10), new(30, 25)], Radius));

        var cleared = map.Read().Count(m => !m) * (double)Resolution * Resolution;
        Assert.Equal(area, cleared, 1e-6);
    }

    [Fact]
    public void PolylineRemovalMatchesBruteForce()
    {
        const int count = 300;
        const float resolution = 0.1f;
        Vector2[] path = [new(3, 3), new(25, 4), new(26, 26), new(4.5f, 20), new(15, 12)];
        using var map = EngagementMap.FromFunction(fixture.Compute, Vector2.Zero, resolution, count, count, (x, y) => x + y > 4);
        var before = map.Read();
        var area = map.Remove(path, 2.2f);
        var after = map.Read();

        var removed = 0;
        for (var iy = 0; iy < count; iy++)
        {
            for (var ix = 0; ix < count; ix++)
            {
                var p = new Vector2((ix + 0.5f) * resolution, (iy + 0.5f) * resolution);
                var swept = false;
                for (var s = 0; s + 1 < path.Length; s++)
                {
                    swept |= SegmentDistance(p, path[s], path[s + 1]) <= 2.2f;
                }

                var index = (iy * count) + ix;
                if (swept && before[index])
                {
                    removed++;
                }

                // Cells within float noise of the cutter edge may go either way.
                var margin = MathF.Abs(MinDistance(p, path) - 2.2f) < 1e-4f;
                if (!margin)
                {
                    Assert.Equal(before[index] && !swept, after[index]);
                }
            }
        }

        Assert.Equal(removed * (double)resolution * resolution, area, 0.05);
    }

    private static float MinDistance(Vector2 p, Vector2[] path)
    {
        var best = float.MaxValue;
        for (var s = 0; s + 1 < path.Length; s++)
        {
            best = MathF.Min(best, SegmentDistance(p, path[s], path[s + 1]));
        }

        return best;
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + (t * ab));
    }
}
