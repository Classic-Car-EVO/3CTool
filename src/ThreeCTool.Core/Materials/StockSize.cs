namespace ThreeCTool.Core.Materials;

/// <summary>
/// A standard size of box material (a board or block as bought or pre-cut). A project stock made of such pieces is
/// split at seams into several pieces when the model is larger than one piece.
/// </summary>
public sealed class StockSize
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    /// <summary>Material this size is stocked in; null for any material.</summary>
    public Guid? MaterialId { get; set; }

    public double SizeX { get; set; }

    public double SizeY { get; set; }

    /// <summary>Thickness.</summary>
    public double SizeZ { get; set; }

    public string? Notes { get; set; }
}
