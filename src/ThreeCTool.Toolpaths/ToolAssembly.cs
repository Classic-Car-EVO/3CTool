using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Toolpaths;

/// <summary>One rigid part of the tool assembly: a cutter profile whose tip sits <see cref="Offset"/> above the tool tip.</summary>
public readonly record struct ToolStage(CutterProfile Profile, double Offset);

/// <summary>
/// Cutter plus shank and holder as stacked stages, so drop- and push-cutter queries keep the whole assembly clear
/// of the part and keep-outs.
/// </summary>
public sealed class ToolAssembly
{
    private ToolAssembly(Tool tool, CutterProfile cutter, IReadOnlyList<ToolStage> stages)
    {
        Tool = tool;
        Cutter = cutter;
        Stages = stages;
    }

    public Tool Tool { get; }

    public CutterProfile Cutter { get; }

    public double Radius => Cutter.Radius;

    public double Diameter => 2.0 * Cutter.Radius;

    /// <summary>Cutter first, then wider stages (shank, holder segments) bottom-up.</summary>
    public IReadOnlyList<ToolStage> Stages { get; }

    /// <summary>Widest stage radius (grid margins).</summary>
    public double MaxRadius => Stages.Max(s => s.Profile.Radius);

    public static ToolAssembly Create(Tool tool, Collet? collet, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(warnings);
        var cutter = CutterProfile.FromTool(tool);
        var stages = new List<ToolStage> { new(cutter, 0) };
        double radius = cutter.Radius;
        var flute = tool.FluteLength > 0 ? tool.FluteLength : cutter.Height;
        var stickout = tool.Stickout;
        if (tool.ShankDiameter / 2 > radius + 1e-6 && (stickout <= 0 || stickout > flute))
        {
            stages.Add(new ToolStage(CutterProfile.Flat(tool.ShankDiameter), flute));
            radius = tool.ShankDiameter / 2;
        }

        if (stickout <= 0)
        {
            warnings.Add($"Tool '{tool.Name}' has no stickout; shank and holder are not protected against collisions.");
        }
        else if (collet is not null)
        {
            var offset = stickout;
            foreach (var segment in collet.Holder)
            {
                if (segment.Diameter / 2 > radius + 1e-6 && segment.Length > 0)
                {
                    stages.Add(new ToolStage(CutterProfile.Flat(segment.Diameter), offset));
                    radius = segment.Diameter / 2;
                }

                offset += segment.Length;
            }
        }

        return new ToolAssembly(tool, cutter, stages);
    }

    /// <summary>
    /// Same assembly whose cutter stage keeps radial / axial stock: the cutter is replaced by its offset profile
    /// (exact for flat, bull-nose and ball tools) and shifted so results stay real tip positions.
    /// </summary>
    public ToolAssembly WithStockToLeave(double radial, double axial)
    {
        if (radial == 0 && axial == 0)
        {
            return this;
        }

        var stages = Stages.ToList();
        stages[0] = new ToolStage(OffsetProfile(Tool, Cutter, radial), -axial);
        return new ToolAssembly(Tool, Cutter, stages);
    }

    private static CutterProfile OffsetProfile(Tool tool, CutterProfile cutter, double s)
    {
        if (s == 0)
        {
            return cutter;
        }

        var diameter = 2 * (cutter.Radius + s);
        if (diameter <= 1e-3)
        {
            throw new ArgumentOutOfRangeException(nameof(s), s, "Negative stock to leave exceeds the tool radius.");
        }

        var height = cutter.Height;
        var corner = Math.Max(cutter.CornerRadius + s, 0);
        return tool.Type switch
        {
            ToolType.BallEnd => CutterProfile.Ball(diameter, height),
            ToolType.FlatEnd or ToolType.BullNose => CutterProfile.BullNose(diameter, Math.Min(corner, diameter / 2), height),
            ToolType.VBit or ToolType.Chamfer => s > 0
                ? CutterProfile.TaperedBall(diameter, Math.Min(2 * (s + (tool.TipDiameter / 2)), diameter), tool.TipAngle / 2, height)
                : CutterProfile.Cone(diameter, tool.TipAngle, Math.Max(tool.TipDiameter + (2 * s), 0), height),
            _ => CutterProfile.TaperedBall(diameter, Math.Clamp(tool.TipDiameter + (2 * s), 1e-3, diameter), tool.TaperAngle, height),
        };
    }

