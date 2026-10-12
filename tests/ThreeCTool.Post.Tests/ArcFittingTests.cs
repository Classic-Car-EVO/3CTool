using ThreeCTool.Core.Machines;
using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Post.Tests;

public sealed class ArcFittingTests
{
    private const double Rounding = 2e-4;

    private readonly FluidNcMillingPost post = new();

    public static TheoryData<int> Planes => [17, 18, 19];

    [Theory]
    [MemberData(nameof(Planes))]
    public void Post_HelixInEachPlane_IsFittedWithArcsWithinTolerance(int plane)
    {
        // Two turns of radius 10 in the plane, dropping 1 mm per turn along the plane normal, as 2° chords.
        var (first, second, normal) = MotionSegment.PlaneAxes(plane);
        var points = new List<P3>();
        for (var i = 0; i <= 360; i++)
        {
            var angle = -i * 2 * Math.PI / 180;
            var p = new double[3];
            p[first] = 10 * Math.Cos(angle);
            p[second] = 10 * Math.Sin(angle);
            p[normal] = -1 - (i / 180.0);
            points.Add(new P3(p[0], p[1], p[2]));
        }

        var (_, path) = PostAndReconstruct(points);

        Assert.True(path.Count(s => s.IsArc) is >= 3 and <= 4, $"{path.Count(s => s.IsArc)} arcs");
        Assert.All(path.Where(s => s.IsArc), s => Assert.Equal(plane, s.Plane));
        Assert.All(path.Where(s => s.IsArc), s => Assert.Equal(MotionMode.ArcClockwise, s.Mode));
        AssertWithin(points, path, 0.005 + Rounding);
    }

    [Fact]
    public void Post_MixedLinesAndArcs_ReproducesThePath()
    {
        // Line, quarter circle (counter-clockwise), line, then a coarse polygon that must stay linear.
        var points = new List<P3> { new(0, 0, -1), new(20, 0, -1) };
        for (var i = 1; i <= 30; i++)
        {
            var angle = (-Math.PI / 2) + (i * Math.PI / 60);
            points.Add(new P3(20 + (5 * Math.Cos(angle)), 5 + (5 * Math.Sin(angle)), -1));
        }

        points.Add(new P3(25, 30, -1));
        points.Add(new P3(10, 40, -1));
        points.Add(new P3(0, 30, -1));
        points.Add(new P3(-5, 10, -1));

        var (output, path) = PostAndReconstruct(points);

        var arc = Assert.Single(path, s => s.IsArc);
        Assert.Equal(MotionMode.ArcCounterClockwise, arc.Mode);
        Assert.Equal(Math.PI / 2, arc.Sweep, 3);
        Assert.Contains(output.Lines, l => l.StartsWith("G3 ", StringComparison.Ordinal));
        AssertWithin(points, path, 0.005 + Rounding);
    }

    [Fact]
    public void Post_NoisyCircle_FallsBackToLinesWhereTheToleranceIsExceeded()
    {
        var points = new List<P3>();
        for (var i = 0; i <= 60; i++)
        {
            var angle = i * Math.PI / 60;
            var radius = 10 + (i % 7 == 3 ? 0.02 : 0);
            points.Add(new P3(radius * Math.Cos(angle), radius * Math.Sin(angle), -1));
        }

        var (_, path) = PostAndReconstruct(points);

        Assert.Contains(path, s => !s.IsArc);
        AssertWithin(points, path, 0.005 + Rounding);
    }

    [Fact]
    public void Post_TolerancesAndSwitch_AreHonoured()
    {
        var points = new List<P3>();
        for (var i = 0; i <= 60; i++)
        {
            var angle = i * Math.PI / 60;
            points.Add(new P3(10 * Math.Cos(angle), 10 * Math.Sin(angle), -1));
        }

        // 3° chords of radius 10 sag 0.0034 mm: fitted at 0.005 but not when the project asks for 0.001.
        var project = new Core.Projects.Project { Name = "Tight" };
        project.Tolerances.ArcFittingTolerance = 0.001;
        Assert.Contains(PostAndReconstruct(points).Path, s => s.IsArc);
        Assert.DoesNotContain(PostAndReconstruct(points, project: project).Path, s => s.IsArc);

        var machine = PostFixture.Machine();
        machine.ArcFitting = false;
        Assert.DoesNotContain(PostAndReconstruct(points, machine).Path, s => s.IsArc);
    }

    [Fact]
    public void Post_LongArc_IsSplitBelowTheMaximumSweep()
    {
        var points = new List<P3>();
        for (var i = 0; i <= 360; i++)
        {
            var angle = i * Math.PI / 180;
            points.Add(new P3(30 * Math.Cos(angle), 30 * Math.Sin(angle), -1));
        }

        var (_, path) = PostAndReconstruct(points);

        Assert.All(path.Where(s => s.IsArc), s => Assert.True(Math.Abs(s.Sweep) <= (1.5 * Math.PI) + 1e-9));
        Assert.Equal(2 * Math.PI, path.Where(s => s.IsArc).Sum(s => s.Sweep), 6);
        AssertWithin(points, path, 0.005 + Rounding);
    }

