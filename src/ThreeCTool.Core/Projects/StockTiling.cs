using System.Globalization;

namespace ThreeCTool.Core.Projects;

public enum StockAxis
{
    X,
    Y,
    Z,
}

/// <summary>
/// One piece of a box stock that is split at seams. <see cref="Number"/> is 1-based; 0 stands for the whole stock when
/// it is not split. Bounds are in part coordinates.
/// </summary>
public sealed record StockPiece(int Number, int Column, int Row, int Layer, double MinX, double MinY, double MinZ, double SizeX, double SizeY, double SizeZ)
{
    public double MaxX => MinX + SizeX;

    public double MaxY => MinY + SizeY;

    public double MaxZ => MinZ + SizeZ;

    public string Label => Number == 0 ? "Stock" : string.Create(CultureInfo.InvariantCulture, $"Piece {Number}");

    /// <summary>Where the piece sits: column (X), row (Y) and layer (Z, from the bottom).</summary>
    public string Position => string.Create(CultureInfo.InvariantCulture, $"column {Column}, row {Row}, layer {Layer}");

    /// <summary>The piece as a box stock of its own.</summary>
    public Stock ToStock() => new()
    {
        Type = StockType.Box,
        OriginX = MinX,
        OriginY = MinY,
        OriginZ = MinZ,
        SizeX = SizeX,
        SizeY = SizeY,
        SizeZ = SizeZ,
    };
}

/// <summary>
/// Splits a box stock into standard-size pieces joined by butt joints (X/Y) or glued layers (Z), like a slicer spreading
/// a large print over several build plates. Every piece is machined on its own with the same setups.
/// </summary>
public static class StockTiling
{
    /// <summary>Smallest piece length along any axis (mm).</summary>
    public const double MinPiece = 1;

    private const double Tolerance = 1e-6;

    public static IReadOnlyList<StockAxis> Axes { get; } = [StockAxis.X, StockAxis.Y, StockAxis.Z];

    /// <summary>The stock is a box with at least one seam.</summary>
    public static bool IsTiled(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        return stock.Type == StockType.Box && stock.SeamsX.Count + stock.SeamsY.Count + stock.SeamsZ.Count > 0;
    }

    /// <summary>The stock is a box made of a standard piece size.</summary>
    public static bool HasPieceSize(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        return stock.Type == StockType.Box && stock.PieceSizeX > 0 && stock.PieceSizeY > 0 && stock.PieceSizeZ > 0;
    }

    public static double Length(Stock stock, StockAxis axis) => axis switch
    {
        StockAxis.X => stock.SizeX,
        StockAxis.Y => stock.SizeY,
        _ => stock.SizeZ,
    };

    public static double PieceLength(Stock stock, StockAxis axis) => axis switch
    {
        StockAxis.X => stock.PieceSizeX,
        StockAxis.Y => stock.PieceSizeY,
        _ => stock.PieceSizeZ,
    };

    public static List<double> Seams(Stock stock, StockAxis axis) => axis switch
    {
        StockAxis.X => stock.SeamsX,
        StockAxis.Y => stock.SeamsY,
        _ => stock.SeamsZ,
    };

