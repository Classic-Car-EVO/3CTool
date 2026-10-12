using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Geometry;

using Xunit;

namespace ThreeCTool.Toolpaths.Tests;

public sealed class ComputeFixture : IDisposable
{
    public ComputeContext Compute { get; } = ComputeContext.Create(ComputeBackend.Cpu);

    public void Dispose() => Compute.Dispose();
}

[CollectionDefinition(Name)]
public sealed class ComputeGroup : ICollectionFixture<ComputeFixture>
{
    public const string Name = "Compute";
}

internal static class Kit
{
    public static Tool Flat(double diameter) => new() { Name = $"flat {diameter}", Type = ToolType.FlatEnd, Diameter = diameter, FluteLength = 3 * diameter, ShankDiameter = diameter, Stickout = 6 * diameter };

    public static Tool Ball(double diameter) => new() { Name = $"ball {diameter}", Type = ToolType.BallEnd, Diameter = diameter, FluteLength = 3 * diameter, ShankDiameter = diameter, Stickout = 6 * diameter };

    public static Stock BoxStock(Vector3 min, Vector3 size) => new()
    {
        Type = StockType.Box,
        OriginX = min.X,
        OriginY = min.Y,
        OriginZ = min.Z,
        SizeX = size.X,
        SizeY = size.Y,
        SizeZ = size.Z,
    };

    /// <summary>30×30 base (z 0..5) with a 12×12 tower (z 5..12) — a concave corner all around the tower foot.</summary>
    public static Mesh Stepped() => Mesh.Merge(
        [TestMeshes.Box(new Vector3(-15, -15, 0), new Vector3(15, 15, 5)), TestMeshes.Box(new Vector3(-6, -6, 5), new Vector3(6, 6, 12))],
        "stepped");

    public static ToolpathRequest Request(ComputeContext compute, StrategyType strategy, Tool tool, Mesh part, Stock stock, Action<OperationParameters>? configure = null, SetupOrientation orientation = SetupOrientation.ThreeAxis, double indexAngle = 0)
    {
        var parameters = new OperationParameters();
        configure?.Invoke(parameters);
        return new ToolpathRequest
        {
            Compute = compute,
            Operation = new Operation { Name = strategy.ToString(), Strategy = strategy, ToolId = tool.Id, Parameters = parameters },
            Setup = new Setup { Name = "setup", Orientation = orientation, IndexAngle = indexAngle },
            Tool = tool,
            Stock = stock,
            Part = part,
        };
    }

    /// <summary>
    /// Largest depth by which the tool — grown by the stock to leave, then shrunk by <paramref name="allowance"/>
    /// all round — sinks into the part along the moves (positive = gouge), sampled every 0.2 mm.
    /// </summary>
    public static double MaxGouge(ToolpathRequest request, Toolpath toolpath, double allowance)
    {
        var settings = ToolpathSettings.Resolve(request.Operation, request.Tool, request.Tolerances);
        using var setup = MachiningSetup.Create(request);
        var tool = ToolAssembly.Create(request.Tool, null, []).WithStockToLeave(settings.RadialStock - allowance, settings.AxialStock - allowance);
        var samples = new List<Vector2>();
        var heights = new List<double>();
        var sources = new List<int>();
        var moves = toolpath.Moves;
        for (var i = 1; i < moves.Count; i++)
        {
            var a = moves[i - 1];
            var b = moves[i];
            var length = toolpath.UsesRotary
                ? Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.A - a.A) * Math.PI / 180 * Math.Max(a.Z, b.Z))
                : new Vector2((float)(b.X - a.X), (float)(b.Y - a.Y)).Length();
            var n = Math.Max(1, (int)Math.Ceiling(length / 0.2));
            for (var k = 0; k <= n; k++)
            {
                var t = (double)k / n;
                var x = a.X + ((b.X - a.X) * t);
                samples.Add(toolpath.UsesRotary
                    ? new Vector2((float)x, (float)(a.A + ((b.A - a.A) * t)))
                    : new Vector2((float)x, (float)(a.Y + ((b.Y - a.Y) * t))));
                heights.Add(a.Z + ((b.Z - a.Z) * t));
                sources.Add(i);
            }
        }

        var drop = toolpath.UsesRotary ? tool.DropRotary(setup, [.. samples]) : tool.Drop(setup, [.. samples]);
        var worst = double.NegativeInfinity;
        for (var i = 0; i < drop.Length; i++)
        {
            if (drop[i] - heights[i] > worst)
            {
                worst = drop[i] - heights[i];
                var s = sources[i];
                var context = string.Join(" | ", moves.Skip(Math.Max(s - 3, 0)).Take(5).Select(m => $"{m.Kind} ({m.X:0.###}, {m.Y:0.###}, {m.Z:0.###}, {m.A:0.#})"));
                WorstAt = $"move {s} sample ({samples[i].X:0.###}, {samples[i].Y:0.###}, {heights[i]:0.###}) drop {drop[i]:0.###}; moves {context}";
            }
        }

        return worst;
    }

    public static string WorstAt { get; private set; } = string.Empty;

    public static int CutMoves(Toolpath toolpath) => toolpath.Moves.Count(m => m.Kind != MoveKind.Rapid);
}