    /// <summary>Lowest safe tip Z at each XY (tool along +Z); −∞ where nothing is below.</summary>
    public float[] Drop(MachiningSetup setup, ReadOnlySpan<Vector2> points, bool obstacles = true)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var origins = new Vector3[points.Length];
        var axes = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            origins[i] = new Vector3(points[i], 0);
            axes[i] = Vector3.UnitZ;
        }

        return Combine(setup, obstacles, (mesh, profile) => DropCutter.Drop(mesh, profile, origins, axes));
    }

    /// <summary>Lowest safe tip Z on a grid (row-major, j·countX + i).</summary>
    public float[] DropGrid(MachiningSetup setup, Vector2 origin, float step, int countX, int countY, bool obstacles = true)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return Combine(setup, obstacles, (mesh, profile) => DropCutter.Grid(mesh, profile, origin, step, countX, countY));
    }

    /// <summary>
    /// Rotary drop: smallest safe tip radius for the tool pointing along (0, sin A, cos A) at each (x, A) pair.
    /// Returns −∞ where nothing is below.
    /// </summary>
    public float[] DropRotary(MachiningSetup setup, ReadOnlySpan<Vector2> xa, bool obstacles = true)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var origins = new Vector3[xa.Length];
        var axes = new Vector3[xa.Length];
        for (var i = 0; i < xa.Length; i++)
        {
            var (sin, cos) = MathF.SinCos(xa[i].Y * MathF.PI / 180f);
            origins[i] = new Vector3(xa[i].X, 0, 0);
            axes[i] = new Vector3(0, sin, cos);
        }

        return Combine(setup, obstacles, (mesh, profile) => DropCutter.Drop(mesh, profile, origins, axes));
    }

    /// <summary>
    /// Blocked tip ranges along horizontal fibers at their Z (part and, optionally, keep-outs), all stages merged.
    /// </summary>
    public FiberInterval[][] Push(MachiningSetup setup, Fiber[] fibers, bool part = true, bool obstacles = true)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(fibers);
        var merged = new List<FiberInterval>[fibers.Length];
        for (var i = 0; i < merged.Length; i++)
        {
            merged[i] = [];
        }

        foreach (var mesh in Meshes(setup, part, obstacles))
        {
            foreach (var stage in Stages)
            {
                var lifted = stage.Offset == 0
                    ? fibers
                    : [.. fibers.Select(f => f with { Start = f.Start + new Vector3(0, 0, (float)stage.Offset) })];
                var intervals = PushCutter.Run(mesh, stage.Profile, lifted);
                for (var i = 0; i < fibers.Length; i++)
                {
                    merged[i].AddRange(intervals[i]);
                }
            }
        }

        return [.. merged.Select(Merge)];
    }

    internal static FiberInterval[] Merge(List<FiberInterval> intervals)
    {
        if (intervals.Count < 2)
        {
            return [.. intervals];
        }

        intervals.Sort((a, b) => a.Enter.CompareTo(b.Enter));
        var result = new List<FiberInterval> { intervals[0] };
        for (var i = 1; i < intervals.Count; i++)
        {
            var last = result[^1];
            if (intervals[i].Enter <= last.Exit)
            {
                result[^1] = last with { Exit = MathF.Max(last.Exit, intervals[i].Exit) };
            }
            else
            {
                result.Add(intervals[i]);
            }
        }

        return [.. result];
    }

    private static IEnumerable<GpuMesh> Meshes(MachiningSetup setup, bool part, bool obstacles)
    {
        if (part)
        {
            yield return setup.PartGpu;
        }

        if (obstacles && setup.ObstaclesGpu is not null)
        {
            yield return setup.ObstaclesGpu;
        }
    }

    private float[] Combine(MachiningSetup setup, bool obstacles, Func<GpuMesh, CutterProfile, float[]> query)
    {
        float[]? result = null;
        foreach (var mesh in Meshes(setup, true, obstacles))
        {
            foreach (var stage in Stages)
            {
                var values = query(mesh, stage.Profile);
                var offset = (float)stage.Offset;
                if (result is null)
                {
                    result = values;
                    for (var i = 0; i < result.Length; i++)
                    {
                        result[i] -= offset;
                    }

                    continue;
                }

                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = MathF.Max(result[i], values[i] - offset);
                }
            }
        }

        return result!;
    }
}
