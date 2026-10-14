using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Geometry;
using ThreeCTool.Simulation;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Post;

/// <summary>Turns one job (a single tool or head in a single setup) into a G-code file.</summary>
public interface IPostProcessor<in TRequest>
{
    PostOutput Post(TRequest request);
}

/// <summary>Request is invalid or the output would be unsafe; nothing is written.</summary>
public sealed class PostException : Exception
{
    public PostException()
    {
    }

    public PostException(string message)
        : base(message)
    {
    }

    public PostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One operation of the file: its toolpath, resolved cutting conditions and (optionally) its simulated timing.</summary>
public sealed record PostOperation(Operation Operation, Toolpath Toolpath, CuttingConditions Conditions, StepSummary? Summary = null);

/// <summary>Where the touch plate is placed: WCS X/Y and the WCS Z of the surface it lies on.</summary>
public readonly record struct TouchPlateSpot(double X, double Y, double SurfaceZ)
{
    /// <summary>
    /// Centre of the stock top in the WCS of <paramref name="frame"/> (stock bounds in the part frame). Rotary setups
    /// use the top of the stock straight above the axis at the setup's A; <paramref name="stockRadius"/> is used for
    /// cylindrical stock.
    /// </summary>
    public static TouchPlateSpot FromStockBounds(SetupFrame frame, BoundingBox stockBounds, double? stockRadius = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var min = new P3(stockBounds.Min.X, stockBounds.Min.Y, stockBounds.Min.Z);
        var max = new P3(stockBounds.Max.X, stockBounds.Max.Y, stockBounds.Max.Z);
        var x = (0.5 * (min.X + max.X)) - frame.OriginX;
        if (!frame.IsRotary)
        {
            // A turned stock shows another face on top: take the WCS box of the turned stock.
            var corners = Enumerable.Range(0, 8)
                .Select(i => frame.PartToWcs(new P3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), 0))
                .ToList();
            return new(
                0.5 * (corners.Min(c => c.X) + corners.Max(c => c.X)),
                0.5 * (corners.Min(c => c.Y) + corners.Max(c => c.Y)),
                corners.Max(c => c.Z));
        }

        if (stockRadius is { } radius)
        {
            return new(x, 0, radius);
        }

        // Ray from the axis along WCS +Z, in the part frame, leaving the stock box.
        var direction = SetupFrame.RotateX(new P3(0, 0, 1), -frame.BaseAngle);
        var (y0, y1, z0, z1) = (min.Y - frame.OriginY, max.Y - frame.OriginY, min.Z - frame.OriginZ, max.Z - frame.OriginZ);
        var t = Math.Min(Exit(direction.Y, y0, y1), Exit(direction.Z, z0, z1));
        return new(x, 0, t);

        static double Exit(double d, double low, double high) =>
            d > 1e-12 ? high / d : d < -1e-12 ? low / d : double.PositiveInfinity;
    }
}

/// <summary>Milling job for <see cref="FluidNcMillingPost"/>: one tool in one setup.</summary>
public sealed class MillingPostRequest
{
    public required Project Project { get; init; }

    public required Setup Setup { get; init; }

    public required MachineProfile Machine { get; init; }

    public required Tool Tool { get; init; }

    public Collet? Collet { get; init; }

    public Material? Material { get; init; }

    /// <summary>Spindle speed for the whole file (the dial is set once per tool).</summary>
    public required double Rpm { get; init; }

    /// <summary>Operations in execution order; all use <see cref="Tool"/>.</summary>
    public required IReadOnlyList<PostOperation> Operations { get; init; }

    /// <summary>First file of the setup: sets the work Z (touch plate) and, for the tool-setter method, the reference.</summary>
    public bool FirstInSetup { get; init; }

    /// <summary>Position of the file within the setup (1-based), used in the file name.</summary>
    public int FileIndex { get; init; } = 1;

    /// <summary>Stock piece this file machines when the stock is split into standard pieces; null otherwise.</summary>
    public StockPiece? Piece { get; init; }

    /// <summary>Number of stock pieces (with <see cref="Piece"/>).</summary>
    public int PieceCount { get; init; }

    /// <summary>Machine position of the WCS origin at A = 0, when known; enables absolute travel checks.</summary>
    public System.Numerics.Vector3? WorkOffset { get; init; }

    /// <summary>Touch plate placement (WCS). Required when the file probes with the touch plate.</summary>
    public TouchPlateSpot? TouchPlate { get; init; }

    /// <summary>Simulation result. Without it, or with errors of this file, output is refused unless overridden.</summary>
    public VerificationReport? Verification { get; init; }

    /// <summary>User explicitly accepts a missing or failed verification.</summary>
    public bool OverrideVerification { get; init; }

    /// <summary>Hashes of the inputs (toolpaths, models, settings) written to the header for traceability.</summary>
    public IReadOnlyDictionary<string, string> InputHashes { get; init; } = new Dictionary<string, string>();

    public string GeneratorVersion { get; init; } =
        typeof(MillingPostRequest).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary>Generated G-code file (LF line endings, trailing newline, UTF-8 without BOM).</summary>
public sealed class PostOutput
{
    /// <summary>Path relative to the project folder, e.g. <c>gcode/01_Top_T01_6mm.nc</c>.</summary>
    public required string FileName { get; init; }

    public required IReadOnlyList<string> Lines { get; init; }

    public required string Text { get; init; }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <see cref="Text"/>.</summary>
    public required string Sha256 { get; init; }

    public int LineCount => Lines.Count;

    public double EstimatedSeconds { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public Guid SetupId { get; init; }

    /// <summary>Stock piece number, 0 when the stock is not split.</summary>
    public int Piece { get; init; }

    public Guid ToolId { get; init; }

    public double Rpm { get; init; }
}
