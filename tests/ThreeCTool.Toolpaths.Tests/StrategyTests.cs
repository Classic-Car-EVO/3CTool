using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

using Xunit;
using Xunit.Abstractions;

namespace ThreeCTool.Toolpaths.Tests;

[Collection(ComputeGroup.Name)]
public sealed class StrategyTests(ComputeFixture fixture, ITestOutputHelper output)
{
    private const double RoughingAllowance = 0.07;
    private const double FinishingAllowance = 0.03;

    private ComputeContext Compute => fixture.Compute;

    private static Stock SteppedStock => Kit.BoxStock(new Vector3(-20, -20, 0), new Vector3(40, 40, 15));

    private static Stock SphereStock => Kit.BoxStock(new Vector3(-15, -15, -10), new Vector3(30, 30, 20));

    private static Mesh Sphere => TestMeshes.Sphere(10, 16, 32);

    /// <summary>Stepped base with an L-shaped tower: one concave vertical corner at (6, 0).</summary>
    private static Mesh LPart => Mesh.Merge(
        [
            TestMeshes.Box(new Vector3(-15, -15, 0), new Vector3(15, 15, 5)),
            TestMeshes.Box(new Vector3(-6, -6, 5), new Vector3(6, 6, 12)),
            TestMeshes.Box(new Vector3(6, -6, 5), new Vector3(12, 0, 12)),
        ],
        "L");

    [Theory]
    [InlineData(ClearingPattern.Offset)]
    [InlineData(ClearingPattern.Raster)]
    public void ZLevelRoughing_ClearsDownToTheBaseWithoutGouging(ClearingPattern pattern)
    {
        var request = Kit.Request(Compute, StrategyType.ZLevelRoughing, Kit.Flat(6), Kit.Stepped(), SteppedStock, p => p.Pattern = pattern);
        var toolpath = Generate(request);

        Assert.True(Kit.CutMoves(toolpath) > 0);
        AssertGouge(request, toolpath, RoughingAllowance);

        // Base top at part z 5 = WCS −10, plus 0.5 axial stock: a flat level must clear there.
        Assert.Contains(toolpath.Moves, m => m.Kind == MoveKind.Cut && Math.Abs(m.Z - -9.5) < 1e-6);
        Assert.DoesNotContain(toolpath.Moves, m => m.Kind != MoveKind.Rapid && m.Z < -15 - 1e-6);
    }

    [Fact]
    public void AdaptiveRoughing_DoesNotGouge()
    {
        var request = Kit.Request(Compute, StrategyType.AdaptiveRoughing, Kit.Flat(6), Kit.Stepped(), SteppedStock);
        var toolpath = Generate(request);

        Assert.True(Kit.CutMoves(toolpath) > 0);
        AssertGouge(request, toolpath, RoughingAllowance);
        Assert.Contains(toolpath.Moves, m => m.Kind == MoveKind.Cut && Math.Abs(m.Z - -9.5) < 1e-6);
    }

    [Fact]
    public void Waterline_FollowsTheWalls()
    {
        var request = Kit.Request(Compute, StrategyType.Waterline, Kit.Ball(6), Kit.Stepped(), SteppedStock);
        var toolpath = Generate(request);

        Assert.True(Kit.CutMoves(toolpath) > 0);
        AssertGouge(request, toolpath, FinishingAllowance);
    }

    [Theory]
    [InlineData(StrategyType.ParallelFinishing)]
    [InlineData(StrategyType.Scallop)]
    public void SurfaceFinishing_OnASphere_StaysOnTheSurface(StrategyType strategy)
    {
        var request = Kit.Request(Compute, strategy, Kit.Ball(6), Sphere, SphereStock);
        var toolpath = Generate(request);

        Assert.True(Kit.CutMoves(toolpath) > 0);
        AssertGouge(request, toolpath, FinishingAllowance);

        // The top of the sphere (WCS z 0) is reached.
        Assert.Contains(toolpath.Moves, m => m.Kind == MoveKind.Cut && m.Z > -0.05);
    }

    [Fact]
    public void Pencil_TracesTheConcaveFootOfTheTower()
    {
        var request = Kit.Request(Compute, StrategyType.Pencil, Kit.Ball(6), Kit.Stepped(), SteppedStock);
        var toolpath = Generate(request);
        var cuts = toolpath.Moves.Where(m => m.Kind == MoveKind.Cut).ToList();

        Assert.NotEmpty(cuts);
        AssertGouge(request, toolpath, FinishingAllowance);

        // Ball tip in the corner between the tower wall (±6) and the base top: z −10, 9 mm off centre.
        Assert.All(cuts, m => Assert.InRange(m.Z, -10.2, -9.5));
        Assert.All(cuts, m => Assert.InRange(Math.Max(Math.Abs(m.X), Math.Abs(m.Y)), 8, 9.6));
    }

