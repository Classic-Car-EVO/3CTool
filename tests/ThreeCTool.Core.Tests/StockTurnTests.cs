using ThreeCTool.Core.Projects;

using Xunit;

namespace ThreeCTool.Core.Tests;

public sealed class StockTurnTests
{
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
    public void Rotate_PutsTheFaceUpAndIsUndone(StockFace face, int spin)
    {
        (double X, double Y, double Z) normal = face switch
        {
            StockFace.Top => (0, 0, 1),
            StockFace.Bottom => (0, 0, -1),
            StockFace.Front => (0, -1, 0),
            StockFace.Back => (0, 1, 0),
            StockFace.Left => (-1, 0, 0),
            _ => (1, 0, 0),
        };
        var up = StockTurn.Rotate(face, spin, normal);
        var back = StockTurn.Unrotate(face, spin, StockTurn.Rotate(face, spin, (1.5, -2, 3)));

        Assert.Equal((0.0, 0.0, 1.0), up);
        Assert.Equal((1.5, -2.0, 3.0), back);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(89, 90)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(1080, 0)]
    public void NormalizeSpin_SnapsToQuarterTurns(int spin, int expected) => Assert.Equal(expected, StockTurn.NormalizeSpin(spin));

    [Theory]
    [InlineData(StockFace.Top, null, 0)]
    [InlineData(StockFace.Bottom, null, 0)]
    [InlineData(StockFace.Front, null, 90)]
    [InlineData(StockFace.Front, 0, 0)]
    [InlineData(StockFace.Left, null, 90)]
    [InlineData(StockFace.Left, 180, 180)]
    public void ResolveSpin_PutsTheLongestEdgeAlongX(StockFace face, int? spin, int expected) =>
        Assert.Equal(expected, StockTurn.ResolveSpin(face, spin, 20, 100, 30));

    [Fact]
    public void Placement_SetsTheTurnedStockOnTheJig()
    {
        // 100 × 60 × 20 stock turned onto its front face: 100 × 20 × 60 on the table.
        var center = StockTurn.Placement(StockFace.Front, 0, TurnAnchor.Center, (0, 0, 0), (100, 60, 20));
        var corner = StockTurn.Placement(StockFace.Front, 0, TurnAnchor.FrontLeft, (0, 0, 0), (100, 60, 20));

        Assert.Equal((100.0, 20.0, 60.0), StockTurn.TurnedSize(StockFace.Front, 0, 100, 60, 20));
        Assert.Equal((0.0, 20.0, 60.0), center);
        Assert.Equal((0.0, 0.0, 60.0), corner);
    }

    [Fact]
    public void DepthLimit_IsHalfTheStockWhenTheOppositeFaceIsMachined()
    {
        var stock = new Stock { Type = StockType.Box, SizeX = 100, SizeY = 60, SizeZ = 20 };
        var top = Machined(new Setup { Name = "top" });
        var bottom = Machined(new Setup { Name = "bottom", Face = StockFace.Bottom });
        var front = Machined(new Setup { Name = "front", Face = StockFace.Front, DepthLimit = 12 });

        Assert.Null(StockTurn.DepthLimit(top, [top, front], stock));
        Assert.Equal(10, StockTurn.DepthLimit(top, [top, bottom, front], stock));
        Assert.Equal(10, StockTurn.DepthLimit(bottom, [top, bottom], stock));
        Assert.Equal(12, StockTurn.DepthLimit(front, [top, front], stock));
        bottom.Operations[0].Enabled = false;
        Assert.Null(StockTurn.DepthLimit(top, [top, bottom], stock));
    }

    [Fact]
    public void Problems_NameTurnsAndTabsThatCannotWork()
    {
        var box = new Stock { Type = StockType.Box, SizeX = 100, SizeY = 60, SizeZ = 20 };
        var round = new Stock { Type = StockType.Cylinder, Length = 100, Diameter = 30 };
        var turned = Machined(new Setup { Name = "under", Face = StockFace.Bottom });
        var tabs = Machined(new Setup { Name = "tabbed", TabCount = 4 });
        var thin = Machined(new Setup { Name = "thin", TabCount = 2, TabWidth = 0, DepthLimit = 5 });

        Assert.Empty(StockTurn.Problems(box, [turned, tabs]));
        Assert.Contains(StockTurn.Problems(round, [turned]), p => p.Contains("only a box stock", StringComparison.Ordinal));
        Assert.Contains(StockTurn.Problems(box, [tabs]), p => p.Contains("no depth limit", StringComparison.Ordinal));
        Assert.Contains(StockTurn.Problems(box, [thin]), p => p.Contains("above zero", StringComparison.Ordinal));
    }

    [Fact]
    public void Instruction_DescribesTheTurn()
    {
        Assert.Equal(
            "bottom face up, original front face towards +Y back, original right face towards +X right",
            StockTurn.Instruction(StockFace.Bottom, 0));
        Assert.Equal(
            "left face up, original top face towards +Y back, original front face towards +X right",
            StockTurn.Instruction(StockFace.Left, 90));
        Assert.True(StockTurn.SameAxis(StockFace.Front, StockFace.Back));
        Assert.False(StockTurn.SameAxis(StockFace.Front, StockFace.Top));
    }

    private static Setup Machined(Setup setup)
    {
        setup.Operations = [new Operation { Name = "op", Enabled = true }];
        return setup;
    }
}
