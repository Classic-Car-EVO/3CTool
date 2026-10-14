using ThreeCTool.Compute;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths;

/// <summary>A previously generated toolpath and the tool that cut it (rest machining input).</summary>
public sealed record PreviousCut(Toolpath Toolpath, Tool Tool);

/// <summary>Everything needed to generate one operation's toolpath. Meshes and stock are in the part frame.</summary>
public sealed class ToolpathRequest
{
    public required ComputeContext Compute { get; init; }

    public required Operation Operation { get; init; }

    public required Setup Setup { get; init; }

    public required Tool Tool { get; init; }

    public Collet? Collet { get; init; }

    public required Stock Stock { get; init; }

    /// <summary>Stock mesh for <see cref="StockType.Mesh"/> (already loaded by the caller).</summary>
    public Mesh? StockMesh { get; init; }

    /// <summary>Milled part (all mill bodies merged), in the part frame.</summary>
    public required Mesh Part { get; init; }

    /// <summary>Keep-out zones; only those attached to <see cref="Setup"/> are used during generation.</summary>
    public IReadOnlyList<KeepOutZone> KeepOuts { get; init; } = [];

    public Tolerances Tolerances { get; init; } = new();

    /// <summary>Travel limits for the checks; null skips them.</summary>
    public MachineProfile? Machine { get; init; }

    /// <summary>Earlier operations (any setup) for rest machining, in machining order.</summary>
    public IReadOnlyList<PreviousCut> PreviousCuts { get; init; } = [];

    /// <summary>Dexel resolution of the rest-machining stock model; null picks one from the tool size.</summary>
    public double? StockResolution { get; init; }

    /// <summary>
    /// 3-axis: deepest cut below the (turned) stock top, resolved with <see cref="StockTurn.DepthLimit"/>; null when
    /// unlimited.
    /// </summary>
    public double? DepthLimit { get; init; }

    /// <summary>Tabs of every setup (<see cref="TabSolids"/>, part frame): material no operation may remove.</summary>
    public Mesh? Tabs { get; init; }

    /// <summary>Notes about the setup (e.g. from <see cref="TabSolids"/>) added to the toolpath warnings.</summary>
    public IReadOnlyList<string> SetupWarnings { get; init; } = [];
}
