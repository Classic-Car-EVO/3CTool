namespace ThreeCTool.Core.Machines;

public sealed class Collet
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid MachineProfileId { get; set; }

    public string Name { get; set; } = string.Empty;

    public double ShankDiameter { get; set; }

    /// <summary>Holder outline from the collet nut face upward, used for collision checks.</summary>
    public List<HolderSegment> Holder { get; set; } = [];
}

public sealed class HolderSegment
{
    public double Diameter { get; set; }

    public double Length { get; set; }
}
