using ThreeCTool.Core.Machines;

using Xunit;

namespace ThreeCTool.Core.Tests;

public sealed class MotionPlannerTests
{
    private static MachineProfile Machine(int plannerBlocks = 16) => new()
    {
        Name = "test",
        JunctionDeviation = 0.01,
        PlannerBlocks = plannerBlocks,
        Axes =
        [
            new AxisConfig { Axis = AxisName.X, MaxRatePerMinute = 1500, AccelerationPerSecond2 = 25 },
            new AxisConfig { Axis = AxisName.Y, MaxRatePerMinute = 1500, AccelerationPerSecond2 = 25 },
            new AxisConfig { Axis = AxisName.Z, MaxRatePerMinute = 1000, AccelerationPerSecond2 = 25 },
            new AxisConfig { Axis = AxisName.A, MaxRatePerMinute = 1000, AccelerationPerSecond2 = 25 },
        ],
    };

    private static readonly MotionPosition Origin = new(0, 0, 0, 0);

    [Fact]
    public void Trapezoid_MatchesAnalyticProfiles()
    {
        // Accelerate 1 s / 12.5 mm, cruise 975 mm at 25 mm/s, decelerate 1 s.
        Assert.Equal(41, MotionPlanner.Trapezoid(1000, 0, 0, 25, 25), 9);

        // Too short to reach the nominal speed: triangle with peak sqrt(a·L).
        Assert.Equal(2 * Math.Sqrt(250) / 25, MotionPlanner.Trapezoid(10, 0, 0, 25, 25), 9);

        // Entering and leaving at full speed is pure cruise.
        Assert.Equal(4, MotionPlanner.Trapezoid(100, 25, 25, 25, 25), 9);
    }

    [Fact]
    public void Durations_UseAxisRatesForRapidsAndProgrammedFeed()
    {
        var planner = new MotionPlanner(Machine());

        var rapidX = planner.Durations(Origin, [new MotionBlock(1000, 0, 0, 0, 0, Rapid: true)]);
        Assert.Equal(41, rapidX[0], 9);

        // Z is limited to 1000 mm/min.
        var rapidZ = planner.Durations(Origin, [new MotionBlock(0, 0, -600, 0, 0, Rapid: true)]);
        Assert.Equal(MotionPlanner.Trapezoid(600, 0, 0, 1000.0 / 60, 25), rapidZ[0], 9);

        // 600 mm/min = 10 mm/s: 0.4 s up, 96 mm cruise, 0.4 s down.
        var feed = planner.Durations(Origin, [new MotionBlock(100, 0, 0, 0, 600)]);
        Assert.Equal(10.4, feed[0], 9);

        // G93: F6 means the block takes 1/6 min, i.e. the same 10 mm/s.
        var inverse = planner.Durations(Origin, [new MotionBlock(100, 0, 0, 0, 6, InverseTime: true)]);
        Assert.Equal(10.4, inverse[0], 9);

        // A feed above the axis rate is clamped to it.
        var fast = planner.Durations(Origin, [new MotionBlock(1000, 0, 0, 0, 99999)]);
        Assert.Equal(41, fast[0], 9);
    }

    [Fact]
    public void Durations_ZeroLengthBlocksTakeNoTime_AndMissingAxesUseFallbacks()
    {
        var planner = new MotionPlanner(Machine());
        var result = planner.Durations(Origin, [new MotionBlock(0, 0, 0, 0, 600), new MotionBlock(100, 0, 0, 0, 600), new MotionBlock(100, 0, 0, 0, 600)]);
        Assert.Equal(0, result[0]);
        Assert.Equal(10.4, result[1], 9);
        Assert.Equal(0, result[2]);

        var bare = new MotionPlanner(new MachineProfile { Name = "bare" });
        var rapid = bare.Durations(Origin, [new MotionBlock(1000, 0, 0, 0, 0, Rapid: true)]);
        Assert.Equal(MotionPlanner.Trapezoid(1000, 0, 0, MotionPlanner.FallbackRate / 60, MotionPlanner.FallbackAcceleration), rapid[0], 9);
    }

    [Fact]
    public void Durations_CollinearBlocksDoNotSlowDown()
    {
        var planner = new MotionPlanner(Machine());
        var split = planner.Durations(Origin, [new MotionBlock(500, 0, 0, 0, 0, Rapid: true), new MotionBlock(1000, 0, 0, 0, 0, Rapid: true)]);
        Assert.Equal(41, split.Sum(), 9);
    }

    [Fact]
    public void Durations_CornerSpeedFollowsJunctionDeviation()
    {
        var planner = new MotionPlanner(Machine());
        var corner = planner.Durations(Origin, [new MotionBlock(100, 0, 0, 0, 0, Rapid: true), new MotionBlock(100, 100, 0, 0, 0, Rapid: true)]);

        // 90° corner: sin(θ/2) = √½; the junction direction (−1, 1)/√2 allows 25·√2 mm/s².
        var sinHalf = Math.Sqrt(0.5);
        var junction = Math.Sqrt(25 * Math.Sqrt(2) * 0.01 * sinHalf / (1 - sinHalf));
        Assert.Equal(MotionPlanner.Trapezoid(100, 0, junction, 25, 25), corner[0], 9);
        Assert.Equal(MotionPlanner.Trapezoid(100, junction, 0, 25, 25), corner[1], 9);

        // Slower than a straight line, faster than stopping at the corner.
        Assert.True(corner.Sum() > MotionPlanner.Trapezoid(200, 0, 0, 25, 25));
        Assert.True(corner.Sum() < 2 * MotionPlanner.Trapezoid(100, 0, 0, 25, 25));

        // Reversing direction forces a stop.
        var reverse = planner.Durations(Origin, [new MotionBlock(100, 0, 0, 0, 0, Rapid: true), new MotionBlock(0, 0, 0, 0, 0, Rapid: true)]);
        Assert.Equal(2 * MotionPlanner.Trapezoid(100, 0, 0, 25, 25), reverse.Sum(), 9);
    }

    [Fact]
    public void Durations_LookAheadWindowLimitsSpeedOnShortBlocks()
    {
        MotionBlock[] blocks = [.. Enumerable.Range(1, 100).Select(i => new MotionBlock(i, 0, 0, 0, 0, Rapid: true))];

        // 16 blocks of 1 mm give more than the 12.5 mm needed to stop from 25 mm/s: same as one 100 mm block.
        var deep = new MotionPlanner(Machine(16)).Durations(Origin, blocks);
        Assert.Equal(MotionPlanner.Trapezoid(100, 0, 0, 25, 25), deep.Sum(), 6);

        // With two blocks the machine must be able to stop within the next 1 mm block: it enters and leaves every
        // block at √(2·25·1) mm/s and peaks at √(3·25·1) mm/s in between.
        var shallow = new MotionPlanner(Machine(2)).Durations(Origin, blocks);
        var steady = 2 * (Math.Sqrt(75) - Math.Sqrt(50)) / 25;
        Assert.Equal(steady, shallow[50], 9);
        Assert.InRange(shallow.Sum(), 100 * steady, (100 * steady) + 1);
    }
}
