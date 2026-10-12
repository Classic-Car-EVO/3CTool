using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Feeds;
using ThreeCTool.Geometry;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Simulation;

/// <summary>Whole project sequence to simulate: stock, part and the milling / printing steps in execution order.</summary>
public sealed class SimulationJob
{
    public required ComputeContext Compute { get; init; }

    /// <summary>Target geometry in the part frame (deviation reference).</summary>
    public required Mesh Part { get; init; }

    public required Stock Stock { get; init; }

    /// <summary>Required for <see cref="StockType.Mesh"/> stock (part frame).</summary>
    public Mesh? StockMesh { get; init; }

    public required MachineProfile Machine { get; init; }

    public Tolerances Tolerances { get; init; } = new();

    public IReadOnlyList<KeepOutZone> KeepOuts { get; init; } = [];

    public required IReadOnlyList<SimulationStep> Steps { get; init; }

    /// <summary>
    /// Machine position of each setup's WCS origin (at A = 0), keyed by setup id. Without it, machine keep-outs and
    /// absolute travel limits of that setup cannot be checked (only the travel span is).
    /// </summary>
    public IReadOnlyDictionary<Guid, Vector3> WorkOffsets { get; init; } = new Dictionary<Guid, Vector3>();

    public SimulationOptions Options { get; init; } = new();
}

public sealed class SimulationOptions
{
    /// <summary>Dexel spacing (mm); null derives it from <see cref="TargetRays"/>.</summary>
    public float? Resolution { get; init; }

    /// <summary>Total dexel rays aimed for when the resolution is automatic.</summary>
    public int TargetRays { get; init; } = 2_000_000;

    public float MinResolution { get; init; } = 0.02f;

    public float MaxResolution { get; init; } = 1f;

    /// <summary>Feed (mm/min) assumed for cycle time when an operation has no cutting conditions.</summary>
    public double DefaultFeed { get; init; } = 1000;

    public double DefaultPlungeFeed { get; init; } = 300;

    /// <summary>Extra distance by which collision bodies are grown (0 = only real contact counts).</summary>
    public float CollisionClearance { get; init; }

    /// <summary>Touching contact ignored by collision checks: bodies shrink radially and start this far above their base.</summary>
    public float ContactTolerance { get; init; } = 0.02f;

    /// <summary>Distance between collision poses along a move (mm).</summary>
    public float SampleSpacing { get; init; } = 1f;

    /// <summary>Rotary moves are split into straight pieces of at most this A step (degrees).</summary>
    public double MaxAngleStep { get; init; } = 0.5;

    /// <summary>Collision poses tested against the in-process stock per batch.</summary>
    public int ChunkPoses { get; init; } = 8192;

    /// <summary>Signed distances are searched up to this far from the part (mm).</summary>
    public float DeviationRange { get; init; } = 5f;

    /// <summary>Evaluate gouges after every milling step to attribute them to the step that caused them.</summary>
    public bool AttributeGouges { get; init; } = true;

    /// <summary>Issues of one kind reported per step before they are summarized.</summary>
    public int MaxIssuesPerKind { get; init; } = 50;
}

/// <summary>One machining or printing file: a single tool or head in a single setup.</summary>
public abstract class SimulationStep
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public string Name { get; init; } = string.Empty;

    public Guid SetupId { get; init; }
}

public sealed class MillingStep : SimulationStep
{
    /// <summary>Moves in the WCS of the toolpath's frame.</summary>
    public required Toolpath Toolpath { get; init; }

    public required Tool Tool { get; init; }

    public Collet? Collet { get; init; }

    public CuttingConditions? Conditions { get; init; }

    /// <summary>Slows moves that cut more than the nominal engagement; null keeps every feed nominal.</summary>
    public EngagementLimiter? Limiter { get; init; }
}

public enum PrintMoveKind : byte
{
    Travel,
    Extrude,
}

/// <summary>
/// Nozzle tip target in the WCS of the step's frame (mm, degrees). Extrusion moves lay a bead of
/// <see cref="Width"/> × <see cref="Height"/> directly below the tip; <see cref="Feed"/> is the nozzle speed (mm/min).
/// </summary>
public readonly record struct PrintMove(PrintMoveKind Kind, double X, double Y, double Z, double A, double Width, double Height, double Feed)
{
    /// <summary>Bead cross-section: a rectangle with semicircular sides (mm²).</summary>
    public double BeadArea => Width <= 0 || Height <= 0 ? 0 : ((Math.Max(Width, Height) - Height) * Height) + (Math.PI * Height * Height / 4);
}

public sealed class DepositionStep : SimulationStep
{
    public required SetupFrame Frame { get; init; }

    public required IReadOnlyList<PrintMove> Moves { get; init; }

    public required Toolhead Head { get; init; }

    public Filament? Filament { get; init; }
}
