using ThreeCTool.Core.Projects;

using Xunit;

namespace ThreeCTool.Core.Tests;

public sealed class StockTilingTests
{
    [Theory]
    [InlineData(600, 600, 0)]
    [InlineData(599, 600, 0)]
    [InlineData(601, 600, 1)]
    [InlineData(1200, 600, 1)]
    [InlineData(1250, 600, 2)]
    public void EvenSeams_UseTheFewestEqualPieces(double length, double piece, int seams)
    {
        var result = StockTiling.EvenSeams(length, piece);

        Assert.Equal(seams, result.Count);
        var bounds = new List<double> { 0 };
        bounds.AddRange(result);
        bounds.Add(length);
        var lengths = bounds.Zip(bounds.Skip(1), (a, b) => b - a).ToList();
        Assert.All(lengths, l => Assert.Equal(length / (seams + 1), l, 9));
        Assert.All(lengths, l => Assert.True(l <= piece + 1e-9));
    }

    [Fact]
    public void Fit_MakesWholePiecesAroundThePart()
    {
        var stock = new Stock { Type = StockType.Box };

        StockTiling.Fit(stock, "MDF 600x400x18", 600, 400, 18, (-500, -100, 0, 500, 100, 30), 5);

        Assert.Equal("MDF 600x400x18", stock.PieceName);
        Assert.Equal(1200, stock.SizeX);
        Assert.Equal(400, stock.SizeY);
        Assert.Equal(36, stock.SizeZ);
        Assert.Equal(-600, stock.OriginX);
        Assert.Equal(-200, stock.OriginY);
        Assert.Equal(35, stock.OriginZ + stock.SizeZ);
        Assert.Equal([600.0], stock.SeamsX);
        Assert.Empty(stock.SeamsY);
        Assert.Equal([18.0], stock.SeamsZ);
        Assert.True(StockTiling.IsTiled(stock));
        Assert.Empty(StockTiling.Problems(stock));
    }

    [Fact]
    public void Fit_PartWithinOnePiece_IsNotTiled()
    {
        var stock = new Stock { Type = StockType.Box };

        StockTiling.Fit(stock, "Board", 300, 200, 25, (0, 0, 0, 100, 100, 20), 2);

        Assert.False(StockTiling.IsTiled(stock));
        Assert.True(StockTiling.HasPieceSize(stock));
        Assert.Equal((300.0, 200.0, 25.0), (stock.SizeX, stock.SizeY, stock.SizeZ));
        var piece = Assert.Single(StockTiling.Pieces(stock));
        Assert.Equal(0, piece.Number);
    }

    [Fact]
    public void Pieces_AreOrderedByLayerRowColumn()
    {
        var stock = new Stock
        {
            Type = StockType.Box,
            OriginX = 10,
            OriginY = 20,
            OriginZ = -30,
            SizeX = 100,
            SizeY = 50,
            SizeZ = 30,
            PieceSizeX = 60,
            PieceSizeY = 30,
            PieceSizeZ = 20,
            SeamsX = [40],
            SeamsY = [25],
            SeamsZ = [15],
        };

        var pieces = StockTiling.Pieces(stock);

        Assert.Equal(8, pieces.Count);
        Assert.Equal(Enumerable.Range(1, 8), pieces.Select(p => p.Number));
        Assert.Equal(new StockPiece(1, 1, 1, 1, 10, 20, -30, 40, 25, 15), pieces[0]);
        Assert.Equal(new StockPiece(2, 2, 1, 1, 50, 20, -30, 60, 25, 15), pieces[1]);
        Assert.Equal(new StockPiece(3, 1, 2, 1, 10, 45, -30, 40, 25, 15), pieces[2]);
        Assert.Equal(new StockPiece(8, 2, 2, 2, 50, 45, -15, 60, 25, 15), pieces[7]);
        Assert.Equal(stock.SizeX * stock.SizeY * stock.SizeZ, pieces.Sum(p => p.SizeX * p.SizeY * p.SizeZ), 6);
    }

    [Fact]
    public void MoveSeam_IsClampedToTheStandardSize()
    {
        var stock = new Stock { Type = StockType.Box, SizeX = 1500, SizeY = 100, SizeZ = 10, PieceSizeX = 600, PieceSizeY = 100, PieceSizeZ = 10 };
        StockTiling.ResetSeams(stock);
        Assert.Equal([500.0, 1000.0], stock.SeamsX);

        Assert.Equal(600, StockTiling.MoveSeam(stock, StockAxis.X, 0, 700));
        Assert.Equal(400, StockTiling.MoveSeam(stock, StockAxis.X, 0, 100));
        Assert.Equal(900, StockTiling.MoveSeam(stock, StockAxis.X, 1, 800));
        Assert.Equal([400.0, 900.0], stock.SeamsX);
        Assert.Empty(StockTiling.Problems(stock));
    }

    [Fact]
    public void Problems_ReportPiecesLargerThanTheStandardSize()
    {
        var stock = new Stock
        {
            Type = StockType.Box,
            SizeX = 1200,
            SizeY = 100,
            SizeZ = 10,
            PieceSizeX = 600,
            PieceSizeY = 100,
            PieceSizeZ = 10,
            SeamsX = [500],
        };

        var problem = Assert.Single(StockTiling.Problems(stock));
        Assert.Contains("700", problem, StringComparison.Ordinal);

        stock.SeamsX = [300, 200];
        Assert.Contains("ascending", Assert.Single(StockTiling.Problems(stock)), StringComparison.Ordinal);
    }

    [Fact]
    public void Update_KeepsValidSeamsAndRespacesInvalidOnes()
    {
        var stock = new Stock { Type = StockType.Box, SizeX = 1100, SizeY = 400, SizeZ = 18, PieceSizeX = 600, PieceSizeY = 400, PieceSizeZ = 18, SeamsX = [520] };

        StockTiling.Update(stock);
        Assert.Equal([520.0], stock.SeamsX);

        stock.SizeX = 1300;
        StockTiling.Update(stock);
        Assert.Equal(3, StockTiling.Pieces(stock).Count);

        stock.Type = StockType.Cylinder;
        StockTiling.Update(stock);
        Assert.False(StockTiling.HasPieceSize(stock));
        Assert.Null(stock.PieceName);
        Assert.Empty(stock.SeamsX);
    }
}
