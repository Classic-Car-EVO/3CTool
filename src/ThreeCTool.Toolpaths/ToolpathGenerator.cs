using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Toolpaths.Strategies;

namespace ThreeCTool.Toolpaths;

/// <summary>Generates the toolpath of one operation.</summary>
public static class ToolpathGenerator
{
    /// <summary>Bumped when the generated toolpaths change, so cached ones are regenerated.</summary>
    public const int Revision = 2;

    public static Toolpath Generate(ToolpathRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = request.Operation;
        var orientation = request.Setup.Orientation;
        var rotary = operation.Strategy is StrategyType.RotaryRoughing or StrategyType.RotaryFinishing;
        if (rotary && orientation != SetupOrientation.SimultaneousRotary)
        {
            throw new InvalidOperationException($"{operation.Strategy} needs a simultaneous rotary setup.");
        }

        var warnings = new List<string>(request.SetupWarnings);
        var settings = ToolpathSettings.Resolve(operation, request.Tool, request.Tolerances);
        using var setup = MachiningSetup.Create(request);
        if (setup.Floor is { } floor)
        {
            settings = settings with { MinZ = Math.Max(settings.MinZ ?? double.MinValue, floor) };
        }

        var context = new StrategyContext(request, setup, settings, warnings, progress, cancellationToken);
        var builder = rotary ? RotaryMachining.NewBuilder(context) : context.NewBuilder();
        switch (operation.Strategy)
        {
            case StrategyType.AdaptiveRoughing:
                AdaptiveClearing.Run(context, builder);
                break;
            case StrategyType.ZLevelRoughing:
                ZLevelRoughing.Run(context, builder);
                break;
            case StrategyType.ParallelFinishing:
                ParallelFinishing.Run(context, builder);
                break;
            case StrategyType.Waterline:
                Waterline.Run(context, builder);
                break;
            case StrategyType.Scallop:
                ScallopFinishing.Run(context, builder);
                break;
            case StrategyType.RestMachining:
                RestMachining.Run(context, builder);
                break;
            case StrategyType.Pencil:
                PencilFinishing.Run(context, builder);
                break;
            case StrategyType.RotaryRoughing:
                RotaryMachining.Roughing(context, builder);
                break;
            case StrategyType.RotaryFinishing:
                RotaryMachining.Finishing(context, builder);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), operation.Strategy, "Unknown strategy.");
        }

        var moves = builder.Finish();
        if (setup.Floor is { } limit && moves.Exists(m => m.Z < limit))
        {
            // The depth limit is a hard floor: raising a tip never cuts more.
            moves = [.. moves.Select(m => m.Z < limit ? m with { Z = limit } : m)];
        }

        if (builder.PlungeEntries > 0)
        {
            warnings.Add($"{builder.PlungeEntries} entr{(builder.PlungeEntries == 1 ? "y plunges" : "ies plunge")} straight into material (no room for a helix or ramp).");
        }

        if (moves.Count == 0)
        {
            warnings.Add("The operation produced no cutting moves.");
        }

        var toolpath = new Toolpath
        {
            OperationId = operation.Id,
            Strategy = operation.Strategy,
            ToolId = request.Tool.Id,
            Frame = setup.Frame,
            Clearance = builder.Clearance,
            Moves = moves,
            Warnings = warnings,
        };
        ToolpathChecks.Run(toolpath, setup, request.Machine, warnings);
        progress?.Report(1);
        return toolpath;
    }
}

/// <summary>Plausibility checks of a generated toolpath against the stock and machine.</summary>
public static class ToolpathChecks
{
    public static void Run(Toolpath toolpath, MachiningSetup setup, MachineProfile? machine, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(warnings);
        if (toolpath.Moves.Count == 0)
        {
            return;
        }

        var bounds = toolpath.Bounds;
        if (!toolpath.UsesRotary)
        {
            var floor = setup.StockBounds.Min.Z - 1e-3;
            var lowest = toolpath.Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Z).DefaultIfEmpty(double.MaxValue).Min();
            if (lowest < floor)
            {
                warnings.Add($"Cuts reach Z {lowest:0.###}, {floor - lowest:0.###} mm below the stock bottom.");
            }
        }

        if (machine is null)
        {
            return;
        }

        foreach (var (axis, span) in (ReadOnlySpan<(AxisName, double)>)[(AxisName.X, bounds.Size.X), (AxisName.Y, bounds.Size.Y), (AxisName.Z, bounds.Size.Z)])
        {
            var config = machine.GetAxis(axis);
            if (config is not null && config.MaxTravel > 0 && span > config.MaxTravel)
            {
                warnings.Add($"Toolpath spans {span:0.#} mm in {axis}, more than the {config.MaxTravel:0.#} mm travel.");
            }
        }

        if (setup.Frame.IsRotary)
        {
            var radius = toolpath.UsesRotary
                ? Math.Max(Math.Abs(bounds.Max.Z), Math.Abs(bounds.Min.Z))
                : setup.StockRadius;
            if (machine.RotaryAxis.MaxDiameter > 0 && 2 * radius > machine.RotaryAxis.MaxDiameter)
            {
                warnings.Add($"Rotary work diameter {2 * radius:0.#} mm exceeds the rotary axis maximum of {machine.RotaryAxis.MaxDiameter:0.#} mm.");
            }

            var length = setup.StockBounds.Size.X;
            if (machine.RotaryAxis.MaxLength > 0 && length > machine.RotaryAxis.MaxLength)
            {
                warnings.Add($"Stock length {length:0.#} mm exceeds the rotary axis maximum of {machine.RotaryAxis.MaxLength:0.#} mm.");
            }
        }
    }
}
