using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

using Xunit;

namespace ThreeCTool.Toolpaths.Tests;

[Collection(ComputeGroup.Name)]
public sealed class BasicsTests(ComputeFixture fixture)
{
    private ComputeContext Compute => fixture.Compute;

    [Fact]
    public void ToolpathFile_RoundTrips()
    {
        var toolpath = new Toolpath
        {
            OperationId = Guid.NewGuid(),
            Strategy = StrategyType.RotaryFinishing,
            ToolId = Guid.NewGuid(),
            Frame = new SetupFrame(SetupOrientation.SimultaneousRotary, 1.5, 0, 0, 0),
            Clearance = 18,
            Moves = [new(MoveKind.Rapid, 0, 0, 18, 0), new(MoveKind.Plunge, 1, 0, 10, 0), new(MoveKind.Cut, 2.25, 0, 9.5, 370.125)],
            Warnings = ["first", "second"],
        };
        using var stream = new MemoryStream();
        ToolpathFile.Write(toolpath, stream);
        stream.Position = 0;
        var read = ToolpathFile.Read(stream);

        Assert.Equal(toolpath.OperationId, read.OperationId);
        Assert.Equal(toolpath.Strategy, read.Strategy);
        Assert.Equal(toolpath.ToolId, read.ToolId);
        Assert.Equal(toolpath.Frame, read.Frame);
        Assert.Equal(toolpath.Clearance, read.Clearance);
        Assert.Equal(toolpath.Moves, read.Moves);
        Assert.Equal(toolpath.Warnings, read.Warnings);
    }

    [Theory]
    [InlineData(SetupOrientation.ThreeAxis, 0)]
    [InlineData(SetupOrientation.Indexed, 90)]
    [InlineData(SetupOrientation.SimultaneousRotary, 37)]
    public void SetupFrame_PartToWcs_IsInvertible(SetupOrientation orientation, double angle)
    {
        var frame = new SetupFrame(orientation, 3, -2, 7, angle);
        var p = new P3(1.25, -4.5, 9);
        var back = frame.WcsToPart(frame.PartToWcs(p, angle), angle);

        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
        Assert.Equal(p.Z, back.Z, 9);
    }

    [Fact]
    public void SetupFrame_RotaryFrame_KeepsTheAxisAtTheOrigin()
    {
        var setup = new Setup { Orientation = SetupOrientation.SimultaneousRotary, WcsOffsetY = 5, WcsOffsetZ = 5 };
        var stock = new Stock { Type = StockType.Cylinder, OriginX = 10, OriginY = 2, OriginZ = 3, Length = 50, Diameter = 20 };
        var bounds = new BoundingBox(new Vector3(10, -8, -7), new Vector3(60, 12, 13));
        var frame = MachiningSetup.CreateFrame(setup, stock, bounds);

        Assert.Equal(35, frame.OriginX, 9);
        Assert.Equal(2, frame.OriginY, 9);
        Assert.Equal(3, frame.OriginZ, 9);
    }

    [Fact]
    public void ToolpathSettings_Defaults_FollowTheStrategy()
    {
        var tool = Kit.Flat(6);
        tool.MaxDepthOfCut = 2;
        var tolerances = new Tolerances();
        var adaptive = ToolpathSettings.Resolve(new Operation { Strategy = StrategyType.AdaptiveRoughing }, tool, tolerances);
        var parallel = ToolpathSettings.Resolve(new Operation { Strategy = StrategyType.ParallelFinishing }, Kit.Ball(6), tolerances);
        var custom = ToolpathSettings.Resolve(new Operation { Strategy = StrategyType.ZLevelRoughing, Parameters = new() { Stepover = 1.5, StockToLeave = 0.3 } }, tool, tolerances);

        Assert.Equal(0.6, adaptive.Stepover, 9);
        Assert.Equal(2, adaptive.Stepdown, 9);
        Assert.Equal(tolerances.RoughingStockToLeave, adaptive.RadialStock);
        Assert.Equal(tolerances.FinishingStockToLeave, parallel.RadialStock);
        Assert.Equal(2 * Math.Sqrt((2 * 3 * tolerances.MaxScallopHeight) - (tolerances.MaxScallopHeight * tolerances.MaxScallopHeight)), parallel.Stepover, 6);
        Assert.Equal(1.5, custom.Stepover);
        Assert.Equal(0.3, custom.RadialStock);
    }

    [Fact]
    public void ToolAssembly_StockToLeave_GrowsTheCutter()
    {
        var ball = ToolAssembly.Create(Kit.Ball(6), null, []).WithStockToLeave(0.5, 0.5);
        var flat = ToolAssembly.Create(Kit.Flat(6), null, []).WithStockToLeave(0.5, 0.2);

        Assert.Equal(3.5, ball.Stages[0].Profile.Radius, 5);
        Assert.Equal(3.5, flat.Stages[0].Profile.Radius, 5);
        Assert.Equal(-0.2, flat.Stages[0].Offset, 9);
    }