    [Fact]
    public void RestMachining_OnlyCutsWhatTheLargerToolLeft()
    {
        var big = Kit.Flat(12);
        var small = Kit.Flat(4);
        var first = Kit.Request(Compute, StrategyType.ZLevelRoughing, big, LPart, SteppedStock, p => p.StockToLeave = 0.5);
        var previous = Generate(first);
        var full = Generate(Kit.Request(Compute, StrategyType.ZLevelRoughing, small, LPart, SteppedStock, p => p.StockToLeave = 0.5));
        var rest = Kit.Request(Compute, StrategyType.RestMachining, small, LPart, SteppedStock, p => p.StockToLeave = 0.5);
        rest = new ToolpathRequest
        {
            Compute = rest.Compute,
            Operation = rest.Operation,
            Setup = rest.Setup,
            Tool = rest.Tool,
            Stock = rest.Stock,
            Part = rest.Part,
            PreviousCuts = [new PreviousCut(previous, big)],
        };
        var toolpath = Generate(rest);

        output.WriteLine($"rest {toolpath.CutLength:0} mm vs full {full.CutLength:0} mm");
        DumpLevels(previous);
        DumpLevels(toolpath);
        Assert.True(Kit.CutMoves(toolpath) > 0);
        AssertGouge(rest, toolpath, RoughingAllowance);
        Assert.True(toolpath.CutLength < 0.3 * full.CutLength);

        // Rest material sits in the concave corner at (6, 0).
        var cuts = toolpath.Moves.Where(m => m.Kind == MoveKind.Cut).ToList();
        Assert.All(cuts, m => Assert.True(Vector2.Distance(new Vector2((float)m.X, (float)m.Y), new Vector2(6, 0)) < 10, $"{m}"));
    }

    [Fact]
    public void IndexedSetup_RunsThreeAxisStrategiesAtTheIndexAngle()
    {
        var request = Kit.Request(Compute, StrategyType.ZLevelRoughing, Kit.Flat(6), Kit.Stepped(), SteppedStock, orientation: SetupOrientation.Indexed, indexAngle: 90);
        var toolpath = Generate(request);

        Assert.Equal(90, toolpath.Frame.BaseAngle);
        Assert.True(Kit.CutMoves(toolpath) > 0);
        Assert.All(toolpath.Moves, m => Assert.Equal(90, m.A));
        AssertGouge(request, toolpath, RoughingAllowance);
    }

    [Theory]
    [InlineData(StrategyType.RotaryRoughing, RotaryPattern.Spiral)]
    [InlineData(StrategyType.RotaryRoughing, RotaryPattern.Lines)]
    [InlineData(StrategyType.RotaryFinishing, RotaryPattern.Spiral)]
    [InlineData(StrategyType.RotaryFinishing, RotaryPattern.Rings)]
    [InlineData(StrategyType.RotaryFinishing, RotaryPattern.Lines)]
    public void Rotary_WrapsAroundThePartWithoutGouging(StrategyType strategy, RotaryPattern pattern)
    {
        var stock = new Stock { Type = StockType.Cylinder, OriginX = 0, Length = 40, Diameter = 30 };
        var part = TestMeshes.Box(new Vector3(5, -8, -8), new Vector3(35, 8, 8));
        var tool = strategy == StrategyType.RotaryRoughing ? Kit.Flat(6) : Kit.Ball(6);
        var request = Kit.Request(Compute, strategy, tool, part, stock, p => p.RotaryPattern = pattern, SetupOrientation.SimultaneousRotary);
        var toolpath = Generate(request);

        Assert.True(toolpath.UsesRotary);
        Assert.True(Kit.CutMoves(toolpath) > 0);
        Assert.All(toolpath.Moves, m => Assert.Equal(0, m.Y));
        AssertGouge(request, toolpath, (strategy == StrategyType.RotaryRoughing ? RoughingAllowance : FinishingAllowance));
        var bounds = toolpath.Bounds;
        Assert.True(bounds.MaxA - bounds.MinA >= 300);

        // Nothing cuts outside the stock length or deeper than the box's inscribed radius allows.
        Assert.All(toolpath.Moves.Where(m => m.Kind != MoveKind.Rapid), m => Assert.InRange(m.X, -20 - 1e-6, 20 + 1e-6));
        Assert.All(toolpath.Moves.Where(m => m.Kind != MoveKind.Rapid), m => Assert.True(m.Z > 7.9, $"{m}"));
    }

    [Fact]
    public void RotaryStrategy_OnAThreeAxisSetup_Throws()
    {
        var request = Kit.Request(Compute, StrategyType.RotaryFinishing, Kit.Ball(6), Sphere, SphereStock);

        Assert.Throws<InvalidOperationException>(() => ToolpathGenerator.Generate(request));
    }

    private void DumpLevels(Toolpath toolpath)
    {
        output.WriteLine($"{toolpath.Strategy} levels:");
        foreach (var group in toolpath.Moves.Where(m => m.Kind == MoveKind.Cut).GroupBy(m => Math.Round(m.Z, 2)))
        {
            output.WriteLine($"  z {group.Key}: {group.Count()} cuts, x {group.Min(m => m.X):0.#}..{group.Max(m => m.X):0.#}, y {group.Min(m => m.Y):0.#}..{group.Max(m => m.Y):0.#}");
        }
    }

    private void AssertGouge(ToolpathRequest request, Toolpath toolpath, double allowance)
    {
        var gouge = Kit.MaxGouge(request, toolpath, allowance);
        output.WriteLine($"max gouge {gouge:0.0000} mm beyond the allowance {allowance}");
        Assert.True(gouge < 1e-3, $"gouge {gouge:0.0000} at {Kit.WorstAt}");
    }

    private Toolpath Generate(ToolpathRequest request)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var toolpath = ToolpathGenerator.Generate(request);
        output.WriteLine($"{request.Operation.Strategy}: {toolpath.MoveCount} moves, cut {toolpath.CutLength:0} mm, {started.ElapsedMilliseconds} ms");
        foreach (var warning in toolpath.Warnings)
        {
            output.WriteLine($"  warning: {warning}");
        }

        return toolpath;
    }
}
