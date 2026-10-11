using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace ThreeCTool.Geometry;

/// <summary>
/// Uniform XY grid of triangle references in CSR layout (<see cref="CellStart"/> / <see cref="TriangleIndices"/>),
/// used by drop-cutter style queries. Flat arrays for GPU upload.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Flat arrays are passed directly to GPU kernels.")]
public sealed class XyGrid
{
    private const int MaxCells = 16 * 1024 * 1024;

    public XyGrid(Mesh mesh, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);

        var bounds = mesh.Bounds;
        Origin = new Vector2(bounds.Min.X, bounds.Min.Y);
        var size = bounds.Size;

        // Grow cells if the requested size would exceed the cell budget.
        while ((((long)(size.X / cellSize) + 1) * ((long)(size.Y / cellSize) + 1)) > MaxCells)
        {
            cellSize *= 2;
        }

        CellSize = cellSize;
        CountX = (int)MathF.Floor(size.X / cellSize) + 1;
        CountY = (int)MathF.Floor(size.Y / cellSize) + 1;
        CellStart = new int[(CountX * CountY) + 1];

        var ranges = new (int X0, int Y0, int X1, int Y1)[mesh.TriangleCount];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            var min = Vector2.Min(new Vector2(a.X, a.Y), Vector2.Min(new Vector2(b.X, b.Y), new Vector2(c.X, c.Y)));
            var max = Vector2.Max(new Vector2(a.X, a.Y), Vector2.Max(new Vector2(b.X, b.Y), new Vector2(c.X, c.Y)));
            var (x0, y0) = Cell(min);
            var (x1, y1) = Cell(max);
            ranges[t] = (x0, y0, x1, y1);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    CellStart[(y * CountX) + x + 1]++;
                }
            }
        }

        for (var i = 1; i < CellStart.Length; i++)
        {
            CellStart[i] += CellStart[i - 1];
        }

        TriangleIndices = new int[CellStart[^1]];
        var fill = CellStart[..^1];
        for (var t = 0; t < ranges.Length; t++)
        {
            var (x0, y0, x1, y1) = ranges[t];
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    TriangleIndices[fill[(y * CountX) + x]++] = t;
                }
            }
        }
    }

    public Vector2 Origin { get; }

    public float CellSize { get; }

    public int CountX { get; }

    public int CountY { get; }

    /// <summary>Cell (x, y) holds <c>TriangleIndices[CellStart[i]..CellStart[i + 1]]</c> with i = y * CountX + x.</summary>
    public int[] CellStart { get; }

    public int[] TriangleIndices { get; }

    /// <summary>Triangles whose XY bounds overlap the cell containing (x, y); empty outside the grid.</summary>
    public ReadOnlySpan<int> TrianglesAt(float x, float y)
    {
        var cx = (int)MathF.Floor((x - Origin.X) / CellSize);
        var cy = (int)MathF.Floor((y - Origin.Y) / CellSize);
        if (cx < 0 || cy < 0 || cx >= CountX || cy >= CountY)
        {
            return [];
        }

        var i = (cy * CountX) + cx;
        return TriangleIndices.AsSpan(CellStart[i], CellStart[i + 1] - CellStart[i]);
    }

    private (int X, int Y) Cell(Vector2 p) => (
        Math.Clamp((int)MathF.Floor((p.X - Origin.X) / CellSize), 0, CountX - 1),
        Math.Clamp((int)MathF.Floor((p.Y - Origin.Y) / CellSize), 0, CountY - 1));
}