    [Fact]
    public void FiberContours_AroundABox_AreTheCutterOffsetOutline()
    {
        var request = Kit.Request(Compute, StrategyType.Waterline, Kit.Flat(6), TestMeshes.Box(new Vector3(-10, -10, 0), new Vector3(10, 10, 10)), Kit.BoxStock(new Vector3(-20, -20, 0), new Vector3(40, 40, 10)));
        using var setup = MachiningSetup.Create(request);
        var tool = ToolAssembly.Create(request.Tool, null, []);
        var forbidden = FiberContours.Forbidden(tool, setup, -5, FiberContours.Spacing(3, 0.01));

        // Flat cutter: square grown by R with rounded corners.
        var expected = (26 * 26) - ((4 - Math.PI) * 9);
        Assert.Equal(expected, Polygons.Area(forbidden), 0);
        Assert.True(Polygons.Contains(forbidden, new PointD(12.9, 0)));
        Assert.False(Polygons.Contains(forbidden, new PointD(13.1, 0)));
    }

    [Fact]
    public void Builder_LinksClosePassesAndRetractsBetweenFarOnes()
    {
        var settings = ToolpathSettings.Resolve(new Operation { Strategy = StrategyType.ParallelFinishing }, Kit.Ball(6), new Tolerances());
        var builder = new ToolpathBuilder(settings, 3, 20, (p, kind) => new ToolpathMove(kind, p.X, p.Y, p.Z))
        {
            StayDownLink = (a, b) => P3.Distance(a, b) < 2 ? [] : null,
        };
        builder.AddPass(new CutPass([new(0, 0, 0), new(10, 0, 0)]));
        builder.AddPass(new CutPass([new(10, 1, 0), new(0, 1, 0)]));
        builder.AddPass(new CutPass([new(50, 50, 0), new(60, 50, 0)]));
        var moves = builder.Finish();

        // Start: rapid over, rapid down, plunge; one stay-down link; then retract + rapid over + rapid down to the far pass.
        var rapidsBeforeFar = moves.TakeWhile(m => m.X < 50).Count(m => m.Kind == MoveKind.Rapid);
        Assert.Equal(3, rapidsBeforeFar);
        Assert.Equal(20, moves.TakeWhile(m => m.X < 50).Last().Z);
        Assert.Contains(moves, m => m.Kind == MoveKind.Link || (m.Kind == MoveKind.Cut && m.X == 10 && m.Y == 1));
        Assert.Equal(20, moves[^1].Z);
        Assert.Contains(moves, m => m.Kind == MoveKind.Rapid && m.X == 50 && m.Y == 50);
    }

    [Fact]
    public void Builder_HelixEntry_DescendsAroundTheStartInsideTheRegion()
    {
        var settings = ToolpathSettings.Resolve(new Operation { Strategy = StrategyType.ZLevelRoughing }, Kit.Flat(6), new Tolerances());
        var builder = new ToolpathBuilder(settings, 3, 20, (p, kind) => new ToolpathMove(kind, p.X, p.Y, p.Z));
        var region = Polygons.Rectangle(-20, -20, 20, 20);
        builder.AddPass(new CutPass([new(0, 0, -5), new(10, 0, -5)]) { InMaterial = true, EntryTop = 0, Region = region });
        var moves = builder.Finish();
        var ramps = moves.Where(m => m.Kind == MoveKind.Ramp).ToList();

        Assert.Equal(0, builder.PlungeEntries);
        Assert.NotEmpty(ramps);
        Assert.All(ramps, m => Assert.InRange(Math.Sqrt((m.X * m.X) + (m.Y * m.Y)), 0, 2 * 3 * 0.75 + 1e-6));
        Assert.Equal(-5, ramps.Min(m => m.Z), 9);
    }

    [Fact]
    public void Builder_NoRoomForEntry_PlungesAndCountsIt()
    {
        var settings = ToolpathSettings.Resolve(new Operation { Strategy = StrategyType.ZLevelRoughing, Parameters = new() { Entry = EntryMode.Helix } }, Kit.Flat(6), new Tolerances());
        var builder = new ToolpathBuilder(settings, 3, 20, (p, kind) => new ToolpathMove(kind, p.X, p.Y, p.Z));
        var region = Polygons.Rectangle(-0.2, -0.2, 0.2, 0.2);
        builder.AddPass(new CutPass([new(0, 0, -5), new(0.1, 0, -5)]) { InMaterial = true, EntryTop = 0, Region = region });
        builder.Finish();

        Assert.Equal(1, builder.PlungeEntries);
    }

    [Fact]
    public void Generator_ReportsChecksAgainstTheMachine()
    {
        var machine = new Core.Machines.MachineProfile
        {
            Axes = [new() { Axis = Core.Machines.AxisName.X, MaxTravel = 5 }, new() { Axis = Core.Machines.AxisName.Z, MaxTravel = 500 }],
        };

        var request = Kit.Request(Compute, StrategyType.ZLevelRoughing, Kit.Flat(6), TestMeshes.Box(new Vector3(-10, -10, 0), new Vector3(10, 10, 10)), Kit.BoxStock(new Vector3(-15, -15, 0), new Vector3(30, 30, 12)));
        request = new ToolpathRequest
        {
            Compute = request.Compute,
            Operation = request.Operation,
            Setup = request.Setup,
            Tool = request.Tool,
            Stock = request.Stock,
            Part = request.Part,
            Machine = machine,
        };
        var toolpath = ToolpathGenerator.Generate(request);

        Assert.Contains(toolpath.Warnings, w => w.Contains("in X", StringComparison.Ordinal) && w.Contains("travel", StringComparison.Ordinal));
        Assert.DoesNotContain(toolpath.Warnings, w => w.Contains("in Z", StringComparison.Ordinal));
    }
}