    [Fact]
    public void Post_ArcRadii_MatchAtBothEndsAfterRounding()
    {
        // Irregular spacing and an off-grid centre: start and end radii must agree as the controller computes them.
        var points = new List<P3>();
        var random = new Random(7);
        var angle = 0.0;
        while (angle < 4)
        {
            points.Add(new P3(3.21987 + (17.3 * Math.Cos(angle)), -8.7654 + (17.3 * Math.Sin(angle)), -2.5));
            angle += 0.01 + (random.NextDouble() * 0.02);
        }

        var (_, path) = PostAndReconstruct(points);

        Assert.Contains(path, s => s.IsArc);
        foreach (var arc in path.Where(s => s.IsArc))
        {
            var start = Math.Sqrt(Math.Pow(arc.Start[0] - arc.Center![0], 2) + Math.Pow(arc.Start[1] - arc.Center[1], 2));
            var end = Math.Sqrt(Math.Pow(arc.End[0] - arc.Center[0], 2) + Math.Pow(arc.End[1] - arc.Center[1], 2));
            Assert.True(Math.Abs(start - end) < 0.002, $"radius {start} vs {end}");
        }

        AssertWithin(points, path, 0.005 + Rounding);
    }

    /// <summary>Posts a single cut through <paramref name="points"/> and interprets the result back into motion.</summary>
    private (PostOutput Output, List<MotionSegment> Path) PostAndReconstruct(
        List<P3> points, MachineProfile? machine = null, Core.Projects.Project? project = null)
    {
        var moves = new List<ToolpathMove> { new(MoveKind.Rapid, points[0].X, points[0].Y, 5) };
        moves.Add(new ToolpathMove(MoveKind.Plunge, points[0].X, points[0].Y, points[0].Z));
        moves.AddRange(points.Skip(1).Select(p => new ToolpathMove(MoveKind.Cut, p.X, p.Y, p.Z)));
        moves.Add(new ToolpathMove(MoveKind.Rapid, points[^1].X, points[^1].Y, 5));
        var output = post.Post(PostFixture.Request(moves, machine: machine, project: project));

        var interpreter = new GcodeInterpreter();
        var path = new List<MotionSegment>();
        var cutting = false;
        for (var i = 0; i < output.Lines.Count; i++)
        {
            var segment = interpreter.Apply(GcodeBlock.Parse(output.Lines[i]), i + 1);
            cutting |= output.Lines[i].StartsWith("G1 Z", StringComparison.Ordinal);
            if (cutting && segment is { Mode: not MotionMode.Rapid })
            {
                path.Add(segment);
            }

            if (cutting && segment is { Mode: MotionMode.Rapid })
            {
                break;
            }
        }

        // Drop the plunge.
        path.RemoveAt(0);
        return (output, path);
    }

    /// <summary>Every input point lies on the output path and every output sample lies on the input polyline.</summary>
    private static void AssertWithin(List<P3> points, List<MotionSegment> path, double tolerance)
    {
        var sampled = path.Select(Sample).ToList();
        foreach (var point in points)
        {
            var distance = sampled.Min(s => PolylineDistance(point, s));
            Assert.True(distance <= tolerance, $"input point {point} is {distance} from the output path");
        }

        foreach (var sample in sampled.SelectMany(s => s))
        {
            var distance = PolylineDistance(sample, points);
            Assert.True(distance <= tolerance, $"output sample {sample} is {distance} from the input path");
        }
    }

    /// <summary>Points along a move at most 0.05 mm apart.</summary>
    private static List<P3> Sample(MotionSegment segment)
    {
        var count = Math.Max(1, (int)Math.Ceiling(segment.Length / 0.05));
        return [.. Enumerable.Range(0, count + 1).Select(k => segment.PointAt(k / (double)count)).Select(p => new P3(p[0], p[1], p[2]))];
    }

    private static double PolylineDistance(P3 point, List<P3> polyline)
    {
        var best = double.MaxValue;
        for (var i = 1; i < polyline.Count; i++)
        {
            var (a, b) = (polyline[i - 1], polyline[i]);
            var ab = b - a;
            var lengthSquared = (ab.X * ab.X) + (ab.Y * ab.Y) + (ab.Z * ab.Z);
            var t = lengthSquared == 0 ? 0 : Math.Clamp((((point.X - a.X) * ab.X) + ((point.Y - a.Y) * ab.Y) + ((point.Z - a.Z) * ab.Z)) / lengthSquared, 0, 1);
            best = Math.Min(best, P3.Distance(point, P3.Lerp(a, b, t)));
        }

        return best;
    }
}