    /// <summary>Number of pieces needed to cover <paramref name="length"/>.</summary>
    public static int PieceCount(double length, double piece) =>
        piece <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling((length - Tolerance) / piece));

    /// <summary>Seams splitting <paramref name="length"/> into the fewest equal pieces no longer than <paramref name="piece"/>.</summary>
    public static List<double> EvenSeams(double length, double piece)
    {
        var count = PieceCount(length, piece);
        return [.. Enumerable.Range(1, count - 1).Select(k => length * k / count)];
    }

    /// <summary>Removes the standard size and all seams: the stock is one free-size block again.</summary>
    public static void Clear(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        stock.PieceName = null;
        stock.PieceSizeX = stock.PieceSizeY = stock.PieceSizeZ = 0;
        stock.SeamsX = [];
        stock.SeamsY = [];
        stock.SeamsZ = [];
    }

    /// <summary>Places the seams at equal spacing on every axis.</summary>
    public static void ResetSeams(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        foreach (var axis in Axes)
        {
            ResetSeams(stock, axis);
        }
    }

    public static void ResetSeams(Stock stock, StockAxis axis)
    {
        ArgumentNullException.ThrowIfNull(stock);
        var seams = HasPieceSize(stock) ? EvenSeams(Length(stock, axis), PieceLength(stock, axis)) : [];
        switch (axis)
        {
            case StockAxis.X:
                stock.SeamsX = seams;
                break;
            case StockAxis.Y:
                stock.SeamsY = seams;
                break;
            default:
                stock.SeamsZ = seams;
                break;
        }
    }

    /// <summary>
    /// Call after the stock size or shape changed: keeps the seams of every axis that still splits it into valid pieces
    /// and spaces the others evenly again. Non-box stock loses its standard size.
    /// </summary>
    public static void Update(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (stock.Type != StockType.Box)
        {
            if (stock.PieceName is not null || stock.PieceSizeX > 0 || IsTiledIgnoringType(stock))
            {
                Clear(stock);
            }

            return;
        }

        foreach (var axis in Axes)
        {
            if (!HasPieceSize(stock) || AxisProblem(stock, axis) is not null)
            {
                ResetSeams(stock, axis);
            }
        }
    }

    /// <summary>
    /// Uses the standard size <paramref name="sizeX"/> × <paramref name="sizeY"/> × <paramref name="sizeZ"/> and makes the
    /// stock whole pieces around <paramref name="part"/> (min/max corners): <paramref name="margin"/> on every side in X/Y and
    /// above the part; the part stands on the bottom layer. Without a part the current stock centre and top are kept.
    /// Seams are spaced evenly.
    /// </summary>
    public static void Fit(Stock stock, string name, double sizeX, double sizeY, double sizeZ, (double X, double Y, double Z, double MaxX, double MaxY, double MaxZ)? part, double margin)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (!(sizeX > 0 && sizeY > 0 && sizeZ > 0))
        {
            throw new ArgumentException("A standard size needs positive dimensions.");
        }

        if (stock.Type != StockType.Box)
        {
            stock.Type = StockType.Box;
            stock.SizeX = sizeX;
            stock.SizeY = sizeY;
            stock.SizeZ = sizeZ;
        }

        var box = part ?? (stock.OriginX, stock.OriginY, stock.OriginZ, stock.OriginX + stock.SizeX, stock.OriginY + stock.SizeY, stock.OriginZ + stock.SizeZ);
        var marginXy = part is null ? 0 : margin;
        var marginTop = part is null ? 0 : margin;
        stock.PieceName = name;
        stock.PieceSizeX = sizeX;
        stock.PieceSizeY = sizeY;
        stock.PieceSizeZ = sizeZ;
        stock.SizeX = PieceCount(box.MaxX - box.X + (2 * marginXy), sizeX) * sizeX;
        stock.SizeY = PieceCount(box.MaxY - box.Y + (2 * marginXy), sizeY) * sizeY;
        stock.SizeZ = PieceCount(box.MaxZ - box.Z + marginTop, sizeZ) * sizeZ;
        stock.OriginX = ((box.X + box.MaxX) / 2) - (stock.SizeX / 2);
        stock.OriginY = ((box.Y + box.MaxY) / 2) - (stock.SizeY / 2);
        stock.OriginZ = box.MaxZ + marginTop - stock.SizeZ;
        ResetSeams(stock);
    }

    /// <summary>
    /// Moves seam <paramref name="index"/> of <paramref name="axis"/> towards <paramref name="position"/> (from the stock
    /// origin), clamped so both neighbouring pieces stay at least <see cref="MinPiece"/> and at most the standard size.
    /// Returns the position used.
    /// </summary>
    public static double MoveSeam(Stock stock, StockAxis axis, int index, double position)
    {
        ArgumentNullException.ThrowIfNull(stock);
        var seams = Seams(stock, axis);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, seams.Count);
        var length = Length(stock, axis);
        var piece = HasPieceSize(stock) ? PieceLength(stock, axis) : double.PositiveInfinity;
        var previous = index == 0 ? 0 : seams[index - 1];
        var next = index == seams.Count - 1 ? length : seams[index + 1];
        var low = Math.Max(previous + MinPiece, next - piece);
        var high = Math.Min(next - MinPiece, previous + piece);
        var clamped = low <= high ? Math.Clamp(position, low, high) : seams[index];
        seams[index] = clamped;
        return clamped;
    }

    /// <summary>Why the seams cannot be used (one message per axis), or empty when the pieces are valid.</summary>
    public static IReadOnlyList<string> Problems(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (stock.Type != StockType.Box)
        {
            return IsTiledIgnoringType(stock) ? ["Only box stock can be split into pieces."] : [];
        }

        return [.. Axes.Select(a => AxisProblem(stock, a)).OfType<string>()];
    }

    /// <summary>
    /// The pieces in machining order: bottom layer first, then front to back (Y), then left to right (X). A stock that is
    /// not split gives one piece numbered 0 covering the whole stock.
    /// </summary>
    public static IReadOnlyList<StockPiece> Pieces(Stock stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (!IsTiled(stock))
        {
            return [new StockPiece(0, 1, 1, 1, stock.OriginX, stock.OriginY, stock.OriginZ, stock.SizeX, stock.SizeY, stock.SizeZ)];
        }

        var xs = Bounds(stock, StockAxis.X);
        var ys = Bounds(stock, StockAxis.Y);
        var zs = Bounds(stock, StockAxis.Z);
        var pieces = new List<StockPiece>();
        for (var layer = 0; layer < zs.Count - 1; layer++)
        {
            for (var row = 0; row < ys.Count - 1; row++)
            {
                for (var column = 0; column < xs.Count - 1; column++)
                {
                    pieces.Add(new StockPiece(
                        pieces.Count + 1,
                        column + 1,
                        row + 1,
                        layer + 1,
                        stock.OriginX + xs[column],
                        stock.OriginY + ys[row],
                        stock.OriginZ + zs[layer],
                        xs[column + 1] - xs[column],
                        ys[row + 1] - ys[row],
                        zs[layer + 1] - zs[layer]));
                }
            }
        }

        return pieces;
    }

    /// <summary>Piece boundaries along <paramref name="axis"/> from the stock origin: 0, the seams, the stock length.</summary>
    public static IReadOnlyList<double> Bounds(Stock stock, StockAxis axis)
    {
        ArgumentNullException.ThrowIfNull(stock);
        return [0, .. Seams(stock, axis), Length(stock, axis)];
    }

    private static bool IsTiledIgnoringType(Stock stock) => stock.SeamsX.Count + stock.SeamsY.Count + stock.SeamsZ.Count > 0;

    /// <summary>Why the seams of <paramref name="axis"/> cannot be used, or null when they split the stock into valid pieces.</summary>
    public static string? AxisProblem(Stock stock, StockAxis axis)
    {
        ArgumentNullException.ThrowIfNull(stock);
        var bounds = Bounds(stock, axis);
        var piece = HasPieceSize(stock) ? PieceLength(stock, axis) : double.PositiveInfinity;
        for (var i = 1; i < bounds.Count; i++)
        {
            var length = bounds[i] - bounds[i - 1];
            if (!double.IsFinite(bounds[i]) || length < MinPiece - Tolerance)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{axis} seams must be ascending, inside the stock and at least {MinPiece:0.##} mm apart.");
            }

            if (length > piece + Tolerance)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{axis}: a piece is {length:0.##} mm long but the standard size is only {piece:0.##} mm; move the seams or add one.");
            }
        }

        return null;
    }
}
