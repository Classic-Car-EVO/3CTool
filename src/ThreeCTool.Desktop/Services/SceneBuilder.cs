using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Geometry;
using ThreeCTool.Rendering;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Desktop.Services;

/// <summary>Converts project content (part frame) into <see cref="CamScene"/> items.</summary>
public static class SceneBuilder
{
    /// <summary>Simultaneous rotary moves are drawn as arcs split into pieces of at most this many degrees.</summary>
    public const double MaxRotaryStep = 2;

    private static readonly Vector4 PrintBody = new(0.45f, 0.6f, 0.95f, 0.6f);

    /// <summary>
    /// Tool tip path in the part frame. Vertex lines are move indices (the start position is 0), so a scene progress
    /// of <c>m</c> shows moves 1..m as done; strips split where moves change between rapid and feed.
    /// </summary>
    public static PolylinePath ToPath(Toolpath toolpath)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        var moves = toolpath.Moves;
        if (moves.Count == 0)
        {
            return PolylinePath.Empty;
        }

        var frame = toolpath.Frame;
        var positions = new List<float>(moves.Count * 3);
        var lines = new List<int>(moves.Count);
        var stripStarts = new List<int>();
        var stripRapid = new List<bool>();
        void Add(ToolpathMove move, int line)
        {
            var tip = frame.ToolInPart(move).Tip;
            positions.AddRange([(float)tip.X, (float)tip.Y, (float)tip.Z]);
            lines.Add(line);
        }

        for (var i = 1; i < moves.Count; i++)
        {
            var (start, end) = (moves[i - 1], moves[i]);
            var rapid = end.Kind == MoveKind.Rapid;
            if (stripRapid.Count == 0 || stripRapid[^1] != rapid)
            {
                stripStarts.Add(lines.Count);
                stripRapid.Add(rapid);
                Add(start, i - 1);
            }

            var pieces = frame.Orientation == SetupOrientation.SimultaneousRotary
                ? Math.Max(1, (int)Math.Ceiling(Math.Abs(end.A - start.A) / MaxRotaryStep))
                : 1;
            for (var j = 1; j < pieces; j++)
            {
                var t = (double)j / pieces;
                Add(new ToolpathMove(
                    end.Kind,
                    start.X + ((end.X - start.X) * t),
                    start.Y + ((end.Y - start.Y) * t),
                    start.Z + ((end.Z - start.Z) * t),
                    start.A + ((end.A - start.A) * t)), i);
            }

            Add(end, i);
        }

        if (stripStarts.Count == 0)
        {
            // A single move: just the start point.
            stripStarts.Add(0);
            stripRapid.Add(false);
            Add(moves[0], 0);
        }

        return new PolylinePath([.. positions], [.. lines], [.. stripStarts], [.. stripRapid]);
    }

    /// <summary>Tool paths in execution order, coloured by their position in it.</summary>
    public static IReadOnlyList<SceneToolpath> Toolpaths(IEnumerable<GeneratedToolpath> generated)
    {
        ArgumentNullException.ThrowIfNull(generated);
        return [.. generated.Select((g, i) => new SceneToolpath(g.Operation.Id, g.Operation.Name, ToPath(g.Toolpath), SceneColors.Operation(i)))];
    }

    /// <summary>Part bodies, stock and keep-out zones in the part frame.</summary>
    /// <param name="frameOf">Frame of a setup, for zones fixed in its WCS; null when the setup is unknown.</param>
    /// <remarks>
    /// Machine-attached zones are in machine coordinates and only meet the part through the work offset found when
    /// the stock is zeroed on the machine, so they are not shown here.
    /// </remarks>
    public static IReadOnlyList<SceneMesh> ProjectMeshes(
        IEnumerable<LoadedPart> parts, Mesh? stock, IEnumerable<KeepOutZone> keepOuts, Func<Guid, SetupFrame?> frameOf)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(keepOuts);
        ArgumentNullException.ThrowIfNull(frameOf);
        var meshes = new List<SceneMesh>();
        foreach (var part in parts)
        {
            foreach (var body in part.Bodies)
            {
                var color = body.Role == BodyRole.Mill && part.Model.Role == BodyRole.Mill ? SceneColors.Part : PrintBody;
                meshes.Add(new SceneMesh(SceneLayer.Part, $"{part.Model.Name}/{body.Name}", body.Mesh.Transform(part.Matrix), color));
            }
        }

        if (stock is not null)
        {
            meshes.Add(new SceneMesh(SceneLayer.Stock, "Stock", stock, SceneColors.Stock));
        }

        foreach (var zone in keepOuts)
        {
            var mesh = PartGeometry.KeepOutMesh(zone);
            switch (zone.Attachment)
            {
                case KeepOutAttachment.Setup:
                    meshes.Add(new SceneMesh(SceneLayer.KeepOut, zone.Solid.Name, mesh, SceneColors.KeepOut));
                    break;
                case KeepOutAttachment.SetupFixed when zone.SetupId is { } id && frameOf(id) is { } frame
                    && Matrix4x4.Invert(frame.PartToWcsMatrix(frame.BaseAngle), out var wcsToPart):
                    meshes.Add(new SceneMesh(SceneLayer.KeepOut, zone.Solid.Name, mesh.Transform(wcsToPart), SceneColors.KeepOut));
                    break;
                default:
                    break;
            }
        }

        return meshes;
    }

    /// <summary>Simulated stock surface, darker towards the bottom for depth.</summary>
    public static ScenePoints StockPoints(IReadOnlyList<Vector3> points, float resolution)
    {
        ArgumentNullException.ThrowIfNull(points);
        var (low, high) = points.Count == 0 ? (0f, 1f) : (points.Min(p => p.Z), points.Max(p => p.Z));
        var span = Math.Max(high - low, 1e-3f);
        var colors = points.Select(p =>
        {
            var k = 0.55f + (0.45f * (p.Z - low) / span);
            var c = SceneColors.SimulatedStock;
            return new Vector4(c.X * k, c.Y * k, c.Z * k, 1);
        }).ToArray();
        return new ScenePoints(SceneLayer.Simulation, "Simulated stock", [.. points], colors, resolution);
    }

    /// <summary>Deviation heatmap: <see cref="Heatmap"/> colours over the stock surface samples.</summary>
    public static ScenePoints DeviationPoints(IReadOnlyList<DeviationSample> samples, double tolerance, double range, float resolution)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return new ScenePoints(
            SceneLayer.Simulation,
            "Deviation",
            [.. samples.Select(s => s.Point)],
            [.. samples.Select(s => Heatmap.Color(s.Distance, (float)tolerance, (float)range))],
            resolution);
    }

    /// <summary>Tool pose of a move target, sized like the tool's flute/stick-out.</summary>
    public static ToolMarker ToolMarker(SetupFrame frame, ToolpathMove move, Tool tool)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(tool);
        var (tip, axis) = frame.ToolInPart(move);
        var length = tool.Stickout > 0 ? tool.Stickout : Math.Max(tool.FluteLength, tool.Diameter * 3);
        return new ToolMarker(
            new Vector3((float)tip.X, (float)tip.Y, (float)tip.Z),
            new Vector3((float)axis.X, (float)axis.Y, (float)axis.Z),
            (float)tool.Diameter,
            (float)length);
    }
}
