using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming.Jobs;

/// <summary>
/// Follows a file block by block and reports moves whose machine Z leaves the controller's Z travel or brings the tool
/// tip below the table (<see cref="JobStreamerOptions.TableMachineZ"/> plus the file's tool stickout). Moves in machine
/// coordinates (<c>G53</c>, and incremental moves from there) are always checked; work-coordinate moves only with the
/// controller's offsets. Unknown positions (after probing) are not checked.
/// </summary>
internal sealed class MachineZGuard(JobStreamerOptions options)
{
    private const double Tolerance = 1e-3;
    private const int ArcSamples = 16;

    private readonly GcodeInterpreter interpreter = new();

    public bool Active => options.MachineZTravel is not null || options.TableMachineZ is not null;

    /// <summary>Tool length below the collet nut from the last <c>(MC:TOOL STICKOUT ...)</c>; 0 without one.</summary>
    public double Stickout { get; private set; }

    /// <summary>Lowest machine Z with the tool tip on the table.</summary>
    public double? Floor => options.TableMachineZ + Stickout;

    /// <summary>Applies <paramref name="block"/>; returns why its move must not be sent, or null.</summary>
    public string? Check(GcodeBlock block, int line, WorkCoordinates? offsets)
    {
        if (block.Directive?.ToolStickout is { } stickout)
        {
            Stickout = stickout;
        }

        if (interpreter.Apply(block, line) is not { } segment)
        {
            return null;
        }

        // Without a Z word the controller keeps the machine Z, whatever the offsets did meanwhile.
        if (block.Get('Z') is null && !(segment.IsArc && segment.Plane != 17))
        {
            return null;
        }

        foreach (var z in Heights(segment))
        {
            if (MachineZ(segment, z, offsets) is { } machineZ && Problem(machineZ) is { } problem)
            {
                return $"Line {line}: {problem}";
            }
        }

        return null;
    }

    /// <summary>The safe Z is used to retract after a stop: it must be inside the travel and above the table.</summary>
    public string? SafeZProblem() => Problem(options.SafeZMachine) is { } problem ? $"The safe Z in the feeder settings: {problem}" : null;

    public void ForgetPositions()
    {
        Array.Fill(interpreter.State.Position, double.NaN);
        Array.Fill(interpreter.State.MachineAxes, false);
    }

    private static IEnumerable<double> Heights(MotionSegment segment)
    {
        yield return segment.ProgrammedEnd[2];
        if (segment.IsArc && segment.Plane != 17)
        {
            // Arcs in the XZ / YZ plane can dip below both end points.
            for (var i = 1; i < ArcSamples; i++)
            {
                yield return segment.PointAt((double)i / ArcSamples)[2];
            }
        }
    }

    private double? MachineZ(MotionSegment segment, double z, WorkCoordinates? offsets)
    {
        if (double.IsNaN(z))
        {
            return null;
        }

        if (segment.MachineAxes[2])
        {
            return z;
        }

        if (offsets?.Get("G" + interpreter.State.Wcs) is not { } wcs)
        {
            return null;
        }

        return z + wcs[2] + (offsets.Get("G92")?[2] ?? 0) + offsets.ToolLengthOffset;
    }

    private string? Problem(double machineZ)
    {
        if (Floor is { } floor && machineZ < floor - Tolerance)
        {
            return $"machine Z {Format(machineZ)} puts the tool tip {Format(floor - machineZ)} mm below the table "
                + $"(collet nut on the table at machine Z {Format(options.TableMachineZ!.Value)}, stickout {Format(Stickout)} mm).";
        }

        if (options.MachineZTravel is { } travel && (machineZ < travel.Low - Tolerance || machineZ > travel.High + Tolerance))
        {
            return $"machine Z {Format(machineZ)} is outside the controller's Z travel {Format(travel.Low)}..{Format(travel.High)}.";
        }

        return null;
    }

    private static string Format(double value) => GcodeInterpreter.Format(value);
}
