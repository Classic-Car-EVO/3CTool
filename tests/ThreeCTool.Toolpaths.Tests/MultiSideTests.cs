using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

using Xunit;

namespace ThreeCTool.Toolpaths.Tests;

[Collection(ComputeGroup.Name)]
public sealed class MultiSideTests(ComputeFixture fixture)
{
    private ComputeContext Compute => fixture.Compute;

    public static TheoryData<StockFace, int> Turns()
    {
        var data = new TheoryData<StockFace, int>();
        foreach (var face in StockTurn.Faces)
        {
            foreach (var spin in StockTurn.Spins)
            {
                data.Add(face, spin);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void TurnedFrame_IsInvertibleAndMatchesItsMatrix(StockFace face, int spin)
    {
        var frame = new SetupFrame(SetupOrientation.ThreeAxis, 3, -2, 7, 0, face, spin);
        var p = new P3(1.25, -4.5, 9);
        var w = frame.PartToWcs(p, 0);
        var back = frame.WcsToPart(w, 0);
        var m = Vector3.Transform(new Vector3(1.25f, -4.5f, 9), frame.PartToWcsMatrix(0));

        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
        Assert.Equal(p.Z, back.Z, 9);
        Assert.Equal(w.X, m.X, 4);
        Assert.Equal(w.Y, m.Y, 4);
        Assert.Equal(w.Z, m.Z, 4);
    }

    [Fact]
    public void BottomFace_PointsTheToolAtThePartBottom()
    {
        var frame = new SetupFrame(SetupOrientation.ThreeAxis, 0, 0, 0, 0, StockFace.Bottom);
        var (_, axis) = frame.ToolInPart(new ToolpathMove(MoveKind.Cut, 0, 0, 0, 0));

        Assert.Equal(0, axis.X, 9);
        Assert.Equal(0, axis.Y, 9);
        Assert.Equal(-1, axis.Z, 9);
    }

    [Fact]
    public void CreateFrame_BottomFace_KeepsTheWcsAtTheSameMachinePoint()
    {
        var stock = Kit.BoxStock(new Vector3(0, 0, 0), new Vector3(100, 60, 20));
        var bounds = new BoundingBox(new Vector3(0, 0, 0), new Vector3(100, 60, 20));
        var frame = MachiningSetup.CreateFrame(new Setup { Face = StockFace.Bottom }, stock, bounds);

        // Turned over about X and centred on the jig: the old bottom centre is now the top centre, at the old top.
        AssertNear(new P3(0, 0, 0), frame.PartToWcs(new P3(50, 30, 0), 0));
        AssertNear(new P3(0, 0, -20), frame.PartToWcs(new P3(50, 30, 20), 0));
        AssertNear(new P3(50, 30, 0), frame.PartToWcs(new P3(100, 0, 0), 0));
    }

    [Fact]
    public void CreateFrame_FrontFace_SitsTheStockOnItsBackInTheJig()
    {
        var stock = Kit.BoxStock(new Vector3(0, 0, 0), new Vector3(100, 60, 20));
        var bounds = new BoundingBox(new Vector3(0, 0, 0), new Vector3(100, 60, 20));
        var frame = MachiningSetup.CreateFrame(new Setup { Face = StockFace.Front }, stock, bounds);

        // 60 mm tall now: the front face centre is 40 above the original top, straight above the original zero.
        Assert.Equal(0, frame.Spin);
        AssertNear(new P3(0, 0, 40), frame.PartToWcs(new P3(50, 0, 10), 0));
        AssertNear(new P3(0, 0, -20), frame.PartToWcs(new P3(50, 60, 10), 0));
    }

    [Fact]
    public void CreateFrame_SideFace_SpinsTheLongEdgeAlongX()
    {
        var stock = Kit.BoxStock(new Vector3(0, 0, 0), new Vector3(20, 100, 30));
        var bounds = new BoundingBox(new Vector3(0, 0, 0), new Vector3(20, 100, 30));
        var auto = MachiningSetup.CreateFrame(new Setup { Face = StockFace.Left }, stock, bounds);
        var fixedSpin = MachiningSetup.CreateFrame(new Setup { Face = StockFace.Left, Spin = 0 }, stock, bounds);

        Assert.Equal(90, auto.Spin);
        Assert.Equal(0, fixedSpin.Spin);
        var along = auto.DirectionToWcs(new P3(0, 1, 0), 0);
        Assert.Equal(1, Math.Abs(along.X), 9);
    }

    [Fact]
    public void ToolpathFile_RoundTripsATurnedFrame()
    {
        var toolpath = new Toolpath
        {
            OperationId = Guid.NewGuid(),
            Strategy = StrategyType.ZLevelRoughing,
            ToolId = Guid.NewGuid(),
            Frame = new SetupFrame(SetupOrientation.ThreeAxis, 1, 2, 3, 0, StockFace.Left, 270),
            Clearance = 5,
            Moves = [new(MoveKind.Rapid, 0, 0, 5, 0)],
            Warnings = [],
        };
        using var stream = new MemoryStream();
        ToolpathFile.Write(toolpath, stream);
        stream.Position = 0;

        Assert.Equal(toolpath.Frame, ToolpathFile.Read(stream).Frame);
    }

    [Fact]
    public void BottomSetup_StaysAboveItsDepthLimit()
    {
        var part = TestMeshes.Box(new Vector3(-10, -10, 0), new Vector3(10, 10, 20));
        var request = Kit.Request(Compute, StrategyType.ZLevelRoughing, Kit.Flat(6), part, Kit.BoxStock(new Vector3(-20, -20, 0), new Vector3(40, 40, 20)));
        request.Setup.Face = StockFace.Bottom;
        request = With(request, depthLimit: 8);
        var toolpath = ToolpathGenerator.Generate(request);

        Assert.Equal(StockFace.Bottom, toolpath.Frame.Face);
        var cuts = toolpath.Moves.Where(m => m.Kind != MoveKind.Rapid).ToList();
        Assert.NotEmpty(cuts);
        Assert.All(cuts, m => Assert.True(m.Z >= -8 - 1e-9, $"cut at Z {m.Z}"));
        Assert.True(cuts.Min(m => m.Z) < -6, "the roughing reaches down to the depth limit");

        // The cuts lie in the bottom half of the part frame.
        Assert.All(cuts, m => Assert.True(toolpath.Frame.ToolInPart(m).Tip.Z < 12.5, $"tip at part Z {toolpath.Frame.ToolInPart(m).Tip.Z}"));
    }

    [Fact]
    public void TabSolids_KeepAFrameAndBarsToThePart()
    {
        var part = TestMeshes.Box(new Vector3(-10, -10, 0), new Vector3(10, 10, 20));
        var stock = Kit.BoxStock(new Vector3(-30, -30, 0), new Vector3(60, 60, 20));
        var setup = new Setup { Name = "top", TabCount = 4, TabWidth = 6, TabThickness = 2, FrameWidth = 10 };
        var layout = TabSolids.Create(setup, 10, stock, part);

        Assert.NotNull(layout);
        Assert.Empty(layout.Warnings);
        var bounds = layout.Mesh.Bounds;
        Assert.Equal(-30, bounds.Min.X, 4);
        Assert.Equal(30, bounds.Max.Y, 4);
        Assert.Equal(0, bounds.Min.Z, 4);
        Assert.Equal(20, bounds.Max.Z, 4);

        // Frame ring 60² − 40², plus four 6 mm bars from 3 mm inside the part out to the frame.
        Assert.Equal(2000 + (4 * 6 * 13), Math.Abs(Clipper.Area(MeshSlicer.Slice(layout.Mesh, 10))), 1);
        Assert.Equal(2000, Math.Abs(Clipper.Area(MeshSlicer.Slice(layout.Mesh, 15))), 1);
    }

    [Fact]
    public void TabSolids_BelowThePart_Warns()
    {
        var part = TestMeshes.Box(new Vector3(-10, -10, 10), new Vector3(10, 10, 20));
        var stock = Kit.BoxStock(new Vector3(-30, -30, 0), new Vector3(60, 60, 20));
        var layout = TabSolids.Create(new Setup { Name = "top", TabCount = 2 }, 15, stock, part);

        Assert.NotNull(layout);
        Assert.Contains(layout.Warnings, w => w.Contains("no tabs", StringComparison.Ordinal));
        Assert.Null(TabSolids.Create(new Setup { Name = "none" }, 15, stock, part));
    }

    [Fact]
    public void Tabs_AreLeftStandingByTheSetupsOnTheirAxis()
    {
        var part = TestMeshes.Box(new Vector3(-10, -10, 0), new Vector3(10, 10, 20));
        var stock = Kit.BoxStock(new Vector3(-30, -30, 0), new Vector3(60, 60, 20));
        var tabSetup = new Setup { Name = "top", TabCount = 4, TabWidth = 6, TabThickness = 2, FrameWidth = 10 };
        var tabs = TabSolids.Create(tabSetup, 10, stock, part)!.Mesh;
        var request = Kit.Request(Compute, StrategyType.ZLevelRoughing, Kit.Flat(6), part, stock);
        request.Setup.Face = StockFace.Bottom;
        var toolpath = ToolpathGenerator.Generate(With(request, depthLimit: 10, tabs: tabs));

        // Cutting from below (part Z 0..10): no cut tip (radius 3) inside the stock reaches into the frame band or
        // up into a tab bar (part Z 9..11).
        foreach (var move in toolpath.Moves.Where(m => m.Kind != MoveKind.Rapid))
        {
            var tip = toolpath.Frame.ToolInPart(move).Tip;
            if (tip.Z <= 1e-6)
            {
                continue;
            }

            Assert.True(tip.Z <= 10 + 1e-6, $"cut at part Z {tip.Z} past the depth limit");
            Assert.True(Math.Max(Math.Abs(tip.X), Math.Abs(tip.Y)) < 20 - 3 + 1e-6, $"cut at ({tip.X}, {tip.Y}) in the frame");
            var onBar = Math.Min(Math.Abs(tip.X), Math.Abs(tip.Y)) < 3 + 3 - 1e-6;
            Assert.False(onBar && tip.Z > 9 + 1e-6, $"cut at ({tip.X}, {tip.Y}, {tip.Z}) into a tab");
        }
    }

    private static ToolpathRequest With(ToolpathRequest request, double? depthLimit = null, Mesh? tabs = null) => new()
    {
        Compute = request.Compute,
        Operation = request.Operation,
        Setup = request.Setup,
        Tool = request.Tool,
        Stock = request.Stock,
        Part = request.Part,
        DepthLimit = depthLimit,
        Tabs = tabs,
    };

    private static void AssertNear(P3 expected, P3 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }
}
